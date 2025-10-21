﻿using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using tun324.libs;

namespace tun324.tun.device
{
    [SupportedOSPlatform("windows")]
    internal sealed class Tun324WinTunDevice : ITun324Device
    {
        private string name = string.Empty;
        public string Name => name;
        public bool Running => session != 0;

        private nint waitHandle = nint.Zero, adapter = nint.Zero, session = nint.Zero;
        private int interfaceNumber = 0;
        private IPAddress address;
        private byte prefixLength = 24;

        private CancellationTokenSource tokenSource;



        public Tun324WinTunDevice()
        {
        }

        public bool Setup(Tun324TunDeviceSetupInfo info, out string error)
        {
            name = info.Name;
            address = info.Address;
            prefixLength = info.PrefixLength;


            error = string.Empty;
            if (adapter != 0)
            {
                error = $"Adapter already exists";
                return false;
            }

            if (info.Guid == Guid.Empty) info.Guid = Guid.Parse("771EF382-8718-5BC5-EBF0-A28B86142278");
            Guid guid = info.Guid;

            for (int i = 0; i < 5; i++)
            {
                if (

                        (adapter = WinTun.WintunCreateAdapter(name, name, ref guid)) == 0
                        && (adapter = WinTun.WintunOpenAdapter(name)) == 0

                    || (session = WinTun.WintunStartSession(adapter, 0x400000)) == 0
                )
                {
                    Shutdown();
                    Thread.Sleep(2000);
                    continue;
                }
                break;
            }
            if (adapter == 0)
            {
                error = $"Failed to create adapter {Marshal.GetLastWin32Error()}";
                return false;
            }
            if (session == 0)
            {
                error = $"Failed to start session";
                Shutdown();
                return false;
            }

            waitHandle = WinTun.WintunGetReadWaitEvent(session);
            for (int i = 0; i < 5 && interfaceNumber == 0; i++)
            {
                try
                {
                    AddIPV4();
                    //AddIPV6();

                    interfaceNumber = GetWindowsInterfaceNum();
                    tokenSource = new CancellationTokenSource();

                    return true;
                }
                catch (Exception)
                {
                    Thread.Sleep(2000);
                }
            }
            error = $"Failed to set adapter ip {Marshal.GetLastWin32Error()}";
            Shutdown();
            return false;

        }

        private void AddIPV4()
        {
            try
            {
                if (session == 0) return;

                WinTun.WintunGetAdapterLUID(adapter, out ulong luid);
                {
                    WinTun.MIB_UNICASTIPADDRESS_ROW AddressRow = default;
                    WinTun.InitializeUnicastIpAddressEntry(ref AddressRow);
                    AddressRow.sin_family = 2;
                    AddressRow.sin_addr = BinaryPrimitives.ReadUInt32LittleEndian(address.GetAddressBytes());
                    AddressRow.OnLinkPrefixLength = prefixLength;
                    AddressRow.DadState = 4;
                    AddressRow.InterfaceLuid = luid;
                    uint LastError = WinTun.CreateUnicastIpAddressEntry(ref AddressRow);
                    if (LastError != 0) throw new InvalidOperationException();
                }
            }
            catch (Exception)
            {
            }
        }
        private void AddIPV6()
        {
            NetworkInterface networkInterface = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(c => c.Name == Name);
            if (networkInterface != null)
            {
                var commands = networkInterface.GetIPProperties()
                    .UnicastAddresses
                    .Where(c => c.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    .Select(c => new IPAddress(c.Address.GetAddressBytes(), 0))
                    .Select(c => $"netsh interface ipv6 delete address \"{Name}\" address=\"{c}\"").ToList();

                byte[] ipv6 = IPAddress.Parse("fe80::1818:1818:1818:1818").GetAddressBytes();
                address.GetAddressBytes().CopyTo(ipv6, ipv6.Length - 4);
                commands.Add($"netsh interface ipv6 add address \"{Name}\" address=\"{new IPAddress(ipv6)}\"");
                CommandHelper.Windows(string.Empty, [.. commands]);
            }
        }

        public void Shutdown()
        {
            tokenSource?.Cancel();
            if (waitHandle != 0)
            {
                WinTun.SetEvent(waitHandle);
            }
            if (session != 0)
            {
                WinTun.WintunEndSession(session);
            }
            if (adapter != 0)
            {
                WinTun.WintunCloseAdapter(adapter);
            }
            waitHandle = 0;
            session = 0;
            adapter = 0;
            interfaceNumber = 0;

            GC.Collect();
        }

        public void Refresh()
        {
            if (session == 0) return;
            try
            {
                nint oldSession = session;
                nint oldWaitHandle = waitHandle;

                CommandHelper.Windows(string.Empty, new string[] { $"netsh interface set interface {Name} enable" });
                session = WinTun.WintunStartSession(adapter, 0x400000);
                waitHandle = WinTun.WintunGetReadWaitEvent(session);
                AddIPV4();
                //AddIPV6();

                WinTun.SetEvent(oldWaitHandle);
                WinTun.WintunEndSession(oldSession);
            }
            catch (Exception)
            {
            }
        }

        public void SetMtu(int value)
        {
            CommandHelper.Windows(string.Empty, new string[] {
                $"netsh interface ipv4 set subinterface {interfaceNumber}  mtu={value} store=persistent" ,
                 $"netsh interface ipv6 set subinterface {interfaceNumber}  mtu={value} store=persistent"
            });
        }

        public void AddRoute(Tun324TunDeviceRouteItem[] ips)
        {
            if (interfaceNumber > 0)
            {
                string[] commands = ips.Select(item =>
                {
                    uint maskValue = NetworkHelper.ToPrefixValue(item.PrefixLength);
                    IPAddress mask = NetworkHelper.ToIP(maskValue);
                    IPAddress _ip = NetworkHelper.ToNetworkIP(item.Address, maskValue);

                    return $"route add {_ip} mask {mask} {address} metric 5 if {interfaceNumber}";
                }).ToArray();
                if (commands.Length > 0)
                {
                    if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                        LoggerHelper.Instance.Warning($"[WIN TUN] win add route: {string.Join("\r\n", commands)}");
                    CommandHelper.Windows(string.Empty, commands);
                }
            }
        }
        public void RemoveRoute(Tun324TunDeviceRouteItem[] ips)
        {
            string[] commands = ips.Select(item =>
            {
                uint maskValue = NetworkHelper.ToPrefixValue(item.PrefixLength);
                IPAddress mask = NetworkHelper.ToIP(maskValue);
                IPAddress _ip = NetworkHelper.ToNetworkIP(item.Address, maskValue);
                return $"route delete {_ip}";
            }).ToArray();
            if (commands.Length > 0)
            {
                if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                    LoggerHelper.Instance.Warning($"[WIN TUN] win del route: {string.Join("\r\n", commands)}");
                CommandHelper.Windows(string.Empty, commands.ToArray());
            }
        }


        private readonly byte[] buffer = new byte[128 * 1024];
        public unsafe byte[] Read(out int length)
        {
            length = 0;
            if (session == 0) return Helper.EmptyArray;
            for (; tokenSource.IsCancellationRequested == false;)
            {
                nint packetPtr = WinTun.WintunReceivePacket(session, out uint size);
                length = (int)size;

                if (packetPtr != 0)
                {
                    new Span<byte>((byte*)packetPtr, length).CopyTo(buffer.AsSpan(4, length));
                    length.ToBytes(buffer.AsSpan());
                    WinTun.WintunReleaseReceivePacket(session, packetPtr);
                    length += 4;
                    return buffer;
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();

                    if (error == 0 || error == 259L)
                    {
                        WinTun.WaitForSingleObject(waitHandle, 0xFFFFFFFF);
                    }
                    else
                    {
                        return Helper.EmptyArray;
                    }
                }
            }
            return Helper.EmptyArray;
        }
        public unsafe bool Write(ReadOnlyMemory<byte> packet)
        {
            if (session == 0 || tokenSource.IsCancellationRequested) return false;

            nint packetPtr = WinTun.WintunAllocateSendPacket(session, (uint)packet.Length);
            if (packetPtr != 0)
            {
                packet.Span.CopyTo(new Span<byte>((byte*)packetPtr, packet.Length));
                WinTun.WintunSendPacket(session, packetPtr);
                return true;
            }
            else
            {
                if (Marshal.GetLastWin32Error() == 111L)
                {
                    return false;
                }
            }
            return false;
        }

        private int GetWindowsInterfaceNum()
        {
            NetworkInterface adapter = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(c => c.Name == Name);
            if (adapter != null)
            {
                return adapter.GetIPProperties().GetIPv4Properties().Index;
            }
            return 0;
        }
        private void GetDefaultInterface()
        {
            string[] lines = CommandHelper.Windows(string.Empty, new string[] { $"route print" }).Split(Environment.NewLine);
            foreach (var item in lines)
            {
                if (item.Trim().StartsWith("0.0.0.0"))
                {
                    string[] arr = Regex.Replace(item.Trim(), @"\s+", " ").Split(' ');
                    IPAddress ip = IPAddress.Parse(arr[arr.Length - 2]);

                    foreach (var inter in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        try
                        {
                            if (ip.Equals(inter.GetIPProperties().UnicastAddresses.FirstOrDefault(c => c.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Address))
                            {
                                /*
                                defaultInterfaceName = inter.Name;
                                defaultInterfaceNumber = inter.GetIPProperties().GetIPv4Properties().Index;
                                defaultInterfaceIP = ip;
                                defaultInterfaceIP32 = NetworkHelper.ToValue(ip);
                                */
                                return;
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
        }

        public async Task<bool> CheckAvailable(bool order = false)
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();

            if (order)
                InterfaceOrder(interfaces);
            NetworkInterface networkInterface = interfaces.FirstOrDefault(c => c.Name == Name || c.Description == $"{Name} Tunnel" || c.Name.Contains(Name));

            UnicastIPAddressInformation firstIpv4 = networkInterface?.GetIPProperties()
                .UnicastAddresses.FirstOrDefault(c => c.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

            if (networkInterface == null || firstIpv4 == null || firstIpv4.Address == null || firstIpv4.Address.Equals(address) == false)
            {
                return false;
            }
            return await Task.FromResult(true);
        }
        private void InterfaceOrder(NetworkInterface[] interfaces)
        {
            NetworkInterface Tun324 = interfaces.FirstOrDefault(c => c.Name == Name || c.Description == $"{Name} Tunnel" || c.Name.Contains(Name));
            NetworkInterface first = interfaces.FirstOrDefault();

            if (Tun324 != null && Tun324.Name != first.Name)
            {
                UnicastIPAddressInformation firstIpv4 = Tun324.GetIPProperties().UnicastAddresses.FirstOrDefault(c => c.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (firstIpv4 == null || firstIpv4.Address == null || firstIpv4.Address.Equals(address) == false)
                {
                    return;
                }


                int metricv4 = 0;
                int metricv6 = 0;
                List<string> commands = new List<string> {
                    $"netsh interface ipv4 set interface \"{Name}\" metric={++metricv4}",
                    $"netsh interface ipv6 set interface \"{Name}\" metric={++metricv6}"
                };
                commands.AddRange(interfaces
                    .Where(c => c.Name != Name)
                    .Select(c => $"netsh interface ipv4 set interface \"{c.Name}\" metric={++metricv4}"));
                commands.AddRange(interfaces
                    .Where(c => c.Name != Name)
                    .Select(c => $"netsh interface ipv6 set interface \"{c.Name}\" metric={++metricv6}"));
                commands.Add(string.Empty);
                foreach (var command in commands)
                {
                    CommandHelper.Windows(string.Empty, [command]);
                }
            }
        }

    }
}


