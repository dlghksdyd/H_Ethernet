using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Communication.Tcp.Client;

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

        public ICommand ClientStartCommand => new DelegateCommand(() =>
        {
            var options = new TcpClientOptions(IPAddress.Parse("127.0.0.1"), 10000);
            var client = TcpClientManager.Create(options);
        });
    }
}
