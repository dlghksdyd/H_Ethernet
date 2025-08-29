using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace H_Ethernet.Client
{
    public sealed class EthernetClientOptions(string host, int port)
    {
        public string Host { get; } = host ?? throw new ArgumentNullException();
        public int Port { get; } = port;

        public bool NoDelay { get; set; } = true;
        public int ReceiveBufferSize { get; set; } = 64 * 1024;
        public int SendBufferSize { get; set; } = 64 * 1024;

        /// <summary>연결 시도 타임아웃.</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
    }
}
