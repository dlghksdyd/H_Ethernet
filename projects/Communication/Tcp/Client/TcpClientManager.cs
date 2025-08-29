using System.Collections.Concurrent;

namespace Communication.Tcp.Client
{
    public static class TcpClientManager
    {
        private static readonly ConcurrentDictionary<Guid, TcpClient> _clients = new();

        public static TcpClient Create(TcpClientOptions options)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));

            var client = TcpClient.CreateInternal(options);

            if (!_clients.TryAdd(client.Guid, client))
            {
                client.Dispose();
                throw new InvalidOperationException("Failed to register client.");
            }

            return client;
        }

        public static void Dispose(TcpClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            _ = _clients.TryRemove(client.Guid, out _);
            client.Dispose();
        }
    }
}
