using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Communication.Tcp.Client
{
    public sealed class TcpClientOptions(IPAddress host, int port)
    {
        public IPAddress Host { get; } = host ?? throw new ArgumentNullException();
        public int Port { get; } = port;

        public bool NoDelay { get; set; } = true;
        public int ReceiveBufferSize { get; set; } = 64 * 1024;
        public int SendBufferSize { get; set; } = 64 * 1024;

        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(10);
    }
}
