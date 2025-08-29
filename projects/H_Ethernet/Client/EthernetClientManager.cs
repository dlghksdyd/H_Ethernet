

using System.Collections.Concurrent;

namespace H_Ethernet.Client
{
    public static class EthernetClientManager
    {
        private static readonly ConcurrentDictionary<Guid, EthernetClient> _clients = new();

        public static EthernetClient Create(EthernetClientOptions options)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));

            var client = EthernetClient.CreateInternal(options);

            if (!_clients.TryAdd(client.Guid, client))
            {
                client.Dispose();
                throw new InvalidOperationException("Failed to register client.");
            }

            return client;
        }

        public static void Dispose(EthernetClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            _ = _clients.TryRemove(client.Guid, out _);
            client.Dispose();
        }
    }
}
