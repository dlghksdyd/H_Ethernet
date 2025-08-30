using System;
using System.Net;

namespace Communication.Tcp.Server
{
    public sealed class TcpServerOptions(IPAddress listenAddress, int port)
    {
        public IPAddress ListenAddress { get; init; } = listenAddress ?? throw new ArgumentNullException(nameof(listenAddress));
        public int Port { get; init; } = port;

        public bool NoDelay { get; set; } = true;
        public int ReceiveBufferSize { get; set; } = 64 * 1024;
        public int SendBufferSize { get; set; } = 64 * 1024;
        public int BackLog { get; set; } = 100;

        public int MaxClients { get; init; } = 1024;
    }
}