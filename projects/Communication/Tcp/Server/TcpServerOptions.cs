using System;
using System.Net;

namespace Communication.Tcp.Server
{
    public sealed class TcpServerOptions(IPAddress listenAddress, int port)
    {
        public IPAddress ListenAddress { get; } = listenAddress ?? throw new ArgumentNullException(nameof(listenAddress));
        public int Port { get; } = port;

        public bool NoDelay { get; set; } = true;
        public int ReceiveBufferSize { get; set; } = 64 * 1024;
        public int SendBufferSize { get; set; } = 64 * 1024;

        /// <summary>수신 이벤트를 원시 스트림 조각 단위로 전달할지(true) 아니면 길이 프레임(TLV 등) 없이 조각 그대로 전달.</summary>
        public bool UseLengthPrefixFraming { get; set; } = false;

        /// <summary>프레이밍을 쓰는 경우 허용 최대 길이(바이트). 기본 256MB.</summary>
        public int MaxFrameLength { get; set; } = 256 * 1024 * 1024;

        public int Backlog { get; set; } = 512;
        public int MaxConnections { get; set; } = 10_000;

        /// <summary>Stop 시 세션 종료 대기(하드 타임아웃)</summary>
        public TimeSpan StopDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);
    }
}