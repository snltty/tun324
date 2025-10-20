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
        private ITun324Device linkerTunDevice;
        private ILinkerTunDeviceCallback linkerTunDeviceCallback;
        private CancellationTokenSource cancellationTokenSource;

        private string setupError = string.Empty;
        public string SetupError => setupError;

        private IPAddress address;
        private byte prefixLength;
        private readonly Tun324PacketHookLanSrcProxy lanSrcProxy = new Tun324PacketHookLanSrcProxy();


        private readonly OperatingManager operatingManager = new OperatingManager();
        public LinkerTunDeviceStatus Status
        {
            get
            {
                if (linkerTunDevice == null) return LinkerTunDeviceStatus.Normal;

                return operatingManager.Operating
                    ? LinkerTunDeviceStatus.Operating
                    : linkerTunDevice.Running
                        ? LinkerTunDeviceStatus.Running
                        : LinkerTunDeviceStatus.Normal;
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
        /// <param name="linkerTunDeviceCallback">读取数据回调</param>
        public bool Initialize(ILinkerTunDeviceCallback linkerTunDeviceCallback)
        {
            this.linkerTunDeviceCallback = linkerTunDeviceCallback;
            if (linkerTunDevice == null)
            {
                if (OperatingSystem.IsWindows())
                {
                    linkerTunDevice = new Tun324WinTunDevice();
                    return true;
                }
                else if (OperatingSystem.IsLinux())
                {
                    linkerTunDevice = new Tun324LinuxTunDevice();
                    return true;
                }

                else if (OperatingSystem.IsMacOS())
                {
                    linkerTunDevice = new Tun324OsxTunDevice();
                    return true;
                }

            }
            return false;
        }
        /// <summary>
        /// 初始化
        /// </summary>
        /// <param name="linkerTunDevice">网卡实现</param>
        /// <param name="linkerTunDeviceCallback">读取数据回调</param>
        /// <returns></returns>
        public bool Initialize(ITun324Device linkerTunDevice, ILinkerTunDeviceCallback linkerTunDeviceCallback)
        {
            this.linkerTunDevice = linkerTunDevice;
            this.linkerTunDeviceCallback = linkerTunDeviceCallback;
            return true;
        }

        /// <summary>
        /// 开启网卡
        /// </summary>
        /// <param name="info">网卡信息</param>
        public bool Setup(LinkerTunDeviceSetupInfo info)
        {
            if (operatingManager.StartOperation() == false)
            {
                setupError = $"setup are operating";
                return false;
            }
            try
            {
                if (linkerTunDevice == null)
                {
                    setupError = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} not support";
                    return false;
                }
                this.address = info.Address;
                this.prefixLength = info.PrefixLength;
                linkerTunDevice.Setup(info, out setupError);
                if (string.IsNullOrWhiteSpace(setupError) == false)
                {
                    return false;
                }
                linkerTunDevice.SetMtu(info.Mtu);
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
            if (linkerTunDevice == null)
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
                linkerTunDevice.Shutdown();
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
            if (linkerTunDevice == null)
            {
                return;
            }
            linkerTunDevice.Refresh();
        }

        /// <summary>
        /// 添加路由
        /// </summary>
        /// <param name="ips"></param>
        public void AddRoute(LinkerTunDeviceRouteItem[] ips)
        {
            if (linkerTunDevice == null)
            {
                return;
            }
            if (linkerTunDevice.Running)
                linkerTunDevice.AddRoute(ips);
        }
        /// <summary>
        /// 删除路由
        /// </summary>
        /// <param name="ips"></param>
        public void RemoveRoute(LinkerTunDeviceRouteItem[] ips)
        {
            if (linkerTunDevice == null)
            {
                return;
            }
            linkerTunDevice.RemoveRoute(ips);
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
                LinkerTunDevicPacket packet = new LinkerTunDevicPacket();
                while (cancellationTokenSource.IsCancellationRequested == false)
                {
                    try
                    {
                        byte[] buffer = linkerTunDevice.Read(out int length);
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

                        LinkerTunPacketHookFlags flags = LinkerTunPacketHookFlags.Next | LinkerTunPacketHookFlags.Send;
                        for (int i = 0; i < readHooks.Length; i++)
                        {
                            (LinkerTunPacketHookFlags addFlags, LinkerTunPacketHookFlags delFlags) = readHooks[i].Read(packet.RawPacket);
                            flags |= addFlags;
                            flags &= ~delFlags;
                            if ((flags & LinkerTunPacketHookFlags.Next) != LinkerTunPacketHookFlags.Next)
                            {
                                break;
                            }
                        }
                        ChecksumHelper.ChecksumWithZero(packet.RawPacket);

                        if ((flags & LinkerTunPacketHookFlags.WriteBack) == LinkerTunPacketHookFlags.WriteBack)
                        {
                            linkerTunDevice.Write(packet.RawPacket);
                        }
                        if ((flags & LinkerTunPacketHookFlags.Send) == LinkerTunPacketHookFlags.Send)
                        {
                            await linkerTunDeviceCallback.Callback(packet).ConfigureAwait(false);
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

            if (Status != LinkerTunDeviceStatus.Running || dstIp == 0)
            {
                return false;
            }
            LinkerTunPacketHookFlags flags = LinkerTunPacketHookFlags.Next | LinkerTunPacketHookFlags.Write;
            for (int i = 0; i < writeHooks.Length; i++)
            {
                (LinkerTunPacketHookFlags addFlags, LinkerTunPacketHookFlags delFlags) = await writeHooks[i].WriteAsync(buffer, dstIp, srcId).ConfigureAwait(false);
                flags |= addFlags;
                flags &= ~delFlags;
                if ((flags & LinkerTunPacketHookFlags.Next) != LinkerTunPacketHookFlags.Next)
                {
                    break;
                }
            }
            ChecksumHelper.ChecksumWithZero(buffer);

            return (flags & LinkerTunPacketHookFlags.Write) != LinkerTunPacketHookFlags.Write || linkerTunDevice.Write(buffer);
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
            if (linkerTunDevice == null)
            {
                return false;
            }
            return await linkerTunDevice.CheckAvailable(order);
        }
    }
}
