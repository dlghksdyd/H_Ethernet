using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Communication.Tcp.Server
{
    /// <summary>
    /// 비동기 TCP 서버. 연결/해제/수신 이벤트 제공, 개별 송신/브로드캐스트 지원.
    /// 클라이언트의 구조/패턴과 대칭되도록 구성됨.
    /// </summary>
    public sealed class TcpServer
    {
        public bool IsDisposed { get; private set; } = false;

        public readonly Guid Guid = Guid.NewGuid();

        private readonly TcpServerOptions _options;

        // 리스너 및 제어
        private Socket? _listener;
        private CancellationTokenSource? _acceptCts;
        private Task? _acceptTask;

        // 시작/중지 동시성 제어
        private readonly SemaphoreSlim _startStopLock = new(1, 1);

        // 세션들
        private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();

        // 이벤트
        public event Action<Guid, EndPoint>? ClientConnected;
        public event Action<Guid, EndPoint, SocketError>? ClientDisconnected;
        public event Action<Guid, ReadOnlyMemory<byte>>? DataReceived;

        private TcpServer(TcpServerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        internal static TcpServer CreateInternal(TcpServerOptions options)
            => new TcpServer(options);

        internal void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            // 수락 루프 취소
            try { _acceptCts?.Cancel(); } catch { /* ignore */ }

            // 리스너 소켓 교체 후 종료
            var listener = Interlocked.Exchange(ref _listener, null);
            if (listener is not null)
            {
                try
                {
                    try { listener.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
                    listener.Dispose();
                }
                catch { /* ignore */ }
            }

            // 세션 전부 정리(강제)
            foreach (var kv in _sessions)
            {
                try { kv.Value.DisposeHard(); } catch { /* ignore */ }
            }
            _sessions.Clear();

            // 수락 태스크 합류 시도
            try { _acceptTask?.Wait(TimeSpan.FromMilliseconds(100)); } catch { /* ignore */ }
            _acceptTask = null;

            // 세마포어 정리(클라이언트 패턴과 동일)
            static void TryDisposeSemaphore(SemaphoreSlim sem)
            {
                try
                {
                    if (sem.Wait(0))
                        sem.Dispose();
                }
                catch { /* ignore */ }
            }
            TryDisposeSemaphore(_startStopLock);

            try { _acceptCts?.Dispose(); } catch { /* ignore */ }
            _acceptCts = null;
        }

        private void ThrowIfDisposed()
        {
            if (!IsDisposed) return;
            throw new ObjectDisposedException(nameof(TcpServer));
        }

        /// <summary>서버 시작</summary>
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
                    sock.Bind(new IPEndPoint(_options.ListenAddress, _options.Port));
                    sock.Listen(_options.Backlog);
                }
                catch
                {
                    try { sock.Dispose(); } catch { /* ignore */ }
                    throw;
                }

                _listener = sock;
                _acceptCts = new CancellationTokenSource();
                _acceptTask = Task.Run(() => AcceptLoopAsync(_acceptCts.Token));
            }
            finally
            {
                _startStopLock.Release();
            }
        }

        /// <summary>서버 정지(세션을 순차 종료하고 대기)</summary>
        public async Task StopAsync()
        {
            await _startStopLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_listener is null)
                    return;

                // 수락 중단
                try { _acceptCts?.Cancel(); } catch { /* ignore */ }

                var listener = Interlocked.Exchange(ref _listener, null);
                if (listener is not null)
                {
                    try
                    {
                        try { listener.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
                        listener.Dispose();
                    }
                    catch { /* ignore */ }
                }

                // 세션 순차 종료
                var tasks = new Task[_sessions.Count];
                int i = 0;
                foreach (var kv in _sessions)
                {
                    tasks[i++] = kv.Value.CloseAsync(SocketError.Success);
                }

                var drain = Task.WhenAll(tasks);
                var timeout = Task.Delay(_options.StopDrainTimeout);
                await Task.WhenAny(drain, timeout).ConfigureAwait(false);

                // 남은 건 강제 종료
                foreach (var kv in _sessions)
                {
                    try { kv.Value.DisposeHard(); } catch { /* ignore */ }
                }
                _sessions.Clear();

                // 수락 루프 합류
                if (_acceptTask is not null)
                {
                    try { await _acceptTask.ConfigureAwait(false); } catch { /* ignore */ }
                    _acceptTask = null;
                }
            }
            finally
            {
                try { _acceptCts?.Dispose(); } catch { /* ignore */ }
                _acceptCts = null;

                _startStopLock.Release();
            }
        }

        public int ConnectionCount => _sessions.Count;

        public bool TryGetRemoteEndPoint(Guid clientId, out EndPoint? endPoint)
        {
            if (_sessions.TryGetValue(clientId, out var s))
            {
                try
                {
                    endPoint = s.Socket.RemoteEndPoint;
                    return true;
                }
                catch { /* ignore */ }
            }

            endPoint = null;
            return false;
        }

        /// <summary>특정 클라이언트로 송신</summary>
        public Task<bool> SendAsync(Guid clientId, ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return Task.FromResult(true);
            if (!_sessions.TryGetValue(clientId, out var s)) return Task.FromResult(false);
            return s.SendAsync(buffer, ct);
        }

        /// <summary>브로드캐스트(성공한 세션 수 반환)</summary>
        public async Task<int> BroadcastAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            int ok = 0;
            foreach (var kv in _sessions)
            {
                if (await kv.Value.SendAsync(buffer, ct).ConfigureAwait(false))
                    ok++;
            }
            return ok;
        }

        /// <summary>강제 연결 해제</summary>
        public Task<bool> DisconnectAsync(Guid clientId, SocketError reason = SocketError.ConnectionAborted)
        {
            if (!_sessions.TryGetValue(clientId, out var s)) return Task.FromResult(false);
            return s.CloseAsync(reason).ContinueWith(_ => true);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            var listener = _listener!;
            while (!ct.IsCancellationRequested)
            {
                Socket? socket = null;
                try
                {
                    socket = await listener.AcceptAsync(ct).ConfigureAwait(false);

                    if (_sessions.Count >= _options.MaxConnections)
                    {
                        try { socket.Close(); } catch { /* ignore */ }
                        continue;
                    }

                    ConfigureAcceptedSocket(socket);

                    var session = new ClientSession(this, socket, _options);
                    if (_sessions.TryAdd(session.Id, session))
                    {
                        try { ClientConnected?.Invoke(session.Id, socket.RemoteEndPoint!); } catch { /* ignore */ }
                        _ = session.RunReceiveLoopAsync(); // fire-and-forget
                    }
                    else
                    {
                        await session.CloseAsync(SocketError.AccessDenied).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch
                {
                    try { socket?.Dispose(); } catch { /* ignore */ }
                    // 계속 루프
                }
            }
        }

        private void ConfigureAcceptedSocket(Socket socket)
        {
            try
            {
                socket.NoDelay = _options.NoDelay;
                socket.ReceiveBufferSize = _options.ReceiveBufferSize;
                socket.SendBufferSize = _options.SendBufferSize;
            }
            catch { /* ignore */ }
        }

        private void OnSessionClosed(ClientSession session, SocketError reason)
        {
            _sessions.TryRemove(session.Id, out _);
            try { ClientDisconnected?.Invoke(session.Id, session.RemoteEndPoint, reason); } catch { /* ignore */ }
        }

        internal void OnSessionData(Guid id, ReadOnlyMemory<byte> data)
        {
            try { DataReceived?.Invoke(id, data); } catch { /* ignore */ }
        }

        // ===========================
        // Per-connection session
        // ===========================
        private sealed class ClientSession
        {
            private readonly TcpServer _owner;
            private readonly TcpServerOptions _opts;
            private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;

            private readonly SemaphoreSlim _sendLock = new(1, 1);

            private volatile bool _closing;

            public Guid Id { get; } = Guid.NewGuid();
            public Socket Socket { get; }
            public EndPoint RemoteEndPoint => Socket.RemoteEndPoint!;

            public ClientSession(TcpServer owner, Socket socket, TcpServerOptions options)
            {
                _owner = owner;
                Socket = socket;
                _opts = options;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static int ReadInt32BE(ReadOnlySpan<byte> span)
            {
                return (span[0] << 24) | (span[1] << 16) | (span[2] << 8) | span[3];
            }

            public async Task RunReceiveLoopAsync()
            {
                SocketError closeReason = SocketError.Success;

                if (!_opts.UseLengthPrefixFraming)
                {
                    var buffer = _pool.Rent(_opts.ReceiveBufferSize);
                    try
                    {
                        while (true)
                        {
                            int received = await Socket.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
                            if (received == 0) { closeReason = SocketError.ConnectionReset; break; }
                            _owner.OnSessionData(Id, new ReadOnlyMemory<byte>(buffer, 0, received));
                        }
                    }
                    catch (SocketException se) { closeReason = se.SocketErrorCode; }
                    catch (ObjectDisposedException) { closeReason = SocketError.OperationAborted; }
                    catch { closeReason = SocketError.SocketError; }
                    finally
                    {
                        _pool.Return(buffer);
                        await CloseAsync(closeReason).ConfigureAwait(false);
                    }
                }
                else
                {
                    byte[] header = _pool.Rent(4);
                    byte[] payload = Array.Empty<byte>();

                    try
                    {
                        while (true)
                        {
                            if (!await ReceiveExactAsync(header, 4).ConfigureAwait(false))
                            { closeReason = SocketError.ConnectionReset; break; }

                            int len = ReadInt32BE(header);
                            if (len < 0 || len > _opts.MaxFrameLength)
                            { closeReason = SocketError.MessageSize; break; }

                            if (payload.Length < len)
                                payload = new byte[len];

                            if (!await ReceiveExactAsync(payload, len).ConfigureAwait(false))
                            { closeReason = SocketError.ConnectionReset; break; }

                            _owner.OnSessionData(Id, new ReadOnlyMemory<byte>(payload, 0, len));
                        }
                    }
                    catch (SocketException se) { closeReason = se.SocketErrorCode; }
                    catch (ObjectDisposedException) { closeReason = SocketError.OperationAborted; }
                    catch { closeReason = SocketError.SocketError; }
                    finally
                    {
                        _pool.Return(header);
                        await CloseAsync(closeReason).ConfigureAwait(false);
                    }
                }
            }

            private async Task<bool> ReceiveExactAsync(byte[] buffer, int size)
            {
                int off = 0;
                while (off < size)
                {
                    int n = await Socket.ReceiveAsync(buffer.AsMemory(off, size - off), SocketFlags.None).ConfigureAwait(false);
                    if (n == 0) return false;
                    off += n;
                }
                return true;
            }

            public async Task<bool> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
            {
                if (_closing) return false;
                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!Socket.Connected) return false;

                    if (_opts.UseLengthPrefixFraming)
                    {
                        int len = buffer.Length;
                        var hdr = new byte[4];              // <-- byte[] 사용
                        hdr[0] = (byte)((len >> 24) & 0xFF);
                        hdr[1] = (byte)((len >> 16) & 0xFF);
                        hdr[2] = (byte)((len >> 8) & 0xFF);
                        hdr[3] = (byte)(len & 0xFF);

                        await SendAllAsync(hdr, ct).ConfigureAwait(false);   // ReadOnlyMemory 오버로드 호출
                        await SendAllAsync(buffer, ct).ConfigureAwait(false);
                        return true;
                    }
                    else
                    {
                        await SendAllAsync(buffer, ct).ConfigureAwait(false);
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            
            private async ValueTask SendAllAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
            {
                int sent = 0;
                while (sent < data.Length)
                {
                    int n = await Socket.SendAsync(data.Slice(sent), SocketFlags.None, ct).ConfigureAwait(false);
                    if (n == 0) throw new SocketException((int)SocketError.ConnectionReset);
                    sent += n;
                }
            }

            public async Task CloseAsync(SocketError reason)
            {
                if (_closing) return;
                _closing = true;

                try
                {
                    try { Socket.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
                    try { Socket.Close(); } catch { /* ignore */ }
                }
                finally
                {
                    try { Socket.Dispose(); } catch { /* ignore */ }
                    _owner.OnSessionClosed(this, reason);

                    // sendLock 해제 보장
                    try { await _sendLock.WaitAsync(0).ConfigureAwait(false); } catch { /* ignore */ }
                    _sendLock.Dispose();
                }
            }

            /// <summary>Stop 타임아웃 경과 등에서 마지막 강제 종료용</summary>
            public void DisposeHard()
            {
                try
                {
                    try { Socket.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
                    Socket.Dispose();
                }
                catch { /* ignore */ }
                finally
                {
                    _owner.OnSessionClosed(this, SocketError.OperationAborted);
                    try { _sendLock.Dispose(); } catch { /* ignore */ }
                }
            }
        }
    }
}
