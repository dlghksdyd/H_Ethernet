using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Communication.Tcp.Client
{
    public class TcpClient
    {
        public bool IsDisposed { get; private set; } = false;

        public readonly Guid Guid = Guid.NewGuid();
        
        private readonly TcpClientOptions _options;

        // 내부 소켓과 동시성 제어용 락
        private Socket? _socket;
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _receiveLock = new(1, 1);

        private TcpClient(TcpClientOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        internal static TcpClient CreateInternal(TcpClientOptions options) 
            => new TcpClient(options);

        internal void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            // 소켓을 안전하게 교체 후 종료
            var sock = Interlocked.Exchange(ref _socket, null);
            if (sock is not null)
            {
                try
                {
                    try { sock.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
                    sock.Dispose();
                }
                catch
                {
                    /* ignore */
                }
            }

            // 세마포어 정리: 사용 중일 수 있으므로 non-blocking으로 가능할 때만 Dispose
            static void TryDisposeSemaphore(SemaphoreSlim sem)
            {
                try
                {
                    // 누가 쓰는 중이면 Release 필요할 수 있어 Dispose를 건너뜀
                    if (sem.Wait(0))
                    {
                        sem.Dispose();
                    }
                    else
                    {
                        // 사용 중이면 건너뜀 (ObjectDisposedException 방지)
                    }
                }
                catch
                {
                    /* ignore */
                }
            }

            TryDisposeSemaphore(_sendLock);
            TryDisposeSemaphore(_receiveLock);
            TryDisposeSemaphore(_connectLock);
        }

        private void ThrowIfDisposed()
        {
            if (!IsDisposed) return;
            throw new ObjectDisposedException(nameof(TcpClient));
        }

        public async Task ConnectAsync()
        {
            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                // 기존 소켓이 사실상 살아있으면 리턴
                if (_socket is { } existing)
                {
                    // Connected는 신뢰 낮음 → Poll/Available로 죽음 감지
                    var looksDead = existing.Poll(0, SelectMode.SelectRead) && existing.Available == 0;
                    if (existing.Connected && !looksDead) return; // 살아있으면 리턴

                    // 죽었으면 정리
                    try
                    {
                        existing.Shutdown(SocketShutdown.Both);
                    }
                    catch
                    {
                        /* ignored */
                    }

                    try
                    {
                        existing.Dispose();
                    }
                    catch
                    {
                        /* ignored */
                    }

                    _socket = null;
                }

                // 새 소켓 생성 (IPv4 전용)
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = _options.NoDelay,
                    ReceiveBufferSize = _options.ReceiveBufferSize,
                    SendBufferSize = _options.SendBufferSize,
                };

                using var timeoutCts = _options.ConnectTimeout == Timeout.InfiniteTimeSpan
                    ? new CancellationTokenSource()
                    : new CancellationTokenSource(_options.ConnectTimeout);

                try
                {
                    ThrowIfDisposed();
                    if (timeoutCts.IsCancellationRequested)
                        throw new OperationCanceledException();

                    await socket.ConnectAsync(_options.Host, _options.Port, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    socket?.Dispose();

                    throw new TimeoutException(
                        $"Connect timed out after {_options.ConnectTimeout.TotalMilliseconds:F0} ms to {_options.Host}:{_options.Port}");
                }
                catch
                {
                    socket?.Dispose();
                    throw;
                }

                var old = Interlocked.Exchange(ref _socket, socket); // 원자성 보장
                if (old is not null && !ReferenceEquals(old, socket))
                {
                    try
                    {
                        old.Shutdown(SocketShutdown.Both);
                    }
                    catch
                    {
                        /* ignore */
                    }

                    try
                    {
                        old.Dispose();
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }
            finally
            {
                _connectLock.Release();
            }
        }

        public async Task SendAsync(ReadOnlyMemory<byte> buffer)
        {
            ThrowIfDisposed();

            if (buffer.Length == 0) return;

            // 연결 확인
            var socket = _socket ?? throw new InvalidOperationException("The socket is not connected.");
            if (!socket.Connected)
                throw new InvalidOperationException("The socket is not connected.");

            // 에러 메시지에 목적지 정보를 포함하면 디버깅에 유용
            string? remote = null;
            try { remote = socket.RemoteEndPoint?.ToString(); } catch { /* ignore */ }

            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeoutCts = new CancellationTokenSource();
                if (_options.SendTimeout != Timeout.InfiniteTimeSpan)
                    timeoutCts.CancelAfter(_options.SendTimeout); // 첫 타임아웃 기동

                int totalSent = 0;

                while (totalSent < buffer.Length)
                {
                    ThrowIfDisposed();

                    ReadOnlyMemory<byte> slice = buffer.Slice(totalSent);

                    int sent = await socket.SendAsync(slice, SocketFlags.None, timeoutCts.Token).ConfigureAwait(false);

                    if (sent <= 0)
                        throw new SocketException((int)SocketError.ConnectionReset);

                    totalSent += sent;

                    // 진행이 있었으니 유휴 타임아웃을 재가동 (Idle 기준)
                    if (_options.SendTimeout != Timeout.InfiniteTimeSpan)
                        timeoutCts.CancelAfter(_options.SendTimeout);
                }
            }
            catch (OperationCanceledException oce)
            {
                throw new TimeoutException(
                    $"Send idle-timed out after {_options.SendTimeout.TotalMilliseconds:F0} ms" +
                    (remote is null ? "." : $" to {remote}."), oce);
            }
            catch (ObjectDisposedException ode)
            {
                throw new InvalidOperationException("The socket is already disposed.", ode);
            }
            catch (SocketException)
            {
                // 필요하면 로깅
                throw;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task SendAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers)
        {
            foreach (var b in buffers)
                await SendAsync(b).ConfigureAwait(false);
        }

        public async Task ReceiveAsync(Memory<byte> buffer)
        {
            ThrowIfDisposed();

            if (buffer.Length == 0) return;

            var socket = _socket ?? throw new InvalidOperationException("The socket is not connected.");
            if (!socket.Connected)
                throw new InvalidOperationException("The socket is not connected.");

            string? remote = null;
            try { remote = socket.RemoteEndPoint?.ToString(); } catch { /* ignore */ }

            await _receiveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeoutCts = new CancellationTokenSource();
                if (_options.ReceiveTimeout != Timeout.InfiniteTimeSpan)
                    timeoutCts.CancelAfter(_options.ReceiveTimeout); // 첫 유휴 타이머

                int totalRead = 0;

                while (totalRead < buffer.Length)
                {
                    ThrowIfDisposed();

                    var slice = buffer.Slice(totalRead);

                    int read = await socket.ReceiveAsync(slice, SocketFlags.None, timeoutCts.Token).ConfigureAwait(false);

                    if (read == 0)
                        throw new SocketException((int)SocketError.ConnectionReset); // 원격 종료

                    totalRead += read;

                    // 진행이 있었으니 유휴 타임아웃 재가동
                    if (_options.ReceiveTimeout != Timeout.InfiniteTimeSpan)
                        timeoutCts.CancelAfter(_options.ReceiveTimeout);
                }
            }
            catch (OperationCanceledException oce)
            {
                throw new TimeoutException(
                    $"Receive idle-timed out after {_options.ReceiveTimeout.TotalMilliseconds:F0} ms" +
                    (remote is null ? "." : $" from {remote}."), oce);
            }
            catch (ObjectDisposedException ode)
            {
                throw new InvalidOperationException("The socket is already disposed.", ode);
            }
            catch (SocketException)
            {
                throw;
            }
            finally
            {
                _receiveLock.Release();
            }
        }
    }
}
