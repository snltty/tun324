using System.Net;

namespace tun324.tun.hook
{
    internal sealed class Tun324PacketHookLanSrcProxy : ITun324PacketHook
    {
        public string Name => "SrcProxy";
        public Tun324TunPacketHookLevel ReadLevel => Tun324TunPacketHookLevel.Low9;
        public Tun324TunPacketHookLevel WriteLevel => Tun324TunPacketHookLevel.High9;
        private readonly Tun324SrcProxy Tun324SrcProxy = new Tun324SrcProxy();

        public bool Running => Tun324SrcProxy.Running;

        public Tun324PacketHookLanSrcProxy()
        {
        }

        public void Setup(IPAddress address, byte prefixLength,string proxy, ref string error)
        {
            Tun324SrcProxy.Setup(address, prefixLength, proxy, ref error);
        }
        public void Shutdown()
        {
            try
            {
                Tun324SrcProxy.Shutdown();
            }
            catch (Exception)
            {
            }
            GC.Collect();
        }

        public (Tun324TunPacketHookFlags add, Tun324TunPacketHookFlags del) Read(ReadOnlyMemory<byte> packet)
        {
            if (Tun324SrcProxy.Read(packet))
            {
                return (Tun324TunPacketHookFlags.None, Tun324TunPacketHookFlags.None);
            }
            return (Tun324TunPacketHookFlags.WriteBack, Tun324TunPacketHookFlags.Next | Tun324TunPacketHookFlags.Send);
        }
        public async ValueTask<(Tun324TunPacketHookFlags add, Tun324TunPacketHookFlags del)> WriteAsync(ReadOnlyMemory<byte> packet, uint originDstIp, string srcId)
        {
            return await ValueTask.FromResult((Tun324TunPacketHookFlags.None, Tun324TunPacketHookFlags.None));
        }
    }
}
