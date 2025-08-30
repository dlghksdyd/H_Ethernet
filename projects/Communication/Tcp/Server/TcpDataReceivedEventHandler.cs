using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Communication.Tcp.Server
{
    public delegate void TcpDataReceivedEventHandler(TcpDataReceivedContext context);

    public sealed class TcpDataReceivedContext(TcpServer tcpServer, Guid clientId, byte[] data)
    {
        public readonly byte[] Data = data;

        public async Task<bool> ReplyAsync(byte[] data)
            => await tcpServer.SendAsync(clientId, data);
    }
}
