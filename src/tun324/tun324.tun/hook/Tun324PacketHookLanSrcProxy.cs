using System.Net;

namespace tun324.tun.hook
{
    internal sealed class Tun324PacketHookLanSrcProxy : ITun324PacketHook
    {
        public string Name => "SrcProxy";
        public LinkerTunPacketHookLevel ReadLevel => LinkerTunPacketHookLevel.Low9;
        public LinkerTunPacketHookLevel WriteLevel => LinkerTunPacketHookLevel.High9;
        private readonly Tun324SrcProxy LinkerSrcProxy = new Tun324SrcProxy();

        public bool Running => LinkerSrcProxy.Running;

        public Tun324PacketHookLanSrcProxy()
        {
        }

        public void Setup(IPAddress address, byte prefixLength,string proxy, ref string error)
        {
            LinkerSrcProxy.Setup(address, prefixLength, proxy, ref error);
        }
        public void Shutdown()
        {
            try
            {
                LinkerSrcProxy.Shutdown();
            }
            catch (Exception)
            {
            }
            GC.Collect();
        }

        public (LinkerTunPacketHookFlags add, LinkerTunPacketHookFlags del) Read(ReadOnlyMemory<byte> packet)
        {
            if (LinkerSrcProxy.Read(packet))
            {
                return (LinkerTunPacketHookFlags.None, LinkerTunPacketHookFlags.None);
            }
            return (LinkerTunPacketHookFlags.WriteBack, LinkerTunPacketHookFlags.Next | LinkerTunPacketHookFlags.Send);
        }
        public async ValueTask<(LinkerTunPacketHookFlags add, LinkerTunPacketHookFlags del)> WriteAsync(ReadOnlyMemory<byte> packet, uint originDstIp, string srcId)
        {
            return await ValueTask.FromResult((LinkerTunPacketHookFlags.None, LinkerTunPacketHookFlags.None));
        }
    }
}
