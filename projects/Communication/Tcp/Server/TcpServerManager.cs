using System;
using System.Collections.Concurrent;

namespace Communication.Tcp.Server
{
    public static class TcpServerManager
    {
        private static readonly ConcurrentDictionary<Guid, TcpServer> _servers = new();

        public static TcpServer Create(TcpServerOptions options)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));

            var server = TcpServer.CreateInternal(options);

            if (!_servers.TryAdd(server.Guid, server))
            {
                server.Dispose();
                throw new InvalidOperationException("Failed to register server.");
            }

            return server;
        }

        public static void Dispose(TcpServer server)
        {
            if (server is null) throw new ArgumentNullException(nameof(server));

            _ = _servers.TryRemove(server.Guid, out _);
            server.Dispose();
        }
    }
}