using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Communication.Tcp.Server
{
    /// <summary>
    /// 비동기 TCP 서버. 연결/해제/수신 이벤트 제공, 개별 송신/브로드캐스트 지원.
    /// 클라이언트의 구조/패턴과 대칭되도록 구성됨.
    /// </summary>
    public sealed class TcpServer
    {
        public bool IsDisposed { get; private set; }

        public readonly Guid Guid = Guid.NewGuid();

        private readonly TcpServerOptions _options;

        // 리스너 및 제어
        private Socket? _listener;

        // 시작/중지 동시성 제어
        private readonly SemaphoreSlim _startStopLock = new(1, 1);

        // Accept 루프 제어
        private CancellationTokenSource? _acceptCts;
        private Task? _acceptTask;

        // 운용 정보 제공
        public int ConnectedCount => _clients.Count;
        public (Guid Id, EndPoint? Remote)[] GetClientsSnapshot()
            => _clients.Values.Select(c => (c.Id, c.RemoteEndPoint)).ToArray();

        // 클라이언트 관리
        private sealed class ClientContext(Socket socket)
        {
            public Guid Id { get; } = Guid.NewGuid();
            public Socket Socket { get; } = socket ?? throw new ArgumentNullException(nameof(socket));
            public EndPoint? RemoteEndPoint { get; } = socket.RemoteEndPoint;
            public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
            public CancellationTokenSource Cts { get; } = new();
            public SemaphoreSlim SendLock { get; } = new(1, 1);   // ← 추가
        }

        private readonly ConcurrentDictionary<Guid, ClientContext> _clients = new();

        #region Events

        private TcpClientConnection ToPublicClient(ClientContext ctx)
            => new TcpClientConnection(this, ctx.Id, ctx.RemoteEndPoint, ctx.ConnectedAtUtc);

        public event EventHandler<DataReceivedEventArgs>? DataReceived;
        public event EventHandler<ClientEventArgs>? ClientConnected;
        public event EventHandler<ClientEventArgs>? ClientDisconnected;

        private void RaiseDataReceived(ClientContext ctx, byte[] data)
        {
            try
            {
                var client = ToPublicClient(ctx);
                DataReceived?.Invoke(this, new DataReceivedEventArgs(client, data));
            }
            catch { /* ignore */ }
        }

        private void RaiseClientConnected(ClientContext ctx)
        {
            try
            {
                var client = ToPublicClient(ctx);
                ClientConnected?.Invoke(this, new ClientEventArgs(client));
            } catch { /* ignored */ }
        }
        private void RaiseClientDisconnected(ClientContext ctx)
        {
            try
            {
                var client = ToPublicClient(ctx);
                ClientDisconnected?.Invoke(this, new ClientEventArgs(client));
            } catch { /* ignored */ }
        }

        #endregion

        #region Log

        public Action<Exception, string>? LogError { get; init; }
        private void Log(Exception ex, string msg)
        {
            try { LogError?.Invoke(ex, msg); } catch { /* ignored */ }
        }

        #endregion

        private TcpServer(TcpServerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            ValidateOptions();
        }

        private void ValidateOptions()
        {
            if (_options.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(_options.Port));
            if (_options.BackLog <= 0) throw new ArgumentOutOfRangeException(nameof(_options.BackLog));
            if (_options.ReceiveBufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(_options.ReceiveBufferSize));
            if (_options.SendBufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(_options.SendBufferSize));
            if (_options.MaxClients <= 0) throw new ArgumentOutOfRangeException(nameof(_options.MaxClients));
        }

        internal static TcpServer CreateInternal(TcpServerOptions options)
            => new TcpServer(options);

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            try { StopAsync().GetAwaiter().GetResult(); } catch { /* ignored */ }
            try { _acceptCts?.Dispose(); } catch { /* ignored */ }
            try { _startStopLock.Dispose(); } catch { /* ignored */ }

            foreach (var kv in _clients)
            {
                if (_clients.TryRemove(kv.Key, out var ctx))
                {
                    try { ctx.Cts.Cancel(); } catch { /* ignored */ }
                    try { ctx.Cts.Dispose(); } catch { /* ignored */ }
                    try { ctx.SendLock.Dispose(); } catch { /* ignored */ }
                    CloseSocketSafe(ctx.Socket);
                }
            }

            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(TcpServer));
        }

        public async Task StartAsync()
        {
            await _startStopLock.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                if (_listener is not null)
                    return; // 이미 시작됨

                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = _options.NoDelay,
                    ReceiveBufferSize = _options.ReceiveBufferSize,
                    SendBufferSize = _options.SendBufferSize,
                };

                try
                {
                    // 빠른 재시작 허용
                    sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                    // 소켓을 지정된 IP 주소와 포트에 바인딩
                    sock.Bind(new IPEndPoint(_options.ListenAddress, _options.Port));

                    // 클라이언트 연결을 대기 시작
                    sock.Listen(_options.BackLog);

                    // 상태 저장 & Accept 루프 시작
                    _listener = sock;
                    _acceptCts = new CancellationTokenSource();
                    _acceptTask = AcceptLoopAsync(_acceptCts.Token);
                }
                catch
                {
                    try { sock.Dispose(); } catch { /* ignore */ }
                    throw;
                }
            }
            finally
            {
                _startStopLock.Release();
            }
        }

        public async Task StopAsync()
        {
            await _startStopLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var listener = _listener;
                if (listener is null)
                    return; // 이미 중지됨

                // Accept 루프 취소
                _acceptCts?.Cancel();

                // 리스너 닫기
                CloseSocketSafe(listener);
                _listener = null;

                // Accept 루프 종료 대기
                if (_acceptTask is not null)
                {
                    try { await _acceptTask.ConfigureAwait(false); }
                    catch { /* ignore */ }
                    _acceptTask = null;
                }

                // 모든 클라이언트 종료
                foreach (var kv in _clients)
                {
                    if (_clients.TryRemove(kv.Key, out var ctx))
                        CloseClient(ctx);
                }
            }
            finally
            {
                _startStopLock.Release();
                _acceptCts?.Dispose();
            }
        }

        public bool Disconnect(Guid clientId)
        {
            if (_clients.TryRemove(clientId, out var ctx))
            {
                CloseClient(ctx);
                RaiseClientDisconnected(ctx);
                return true;
            }
            return false;
        }

        public async Task<bool> SendAsync(Guid clientId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (data.Length == 0) return true;
            if (!_clients.TryGetValue(clientId, out var ctx)) return false;

            // 연결 CTS와 호출자 CT를 링크해서 CloseClient의 Cancel을 즉시 반영
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, ctx.Cts.Token);
            var token = linkedCts.Token;

            await ctx.SendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var sock = ctx.Socket;
                int offset = 0;
                while (offset < data.Length)
                {
                    int sent = await sock.SendAsync(data.Slice(offset), SocketFlags.None, token).ConfigureAwait(false);
                    if (sent <= 0) throw new SocketException((int)SocketError.ConnectionReset);
                    offset += sent;
                }
                return true;
            }
            catch (SocketException ex)
            {
                Log(ex, $"Send socket error ({ex.SocketErrorCode})");
                try { ctx.Cts.Cancel(); } catch { /* ignored */ }
                return false;
            }
            catch (OperationCanceledException)
            {
                // CloseClient에서 Cancel되었거나 외부 CT 취소
                return false;
            }
            catch (Exception ex)
            {
                Log(ex, "Send unexpected");
                try { ctx.Cts.Cancel(); } catch { /* ignored */ }
                return false;
            }
            finally
            {
                ctx.SendLock.Release();
            }
        }

        public async Task<int> BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (data.Length == 0) return 0;

            var targets = _clients.Values.ToArray(); // snapshot
            var tasks = new Task<bool>[targets.Length];
            for (int i = 0; i < targets.Length; i++)
                tasks[i] = SendAsync(targets[i].Id, data, ct);

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.Count(r => r);
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            var listener = _listener!;
            while (!token.IsCancellationRequested)
            {
                Socket clientSock;
                try
                {
                    // 최대 클라이언트 수 제한
                    if (_clients.Count >= _options.MaxClients)
                    {
                        await Task.Delay(100, token).ConfigureAwait(false); // backoff
                        continue;
                    }

                    clientSock = await listener.AcceptAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    Log(ex, $"AcceptLoop: {ex.Message}");
                    break;
                }
                catch (ObjectDisposedException ex)
                {
                    Log(ex, $"AcceptLoop: {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    Log(ex, "AcceptLoop: unexpected exception");

                    // 딜레이를 줘서 Busy waiting 방지
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    // TCP KeepAlive 활성화
                    clientSock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                    clientSock.NoDelay = _options.NoDelay;
                    clientSock.ReceiveBufferSize = _options.ReceiveBufferSize;
                    clientSock.SendBufferSize = _options.SendBufferSize;

                    var ctx = new ClientContext(clientSock);

                    if (!_clients.TryAdd(ctx.Id, ctx))
                    {
                        // 등록 실패 시 바로 닫기
                        CloseSocketSafe(clientSock);
                        continue;
                    }

                    RaiseClientConnected(ctx);
                    _ = HandleClientAsync(ctx, token);
                }
                catch (Exception ex)
                {
                    Log(ex, "AcceptLoop: unexpected exception");

                    CloseSocketSafe(clientSock);
                }
            }
        }

        private async Task HandleClientAsync(ClientContext ctx, CancellationToken serverToken)
        {
            var sock = ctx.Socket;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken, ctx.Cts.Token);
            var token = linkedCts.Token;

            // 고정 크기 버퍼(풀링/파이프 등은 필요 시 확장)
            var buffer = new byte[_options.ReceiveBufferSize > 0 ? Math.Min(_options.ReceiveBufferSize, 128 * 1024) : 8192];
            if (buffer.Length == 0) buffer = new byte[8192];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    int received = await sock.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
                    if (received <= 0) break;

                    // 이벤트로 전달할 때는 메시지 수명 문제 방지를 위해 복사본 사용
                    var copy = new byte[received];
                    Buffer.BlockCopy(buffer, 0, copy, 0, received);

                    RaiseDataReceived(ctx, copy);
                }
            }
            catch (OperationCanceledException) { /* 정상 취소 */ }
            catch (ObjectDisposedException) { /* 소켓 종료 */ }
            catch (SocketException ex)
            {
                Log(ex, $"Receive socket error ({ex.SocketErrorCode})");
            }
            catch (Exception ex)
            {
                Log(ex, "Receive unexpected");
            }
            finally
            {
                // 딕셔너리에서 제거 & 연결 종료 알림
                if (_clients.TryRemove(ctx.Id, out _))
                {
                    CloseClient(ctx);
                    RaiseClientDisconnected(ctx);
                }
            }
        }

        private void CloseClient(ClientContext ctx)
        {
            // 모든 작업 취소 신호
            try { ctx.Cts.Cancel(); } catch { /* ignored */ }

            // 보내기 임계구역이 끝나길 잠깐 기다렸다가(최대 1초) 락 확보 후 Dispose
            // - 확보 실패 시 Dispose 생략 (레이스 방지 우선)
            bool sendLockAcquired = false;
            try
            {
                sendLockAcquired = ctx.SendLock.Wait(TimeSpan.FromSeconds(1));
            }
            catch { /* ignore */ }
            finally
            {
                if (sendLockAcquired)
                {
                    try { ctx.SendLock.Dispose(); } catch { /* ignored */ }
                }
                // 확보 실패 시 Dispose 하지 않음
            }

            // 소켓 종료
            CloseSocketSafe(ctx.Socket);

            // CTS 정리
            try { ctx.Cts.Dispose(); } catch { /* ignored */ }
        }

        private static void CloseSocketSafe(Socket? s)
        {
            if (s == null) return;
            try { s.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
            try { s.Dispose(); } catch { /* ignore */ }
        }
    }

    public sealed class ClientEventArgs(TcpClientConnection client) : EventArgs
    {
        public Guid ClientId { get; } = client.Id;
        public EndPoint? RemoteEndPoint { get; } = client.RemoteEndPoint;
    }

    public sealed class DataReceivedEventArgs : EventArgs
    {
        internal DataReceivedEventArgs(TcpClientConnection client, byte[] data)
        {
            Client = client;
            Data = data;
        }

        public TcpClientConnection Client { get; }
        public byte[] Data { get; }

        // 편의 메서드: 바로 응답
        public Task<bool> ReplyAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
            => Client.SendAsync(data, ct);
    }
}
