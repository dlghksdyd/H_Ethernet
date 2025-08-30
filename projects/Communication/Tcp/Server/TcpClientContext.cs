using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Communication.Tcp.Server
{
    internal sealed class TcpClientContext(Socket socket)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Socket Socket { get; } = socket ?? throw new ArgumentNullException(nameof(socket));
        public EndPoint? RemoteEndPoint { get; } = socket.RemoteEndPoint;
        public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
        public CancellationTokenSource Cts { get; } = new();
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }
}
