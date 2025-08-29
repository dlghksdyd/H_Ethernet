using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace H_Ethernet.Client
{
    public class EthernetClient
    {
        private bool _isDisposed = false;
        public bool IsDisposed => _isDisposed;

        public readonly Guid Guid = Guid.NewGuid();
        
        private readonly EthernetClientOptions _options;

        private EthernetClient(EthernetClientOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        internal static EthernetClient CreateInternal(EthernetClientOptions options) 
            => new EthernetClient(options);

        internal void Dispose()
        {
            if (_isDisposed) return; // 이미 정리됨
        }
    }
}
