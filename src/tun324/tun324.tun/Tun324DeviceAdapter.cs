using tun324.tun.device;
using System.Buffers.Binary;
using System.Net;
using tun324.libs;
using tun324.tun.hook;

namespace tun324.tun
{
    /// <summary>
    /// tun网卡适配器，自动选择不同平台的实现
    /// </summary>
    public sealed class Tun324DeviceAdapter
    {
        private ITun324Device Tun324TunDevice;
        private ITun324TunDeviceCallback Tun324TunDeviceCallback;
        private CancellationTokenSource cancellationTokenSource;

        private string setupError = string.Empty;
        public string SetupError => setupError;

        private IPAddress address;
        private byte prefixLength;
        private readonly Tun324PacketHookLanSrcProxy lanSrcProxy = new Tun324PacketHookLanSrcProxy();


        private readonly OperatingManager operatingManager = new OperatingManager();
        public Tun324TunDeviceStatus Status
        {
            get
            {
                if (Tun324TunDevice == null) return Tun324TunDeviceStatus.Normal;

                return operatingManager.Operating
                    ? Tun324TunDeviceStatus.Operating
                    : Tun324TunDevice.Running
                        ? Tun324TunDeviceStatus.Running
                        : Tun324TunDeviceStatus.Normal;
            }
        }

        private ITun324PacketHook[] readHooks = [];
        private ITun324PacketHook[] writeHooks = [];

        public Tun324DeviceAdapter()
        {
            var hooks = new ITun324PacketHook[] { lanSrcProxy };
            readHooks = [.. hooks.OrderBy(c => c.ReadLevel)];
            writeHooks = [.. hooks.OrderBy(c => c.WriteLevel)];
        }

        /// <summary>
        /// 初始化
        /// </summary>
        /// <param name="Tun324TunDeviceCallback">读取数据回调</param>
        public bool Initialize(ITun324TunDeviceCallback Tun324TunDeviceCallback)
        {
            this.Tun324TunDeviceCallback = Tun324TunDeviceCallback;
            if (Tun324TunDevice == null)
            {
                if (OperatingSystem.IsWindows())
                {
                    Tun324TunDevice = new Tun324WinTunDevice();
                    return true;
                }
                else if (OperatingSystem.IsLinux())
                {
                    Tun324TunDevice = new Tun324LinuxTunDevice();
                    return true;
                }

                else if (OperatingSystem.IsMacOS())
                {
                    Tun324TunDevice = new Tun324OsxTunDevice();
                    return true;
                }

            }
            return false;
        }
        /// <summary>
        /// 初始化
        /// </summary>
        /// <param name="Tun324TunDevice">网卡实现</param>
        /// <param name="Tun324TunDeviceCallback">读取数据回调</param>
        /// <returns></returns>
        public bool Initialize(ITun324Device Tun324TunDevice, ITun324TunDeviceCallback Tun324TunDeviceCallback)
        {
            this.Tun324TunDevice = Tun324TunDevice;
            this.Tun324TunDeviceCallback = Tun324TunDeviceCallback;
            return true;
        }

        /// <summary>
        /// 开启网卡
        /// </summary>
        /// <param name="info">网卡信息</param>
        public bool Setup(Tun324TunDeviceSetupInfo info)
        {
            if (operatingManager.StartOperation() == false)
            {
                setupError = $"setup are operating";
                return false;
            }
            try
            {
                if (Tun324TunDevice == null)
                {
                    setupError = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} not support";
                    return false;
                }
                this.address = info.Address;
                this.prefixLength = info.PrefixLength;
                Tun324TunDevice.Setup(info, out setupError);
                if (string.IsNullOrWhiteSpace(setupError) == false)
                {
                    return false;
                }
                Tun324TunDevice.SetMtu(info.Mtu);
                Read();

                lanSrcProxy.Setup(address, prefixLength, info.Proxy, ref setupError);
                return true;
            }
            catch (Exception ex)
            {
                if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                    LoggerHelper.Instance.Warning($"[TUN] tuntap setup Exception {ex}");
                setupError = ex.Message;
            }
            finally
            {
                operatingManager.StopOperation();
            }
            return false;
        }
        /// <summary>
        /// 关闭网卡
        /// </summary>
        public bool Shutdown()
        {
            if (Tun324TunDevice == null)
            {
                return false;
            }
            if (operatingManager.StartOperation() == false)
            {
                setupError = $"shutdown are operating";
                return false;
            }
            try
            {
                cancellationTokenSource?.Cancel();
                Tun324TunDevice.Shutdown();
                lanSrcProxy.Shutdown();
            }
            catch (Exception)
            {
            }
            finally
            {
                operatingManager.StopOperation();
            }
            setupError = string.Empty;
            return true;
        }
        /// <summary>
        /// 刷新网卡
        /// </summary>
        public void Refresh()
        {
            if (Tun324TunDevice == null)
            {
                return;
            }
            Tun324TunDevice.Refresh();
        }

        /// <summary>
        /// 添加路由
        /// </summary>
        /// <param name="ips"></param>
        public void AddRoute(Tun324TunDeviceRouteItem[] ips)
        {
            if (Tun324TunDevice == null)
            {
                return;
            }
            if (Tun324TunDevice.Running)
                Tun324TunDevice.AddRoute(ips);
        }
        /// <summary>
        /// 删除路由
        /// </summary>
        /// <param name="ips"></param>
        public void RemoveRoute(Tun324TunDeviceRouteItem[] ips)
        {
            if (Tun324TunDevice == null)
            {
                return;
            }
            Tun324TunDevice.RemoveRoute(ips);
        }

        public void AddHooks(List<ITun324PacketHook> hooks)
        {
            hooks = hooks.UnionBy(this.readHooks, c => c.Name).ToList();

            readHooks = [.. hooks.OrderBy(c => c.ReadLevel)];
            writeHooks = [.. hooks.OrderBy(c => c.WriteLevel)];
        }

        private void Read()
        {
            TimerHelper.Async(async () =>
            {
                cancellationTokenSource = new CancellationTokenSource();
                Tun324TunDevicPacket packet = new Tun324TunDevicPacket();
                while (cancellationTokenSource.IsCancellationRequested == false)
                {
                    try
                    {
                        byte[] buffer = Tun324TunDevice.Read(out int length);
                        if (length == 0)
                        {
                            if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                                LoggerHelper.Instance.Warning($"[TUN] read buffer 0");
                            await Task.Delay(1000);
                            continue;
                        }

                        packet.Unpacket(buffer, 0, length);
                        if (packet.DstIp.Length == 0 || packet.Version != 4)
                        {
                            continue;
                        }

                        Tun324TunPacketHookFlags flags = Tun324TunPacketHookFlags.Next | Tun324TunPacketHookFlags.Send;
                        for (int i = 0; i < readHooks.Length; i++)
                        {
                            (Tun324TunPacketHookFlags addFlags, Tun324TunPacketHookFlags delFlags) = readHooks[i].Read(packet.RawPacket);
                            flags |= addFlags;
                            flags &= ~delFlags;
                            if ((flags & Tun324TunPacketHookFlags.Next) != Tun324TunPacketHookFlags.Next)
                            {
                                break;
                            }
                        }
                        ChecksumHelper.ChecksumWithZero(packet.RawPacket);

                        if ((flags & Tun324TunPacketHookFlags.WriteBack) == Tun324TunPacketHookFlags.WriteBack)
                        {
                            Tun324TunDevice.Write(packet.RawPacket);
                        }
                        if ((flags & Tun324TunPacketHookFlags.Send) == Tun324TunPacketHookFlags.Send)
                        {
                            await Tun324TunDeviceCallback.Callback(packet).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                            LoggerHelper.Instance.Warning($"[TUN] read buffer Exception {ex}");
                        setupError = ex.Message;
                        await Task.Delay(1000);
                    }
                }
            });
        }
        /// <summary>
        /// 写入一个TCP/IP数据包
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public async ValueTask<bool> Write(string srcId, ReadOnlyMemory<byte> buffer)
        {
            uint dstIp = VerifyPacket(buffer);

            if (Status != Tun324TunDeviceStatus.Running || dstIp == 0)
            {
                return false;
            }
            Tun324TunPacketHookFlags flags = Tun324TunPacketHookFlags.Next | Tun324TunPacketHookFlags.Write;
            for (int i = 0; i < writeHooks.Length; i++)
            {
                (Tun324TunPacketHookFlags addFlags, Tun324TunPacketHookFlags delFlags) = await writeHooks[i].WriteAsync(buffer, dstIp, srcId).ConfigureAwait(false);
                flags |= addFlags;
                flags &= ~delFlags;
                if ((flags & Tun324TunPacketHookFlags.Next) != Tun324TunPacketHookFlags.Next)
                {
                    break;
                }
            }
            ChecksumHelper.ChecksumWithZero(buffer);

            return (flags & Tun324TunPacketHookFlags.Write) != Tun324TunPacketHookFlags.Write || Tun324TunDevice.Write(buffer);
        }
        private unsafe uint VerifyPacket(ReadOnlyMemory<byte> buffer)
        {
            fixed (byte* ptr = buffer.Span)
            {
                if (BinaryPrimitives.ReverseEndianness(*(ushort*)(ptr + 2)) == buffer.Length)
                {
                    return BinaryPrimitives.ReverseEndianness(*(uint*)(ptr + 16));
                }
            }
            return 0;
        }

        public async Task<bool> CheckAvailable(bool order = false)
        {
            if (Tun324TunDevice == null)
            {
                return false;
            }
            return await Tun324TunDevice.CheckAvailable(order);
        }
    }
}
