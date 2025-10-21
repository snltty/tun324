using System.Net;
using System.Net.Sockets;
using tun324.libs;

namespace tun324.tun.proxy
{
    public sealed class ProxyManager
    {
        private readonly IProxy[] proxys = [new ProxySocks5()];
        private IProxy currentProxy = null;

        public void Parse(string proxy)
        {
            for (int i = 0; i < proxys.Length; i++)
            {
                if (proxys[i].Test(proxy))
                {
                    proxys[i].Parse(proxy);
                    currentProxy = proxys[i];
                    LoggerHelper.Instance.Debug($"[Proxy] current proxy {proxy}->[{currentProxy.Name}]!");
                    return;
                }
            }
        }

        public async Task ConnectAsync(Socket src, IPEndPoint ep)
        {
            if (currentProxy == null)
            {
                src.SafeClose();
                LoggerHelper.Instance.Error($"[Proxy] proxy not found!");
                return;
            }
            await currentProxy.ConnectAsync(src, ep);
        }
    }
}
