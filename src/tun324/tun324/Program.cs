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

            ArgInfo info = ArgInfo.ParseArg(args);
            if (string.IsNullOrWhiteSpace(info.Proxy))
            {
                LoggerHelper.Instance.Error($"[Args] please set proxy url");
                return;
            }
            if (IPAddress.Any.Equals(info.Address))
            {
                LoggerHelper.Instance.Error($"[Args] please set ip");
                return;
            }

            Tun324DeviceAdapter adapter = new Tun324DeviceAdapter();
            adapter.Initialize(new Tun324TunDeviceCallback());
            adapter.Setup(new Tun324TunDeviceSetupInfo
            {
                Address = info.Address,
                PrefixLength = info.PrefixLength,
                Mtu = info.Mtu,

                Guid = info.Guid,
                Name = info.Name,

                Proxy = info.Proxy
            });
            adapter.AddRoute(info.Routes.ToArray());

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


    sealed class ArgInfo
    {
        /// <summary>
        /// 设备名
        /// </summary>
        public string Name { get; set; } = "tun324";
        /// <summary>
        /// IP地址
        /// </summary>
        public IPAddress Address { get; set; } = IPAddress.Parse("10.18.18.2");
        /// <summary>
        /// 前缀长度
        /// </summary>
        public byte PrefixLength { get; set; } = 24;
        /// <summary>
        /// GUID 仅windows
        /// </summary>
        public Guid Guid { get; set; } = Guid.Parse("2ef1a78e-9579-4214-bbc1-5dc556b59042");

        /// <summary>
        /// MTU
        /// </summary>
        public int Mtu { get; set; } = 1420;

        /// <summary>
        /// 代理地址
        /// </summary>
        public string Proxy { get; set; } = string.Empty;

        /// <summary>
        /// 路由
        /// </summary>
        public List<Tun324TunDeviceRouteItem> Routes { get; set; } = new List<Tun324TunDeviceRouteItem>();


        public static ArgInfo ParseArg(string[] args)
        {
            ArgInfo info = new ArgInfo();
            info.ParseArgInternal(args);
            return info;
        }
        private void ParseArgInternal(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--name":
                        Name = args[++i];
                        break;
                    case "--ip":
                        {
                            (IPAddress ip, byte prefixlength) = ParseCidr(args[++i]);
                            Address = ip;
                            PrefixLength = prefixlength;
                        }
                        break;
                    case "--guid":
                        Guid = Guid.Parse(args[++i]);
                        break;
                    case "--mtu":
                        Mtu = int.Parse(args[++i]);
                        break;
                    case "--proxy":
                        Proxy = args[++i];
                        break;
                    case "--route":
                        {
                            (IPAddress ip, byte prefixlength) = ParseCidr(args[++i]);
                            Routes.Add(new Tun324TunDeviceRouteItem { Address = ip, PrefixLength = prefixlength });
                        }
                        break;
                    default:
                        break;
                }
            }
        }
        private (IPAddress ip, byte prefixlength) ParseCidr(string str)
        {
            try
            {
                string[] arr = str.Split('/');
                if (arr.Length == 1) return (IPAddress.Parse(arr[0]), 24);
                return (IPAddress.Parse(arr[0]), byte.Parse(arr[1]));
            }
            catch (Exception)
            {
            }
            return (IPAddress.Any, 0);
        }
    }
}
