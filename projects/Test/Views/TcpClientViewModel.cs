using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Communication.Tcp.Client;
using Communication.Tcp.Server;
using DataReceivedEventArgs = Communication.Tcp.Server.DataReceivedEventArgs;

namespace Test.Views
{
    public class TcpClientViewModel : BindableBase
    {
        private string _title = string.Empty;
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        private TcpClient _client;

        public ICommand ClientStartCommand => new DelegateCommand(() =>
        {
            var options = new TcpClientOptions(IPAddress.Parse("127.0.0.1"), 10000);
            _client = TcpClientManager.Create(options);
            _ = _client.ConnectAsync();
        });

        public ICommand ClientSendDataCommand => new DelegateCommand(() =>
        {
            List<byte> data = new List<byte>();
            data.Add(40);
            _ = _client.SendAsync(data.ToArray());
        });

        public ICommand ServerStartCommand => new DelegateCommand(() =>
        {
            var options = new TcpServerOptions(IPAddress.Parse("0.0.0.0"), 10000);
            var server = TcpServerManager.Create(options);
            server.DataReceived += Server_DataReceived;
            _ = server.StartAsync();
        });

        private void Server_DataReceived(object? sender, DataReceivedEventArgs e)
        {
            Debug.WriteLine($"수신받은 데이터: {e.Data[0]}");
        }
    }
}
