using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using tun324.libs;

namespace tun324.tun.proxy
{
    public sealed class ProxySocks5 : IProxy
    {
        public string Name => "Socks5";

        private IPEndPoint server = new IPEndPoint(IPAddress.Any, 0);
        private string username = string.Empty;
        private string password = string.Empty;
        public bool Test(string proxy) => string.IsNullOrWhiteSpace(proxy) == false && proxy.StartsWith("socks5");
        public void Parse(string proxy)
        {
            Uri uri = new Uri(proxy);

            server = new IPEndPoint(NetworkHelper.GetDomainIp(uri.Host), uri.Port);
            if (string.IsNullOrWhiteSpace(uri.UserInfo) == false)
            {
                var parts = uri.UserInfo.Split(':');
                username = parts[0];
                if (parts.Length > 1)
                {
                    password = parts[1];
                }
            }
        }

        public async Task ConnectAsync(Socket src, IPEndPoint ep)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1 * 1024 * 1024);
            byte[] buffer1 = ArrayPool<byte>.Shared.Rent(1 * 1024 * 1024);

            Socket dst = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                    LoggerHelper.Instance.Info($"[Socks5] connect {ep} with socks server {server}");
                await dst.ConnectAsync(server);

                int length = 0;
                buffer[0] = 0x05;
                if (string.IsNullOrWhiteSpace(username))
                {
                    buffer[1] = 0x01;
                    buffer[2] = 0x00;
                    length = 3;
                }
                else
                {
                    buffer[1] = 0x02;
                    buffer[2] = 0x00;
                    buffer[3] = 0x02;
                    length = 4;
                }
                await dst.SendAsync(buffer.AsMemory(0, length), SocketFlags.None);
                await dst.ReceiveAsync(buffer.AsMemory(), SocketFlags.None);
                if (buffer[1] == 0x02)
                {
                    length = 0;
                    buffer[length] = 0x05;
                    length++;
                    buffer[length] = (byte)username.Length;
                    length++;
                    username.ToBytes().CopyTo(buffer.AsMemory(length));
                    length += username.Length;
                    buffer[length] = (byte)password.Length;
                    length++;
                    password.ToBytes().CopyTo(buffer.AsMemory(length));
                    length += password.Length;

                    await dst.SendAsync(buffer.AsMemory(0, length), SocketFlags.None);
                    await dst.ReceiveAsync(buffer.AsMemory(), SocketFlags.None);
                    if (buffer[1] != 0x00)
                    {
                        throw new Exception("Authentication failed");
                    }
                }

                buffer[0] = 0x05;
                buffer[1] = 0x01;
                buffer[2] = 0x00;
                buffer[3] = 0x01;
                ep.Address.TryWriteBytes(buffer.AsSpan(4, 4), out _);
                BinaryPrimitives.ReverseEndianness((short)ep.Port).ToBytes().CopyTo(buffer.AsMemory(8, 2));

                await dst.SendAsync(buffer.AsMemory(0, 10), SocketFlags.None);
                await dst.ReceiveAsync(buffer.AsMemory(), SocketFlags.None);
                if (buffer[1] != 0x00)
                {
                    throw new Exception("Connect failed");
                }

                await Task.WhenAny(CopyToAsync(buffer, src, dst), CopyToAsync(buffer1, dst, src)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                    LoggerHelper.Instance.Error($"[Socks5] ex {ex}");
            }
            finally
            {
                src.SafeClose();
                dst.SafeClose();
                ArrayPool<byte>.Shared.Return(buffer);
                ArrayPool<byte>.Shared.Return(buffer1);
            }
        }
        private async Task CopyToAsync(Memory<byte> buffer, Socket source, Socket target)
        {
            try
            {
                int bytesRead;
                while ((bytesRead = await source.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false)) != 0)
                {
                    await target.SendAsync(buffer.Slice(0, bytesRead), SocketFlags.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (LoggerHelper.Instance.LoggerLevel <= LoggerTypes.DEBUG)
                    LoggerHelper.Instance.Error($"[Socks5] ex {ex}");
            }
            finally
            {
                source.SafeClose();
                target.SafeClose();
            }
        }
    }
}