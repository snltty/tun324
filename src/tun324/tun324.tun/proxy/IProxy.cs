
using System.Net;
using System.Net.Sockets;

namespace tun324.tun.proxy
{
    public interface IProxy
    {
        public string Name { get; }

        public bool Test(string proxy);
        public void Parse(string proxy);

        public Task ConnectAsync(Socket src, IPEndPoint ep);
    }
}
