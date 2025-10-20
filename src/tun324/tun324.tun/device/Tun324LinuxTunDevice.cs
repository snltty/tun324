using Microsoft.Win32.SafeHandles;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using tun324.libs;

namespace tun324.tun.device
{
    internal sealed class Tun324LinuxTunDevice : ITun324Device
    {

        private string name = string.Empty;
        public string Name => name;
        public bool Running => safeFileHandle != null;

        private string interfaceLinux = string.Empty;
        private FileStream fsRead = null;
        private FileStream fsWrite = null;
        private SafeFileHandle safeFileHandle;
        private IPAddress address;
        private byte prefixLength = 24;

        public Tun324LinuxTunDevice()
        {
        }

        public bool Setup(LinkerTunDeviceSetupInfo info, out string error)
        {
            error = string.Empty;

            name = info.Name;
            address = info.Address;
            prefixLength = info.PrefixLength;

            if (Running)
            {
                error = $"Adapter already exists";
                return false;
            }
            if (Create(out error) == false)
            {
                return false;
            }
            if (Open(out error) == false)
            {
                Shutdown();
                return false;
            }
            fsRead = new FileStream(safeFileHandle, FileAccess.Read, 65 * 1024, true);
            fsWrite = new FileStream(safeFileHandle, FileAccess.Write, 65 * 1024, true);

            interfaceLinux = GetLinuxInterfaceNum();
            return true;
        }
        private bool Create(out string error)
        {
            error = string.Empty;

            //byte[] ipv6 = IPAddress.Parse("fe80::1818:1818:1818:1818").GetAddressBytes();
            //address.GetAddressBytes().CopyTo(ipv6, ipv6.Length - 4);

            CommandHelper.Linux(string.Empty, new string[] {
                $"ip tuntap add mode tun dev {Name}",
                $"ip addr add {address}/{prefixLength} dev {Name}",
                //$"ip addr add {new IPAddress(ipv6)}/64 dev {Name}",
                $"ip link set dev {Name} up"
            });

            string str = CommandHelper.Linux(string.Empty, new string[] { $"ifconfig" });
            if (str.Contains(Name) == false)
            {
                CommandHelper.Linux(string.Empty, new string[] { $"ip tuntap add mode tun dev {Name}" }, out error);
                return false;
            }

            return true;
        }
        private bool Open(out string error)
        {
            error = string.Empty;

            SafeFileHandle _safeFileHandle = File.OpenHandle("/dev/net/tun", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
            if (_safeFileHandle.IsInvalid)
            {
                _safeFileHandle?.Dispose();
                Shutdown();
                error = $"open file /dev/net/tun fail {Marshal.GetLastWin32Error()}";
                return false;
            }

            int ioctl = LinuxAPI.Ioctl(Name, _safeFileHandle, 1074025674);
            if (ioctl != 0)
            {
                _safeFileHandle?.Dispose();
                Shutdown();
                error = $"Ioctl fail : {ioctl}，{Marshal.GetLastWin32Error()}";
                return false;
            }
            safeFileHandle = _safeFileHandle;

            return true;
        }

        public void Shutdown()
        {
            try
            {
                safeFileHandle?.Dispose();
                safeFileHandle = null;

                try { fsRead?.Flush(); } catch (Exception) { }
                try { fsRead?.Close(); fsRead?.Dispose(); } catch (Exception) { }
                fsRead = null;

                try { fsWrite?.Flush(); } catch (Exception) { }
                try { fsWrite?.Close(); fsWrite?.Dispose(); } catch (Exception) { }
                fsWrite = null;
            }
            catch (Exception)
            {
            }

            interfaceLinux = string.Empty;
            CommandHelper.Linux(string.Empty, new string[] { $"ip link del {Name}", $"ip tuntap del mode tun dev {Name}" });

            GC.Collect();
        }

        public void Refresh()
        {
            if (safeFileHandle == null) return;
            try
            {
                CommandHelper.Linux(string.Empty, new string[] {
                    $"ip link set dev {Name} up"
                });
            }
            catch (Exception)
            {
            }
        }

        public void SetMtu(int value)
        {
            CommandHelper.Linux(string.Empty, new string[] { $"ip link set dev {Name} mtu {value}" });
        }

        public void AddRoute(LinkerTunDeviceRouteItem[] ips)
        {
            string[] commands = ips.Select(item =>
            {
                uint prefixValue = NetworkHelper.ToPrefixValue(item.PrefixLength);
                IPAddress network = NetworkHelper.ToNetworkIP(item.Address, prefixValue);

                return $"ip route add {network}/{item.PrefixLength} via {address} dev {Name} metric 1 ";
            }).ToArray();
            if (commands.Length > 0)
            {
                if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                    LoggerHelper.Instance.Warning($"tuntap linux add route: {string.Join("\r\n", commands)}");
                CommandHelper.Linux(string.Empty, commands);
            }
        }
        public void RemoveRoute(LinkerTunDeviceRouteItem[] ip)
        {
            string[] commands = ip.Select(item =>
            {
                uint prefixValue = NetworkHelper.ToPrefixValue(item.PrefixLength);
                IPAddress network = NetworkHelper.ToNetworkIP(item.Address, prefixValue);
                return $"ip route del {network}/{item.PrefixLength}";
            }).ToArray();

            if (commands.Length > 0)
            {
                if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                    LoggerHelper.Instance.Warning($"tuntap linux del route: {string.Join("\r\n", commands)}");
                CommandHelper.Linux(string.Empty, commands);
            }

        }


        private readonly byte[] buffer = new byte[128 * 1024];
        private readonly object writeLockObj = new object();
        public byte[] Read(out int length)
        {
            length = 0;
            if (safeFileHandle == null) return Helper.EmptyArray;

            length = fsRead.Read(buffer.AsSpan(4));
            length.ToBytes(buffer.AsSpan());
            length += 4;
            return buffer;

        }
        public bool Write(ReadOnlyMemory<byte> buffer)
        {
            if (safeFileHandle == null) return true;

            lock (writeLockObj)
            {
                try
                {
                    fsWrite.Write(buffer.Span);
                    fsWrite.Flush();
                }
                catch (Exception ex)
                {
                    if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                    {
                        LoggerHelper.Instance.Error(ex.Message);
                        LoggerHelper.Instance.Error(string.Join(",", buffer.ToArray()));
                    }
                }
                return true;
            }
        }

        private string GetLinuxInterfaceNum()
        {
            string output = CommandHelper.Linux(string.Empty, new string[] { "ip route show default | awk '{print $5}'" }).TrimNewLineAndWhiteSapce();
            return output;
        }

        public async Task<bool> CheckAvailable(bool order = false)
        {
            string output = CommandHelper.Linux(string.Empty, new string[] { $"ip link show {Name}" });
            return await Task.FromResult(output.Contains("state UP")).ConfigureAwait(false);
        }
    }
}
