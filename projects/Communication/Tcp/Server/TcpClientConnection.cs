using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Communication.Tcp.Server
{
    public sealed class TcpClientConnection
    {
        private readonly TcpServer _server;

        internal TcpClientConnection(TcpServer server, Guid id, EndPoint? remote, DateTime connectedAtUtc)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            Id = id;
            RemoteEndPoint = remote;
            ConnectedAtUtc = connectedAtUtc;
        }

        public Guid Id { get; }
        public EndPoint? RemoteEndPoint { get; }
        public DateTime ConnectedAtUtc { get; }

        public Task<bool> SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
            => _server.SendAsync(Id, data, ct);

        public bool Disconnect() => _server.Disconnect(Id);

        public override string ToString() => $"{Id} ({RemoteEndPoint})";
    }
}
