using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using tun324.libs;
using tun324.tun.proxy;

namespace tun324.tun
{
    public sealed class Tun324SrcProxy
    {
        public bool Running => listenSocketTcp != null;
        private Socket listenSocketTcp;

        ushort proxyPort = 0;
        uint tunIp = 0;
        uint proxySrc = 0;

        private readonly ProxyManager proxyManager = new ProxyManager();

        private readonly ConcurrentDictionary<(uint srcIp, ushort srcPort), SrcCacheInfo> srcMap = new();

        public Tun324SrcProxy()
        {
            SrcMapClearTask();
        }
        public bool Setup(IPAddress srcAddr, byte prefixLength, string proxy, ref string error)
        {
            Shutdown();
            try
            {
                proxyManager.Parse(proxy);

                listenSocketTcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listenSocketTcp.Bind(new IPEndPoint(IPAddress.Any, 0));
                listenSocketTcp.Listen(int.MaxValue);

                proxySrc = NetworkHelper.ToNetworkValue(srcAddr, prefixLength);
                tunIp = NetworkHelper.ToValue(srcAddr);
                proxyPort = (ushort)(listenSocketTcp.LocalEndPoint as IPEndPoint).Port;

                _ = AcceptAsync().ConfigureAwait(false);

                error = string.Empty;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            return true;
        }
        private async Task AcceptAsync()
        {
            int hashcode = listenSocketTcp.GetHashCode();
            try
            {
                while (true)
                {
                    Socket source = await listenSocketTcp.AcceptAsync().ConfigureAwait(false);
                    IPEndPoint local = source.LocalEndPoint as IPEndPoint;
                    IPEndPoint remote = source.RemoteEndPoint as IPEndPoint;

                    (uint srcIp, ushort srcPort) key = (NetworkHelper.ToValue(local.Address), (ushort)remote.Port);
                    if (srcMap.TryGetValue(key, out SrcCacheInfo cache) == false)
                    {
                        source.SafeClose();
                        continue;
                    }
                    _ = proxyManager.ConnectAsync(source, new IPEndPoint(NetworkHelper.ToIP(cache.DstAddr), cache.DstPort)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                    LoggerHelper.Instance.Error(ex);
                if (listenSocketTcp != null && listenSocketTcp.GetHashCode() == hashcode)
                    Shutdown();
            }
        }

        public void Shutdown()
        {
            listenSocketTcp?.SafeClose();
            listenSocketTcp = null;
            srcMap.Clear();
        }

        public unsafe bool Read(ReadOnlyMemory<byte> packet)
        {
            if (Running == false) return true;

            //《关于TUN虚拟网卡自转发实现TCP/IP三层转四层代理》，目标可以是HTTP/HTTPS/SOCKS5任意代理
            //虚拟网卡IP 10.18.18.2，代理端口33333，假设原始连接 10.18.18.2:11111->10.18.18.3:5201
            fixed (byte* ptr = packet.Span)
            {
                SrcProxyPacket srcProxyPacket = new(ptr);
                if (srcProxyPacket.Protocol != ProtocolType.Tcp || srcProxyPacket.SrcAddr != tunIp) return true;//往下走
                //从代理端口回来的
                if (srcProxyPacket.SrcPort == proxyPort)
                {
                    //(10.18.18.2,22222)、取到说明已建立连接，包括[SYN+ACK/PSH+ACK/ACK/FIN/RST]的任意包
                    if (srcMap.TryGetValue((srcProxyPacket.SrcAddr, srcProxyPacket.DstPort), out SrcCacheInfo cache))
                    {
                        if (srcProxyPacket.TcpFinOrRst) cache.Fin = true;
                        cache.LastTime = Environment.TickCount64;
                        //3、10.18.18.2:33333->10.18.18.2:22222 改为 10.18.18.3:5201->10.18.18.2:11111 
                        srcProxyPacket.DstAddr = srcProxyPacket.SrcAddr;
                        srcProxyPacket.DstPort = cache.SrcPort;
                        srcProxyPacket.SrcAddr = cache.DstAddr;
                        srcProxyPacket.SrcPort = cache.DstPort;
                        srcProxyPacket.IPChecksum = 0; //需要重新计算IP头校验和
                        srcProxyPacket.PayloadChecksum = 0; //需要重新计算TCP校验和
                    }
                }
                else //从访问端来的
                {
                    (uint srcIp, ushort srcPort) key = (srcProxyPacket.SrcAddr, srcProxyPacket.SrcPort);
                    //(10.18.18.2,11111)、取不到是SYN包则建立映射，不是SYN包则继续走原路
                    if (srcMap.TryGetValue(key, out SrcCacheInfo cache) == false)
                    {
                        if (srcProxyPacket.TcpOnlySyn == false) return true; //往下走
                        //1、10.18.18.2:11111->10.18.18.3:5201 [SYN] 新连接
                        cache = new SrcCacheInfo
                        {
                            DstAddr = srcProxyPacket.DstAddr,
                            DstPort = srcProxyPacket.DstPort,
                            SrcPort = srcProxyPacket.SrcPort,
                            NewPort = NetworkHelper.ApplyNewPort() //随机新端口,比如 22222，windows某些版本不需要新端口，可以直接使用11111
                        };
                        //添加 (10.18.18.2,11111)、(10.18.18.2,22222) 作为key的缓存
                        srcMap.AddOrUpdate((srcProxyPacket.SrcAddr, cache.SrcPort), cache, (a, b) => cache);
                        srcMap.AddOrUpdate((srcProxyPacket.SrcAddr, cache.NewPort), cache, (a, b) => cache);
                    }
                    if (srcProxyPacket.TcpFinOrRst) cache.Fin = true;
                    cache.LastTime = Environment.TickCount64;
                    //2、10.18.18.2:11111->10.18.18.3:5201 改为 10.18.18.0:22222->10.18.18.2:33333 包括[SYN/PSH+ACK/ACK/FIN/RST]的任意包
                    srcProxyPacket.DstAddr = srcProxyPacket.SrcAddr;
                    srcProxyPacket.DstPort = proxyPort;
                    srcProxyPacket.SrcAddr = proxySrc;
                    srcProxyPacket.SrcPort = cache.NewPort;
                    srcProxyPacket.IPChecksum = 0; //需要重新计算IP头校验和
                    srcProxyPacket.PayloadChecksum = 0;//需要重新计算TCP校验和
                }
                return false;
            }

        }
        private void SrcMapClearTask()
        {
            TimerHelper.SetIntervalLong(() =>
            {
                foreach (var item in srcMap.Where(c => c.Value.Fin && Environment.TickCount64 - c.Value.LastTime > 60 * 1000).ToList())
                {
                    srcMap.TryRemove(item.Key, out _);
                }
            }, 30000);
        }

        sealed class SrcCacheInfo
        {
            public long LastTime { get; set; } = Environment.TickCount64;
            public uint DstAddr { get; set; }
            public ushort DstPort { get; set; }
            public ushort SrcPort { get; set; }
            public ushort NewPort { get; set; }
            public bool Fin { get; set; }
        }
        readonly unsafe struct SrcProxyPacket
        {
            private readonly byte* ptr;

            public readonly byte Version => (byte)((*ptr >> 4) & 0b1111);
            public readonly ProtocolType Protocol => (ProtocolType)(*(ptr + 9));
            public readonly int IPHeadLength => (*ptr & 0b1111) * 4;
            public readonly byte* PayloadPtr => ptr + IPHeadLength;
            public readonly uint SrcAddr
            {
                get
                {
                    return BinaryPrimitives.ReverseEndianness(*(uint*)(ptr + 12));
                }
                set
                {
                    *(uint*)(ptr + 12) = BinaryPrimitives.ReverseEndianness(value);
                }
            }
            public readonly uint DstAddr
            {
                get
                {
                    return BinaryPrimitives.ReverseEndianness(*(uint*)(ptr + 16));
                }
                set
                {
                    *(uint*)(ptr + 16) = BinaryPrimitives.ReverseEndianness(value);
                }
            }
            public readonly ushort SrcPort
            {
                get
                {
                    return BinaryPrimitives.ReverseEndianness(*(ushort*)(PayloadPtr));
                }
                set
                {
                    *(ushort*)(PayloadPtr) = BinaryPrimitives.ReverseEndianness(value);
                }
            }
            public readonly ushort DstPort
            {
                get
                {
                    return BinaryPrimitives.ReverseEndianness(*(ushort*)(PayloadPtr + 2));
                }
                set
                {
                    *(ushort*)(PayloadPtr + 2) = BinaryPrimitives.ReverseEndianness(value);
                }
            }

            public byte DataOffset
            {
                get
                {
                    return (byte)((*(PayloadPtr + 12) >> 4) & 0b1111);
                }
                set
                {
                    *(PayloadPtr + 12) = (byte)((*(PayloadPtr + 12) & 0b00001111) | ((value & 0b1111) << 4));
                }
            }


            const byte fin = 1;
            const byte syn = 2;
            const byte rst = 4;
            const byte psh = 8;
            const byte ack = 16;
            const byte urg = 32;
            public readonly byte TcpFlag
            {
                get
                {
                    return *(PayloadPtr + 13);
                }
                set
                {
                    *(PayloadPtr + 13) = value;
                }
            }
            public readonly bool TcpFlagFin => (TcpFlag & fin) != 0;
            public readonly bool TcpFlagSyn => (TcpFlag & syn) != 0;
            public readonly bool TcpFlagRst => (TcpFlag & rst) != 0;
            public readonly bool TcpFlagPsh => (TcpFlag & psh) != 0;
            public readonly bool TcpFlagAck => (TcpFlag & ack) != 0;
            public readonly bool TcpFlagUrg => (TcpFlag & urg) != 0;

            public readonly bool TcpPshAck => (TcpFlag & (psh | ack)) == (psh | ack);
            public readonly bool TcpOnlyAck => TcpFlag == ack;
            public readonly bool TcpOnlySyn => TcpFlag == syn;
            public readonly bool TcpSynAck => TcpFlag == (syn | ack);
            public readonly bool TcpFinOrRst => (TcpFlag & (fin | rst)) != 0;


            public readonly ushort IPChecksum
            {
                get
                {
                    return BinaryPrimitives.ReverseEndianness(*(ushort*)(ptr + 10));
                }
                set
                {
                    *(ushort*)(ptr + 10) = BinaryPrimitives.ReverseEndianness(value);
                }
            }
            public readonly ushort PayloadChecksum
            {
                get
                {
                    return Protocol switch
                    {
                        ProtocolType.Icmp => BinaryPrimitives.ReverseEndianness(*(ushort*)(PayloadPtr + 2)),
                        ProtocolType.Tcp => BinaryPrimitives.ReverseEndianness(*(ushort*)(PayloadPtr + 16)),
                        ProtocolType.Udp => BinaryPrimitives.ReverseEndianness(*(ushort*)(PayloadPtr + 6)),
                        _ => (ushort)0,
                    };
                }
                set
                {
                    switch (Protocol)
                    {
                        case ProtocolType.Icmp:
                            *(ushort*)(PayloadPtr + 2) = BinaryPrimitives.ReverseEndianness(value);
                            break;
                        case ProtocolType.Tcp:
                            *(ushort*)(PayloadPtr + 16) = BinaryPrimitives.ReverseEndianness(value);
                            break;
                        case ProtocolType.Udp:
                            *(ushort*)(PayloadPtr + 6) = BinaryPrimitives.ReverseEndianness(value);
                            break;
                    }
                }
            }

            public SrcProxyPacket(byte* ptr)
            {
                this.ptr = ptr;
            }
        }

    }

}
