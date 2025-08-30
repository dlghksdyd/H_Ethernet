using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Communication.Tcp.Client;
using Communication.Tcp.Server;

namespace Test.Views
{
    public class TcpClientViewModel : BindableBase
    {
        private TcpClient? _client;

        public ICommand ClientStartCommand => new DelegateCommand(() =>
        {
            var options = new TcpClientOptions(IPAddress.Parse("127.0.0.1"), 10000);
            _client = TcpClientManager.Create(options);
            _ = _client.ConnectAsync();
        });

        public ICommand ClientSendDataCommand => new DelegateCommand(() =>
        {
            var data = new List<byte> { 40 };
            _ = _client?.SendAsync(data.ToArray());
        });

        public ICommand ServerStartCommand => new DelegateCommand(() =>
        {
            var options = new TcpServerOptions(IPAddress.Parse("0.0.0.0"), 10000);
            var server = TcpServerManager.Create(options);
            server.DataReceived += (context) =>
            {
                Debug.WriteLine($"수신받은 데이터: {context.Data[0]}");
            };
            _ = server.StartAsync();
        });
    }
}
