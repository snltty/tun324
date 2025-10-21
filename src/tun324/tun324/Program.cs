using System.Net;
using tun324.libs;
using tun324.tun;
using tun324.tun.device;

namespace tun324
{
    internal class Program
    {
        static void Main(string[] args)
        {
            LoggerConsole();

            Tun324DeviceAdapter adapter = new Tun324DeviceAdapter();
            adapter.Initialize(new Tun324TunDeviceCallback());
            adapter.Setup(new Tun324TunDeviceSetupInfo
            {
                Address = IPAddress.Parse("172.18.18.2"),
                PrefixLength = 24,
                Mtu = 65535,

                Guid = Guid.Parse("2ef1a78e-9579-4214-bbc1-5dc556b59042"),
                Name = "tun324",

                Proxy = "socks5://172.25.16.239:12345"
            });
            adapter.AddRoute(new[] {
                new Tun324TunDeviceRouteItem { Address = IPAddress.Parse("169.254.86.78"), PrefixLength = 32 }
            });

            Console.ReadLine();
        }

        private static void LoggerConsole()
        {
            LoggerHelper.Instance.OnLogger += (model) =>
            {
                ConsoleColor currentForeColor = Console.ForegroundColor;
                switch (model.Type)
                {
                    case LoggerTypes.DEBUG:
                        Console.ForegroundColor = ConsoleColor.Blue;
                        break;
                    case LoggerTypes.INFO:
                        Console.ForegroundColor = ConsoleColor.White;
                        break;
                    case LoggerTypes.WARNING:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        break;
                    case LoggerTypes.ERROR:
                        Console.ForegroundColor = ConsoleColor.Red;
                        break;
                    default:
                        break;
                }
                string line = $"[{model.Type,-7}][{model.Time:yyyy-MM-dd HH:mm:ss}]:{model.Content}";
                Console.WriteLine(line);
                Console.ForegroundColor = currentForeColor;
            };
        }
    }

    public sealed class Tun324TunDeviceCallback : ITun324TunDeviceCallback
    {
        public async Task Callback(Tun324TunDevicPacket packet)
        {
            await Task.CompletedTask;
        }
    }
}
