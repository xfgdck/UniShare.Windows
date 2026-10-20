using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UniShare.Core.Discovery
{
    /// <summary>
    /// 基于 RFC 6762 (Multicast DNS) 与 RFC 6763 (DNS-SD) 的轻量级纯托管 mDNS 服务端。
    /// 向局域网广播与响应 _smb._tcp.local 服务，让 macOS (访达)、iOS/iPadOS (文件)、
    /// Apple TV (Infuse/VidHub) 以及 Android 智能电视 (Kodi/VLC) 等设备能自动发现当前主机。
    /// </summary>
    public class MdnsDiscoveryServer : IDisposable
    {
        private const string MulticastIPv4 = "224.0.0.251";
        private const int MdnsPort = 5353;
        private const ushort TypeA = 1;
        private const ushort TypePtr = 12;
        private const ushort TypeTxt = 16;
        private const ushort TypeSrv = 33;
        private const ushort ClassIn = 1;
        private const ushort ClassInFlush = 0x8001; // Class IN with Cache-Flush bit

        private readonly string m_instanceName;
        private readonly string m_hostName;
        private readonly int m_smbPort;
        private readonly Action<string, string>? m_logger;

        private Socket? m_socket;
        private CancellationTokenSource? m_cts;
        private Timer? m_heartbeatTimer;
        private bool m_isRunning = false;

        public bool IsRunning => m_isRunning;

        public MdnsDiscoveryServer(int smbPort = 445, Action<string, string>? logger = null)
        {
            m_smbPort = smbPort;
            m_logger = logger;

            string rawMachineName = Environment.MachineName;
            m_hostName = string.IsNullOrWhiteSpace(rawMachineName) ? "UniShare" : rawMachineName.Trim();
            m_instanceName = m_hostName;
        }

        private void Log(string message, string severity = "Info")
        {
            m_logger?.Invoke(message, severity);
        }

        public void Start()
        {
            if (m_isRunning) return;

            try
            {
                m_cts = new CancellationTokenSource();

                m_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                m_socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                m_socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));

                // 加入 mDNS IPv4 多播组
                m_socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(IPAddress.Parse(MulticastIPv4), IPAddress.Any));
                m_socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

                m_isRunning = true;
                Log($"mDNS 服务已启动，正在监听 UDP 5353 (广播服务: {m_instanceName}._smb._tcp.local)");

                // 启动后台监听循环
                _ = Task.Run(() => ReceiveLoopAsync(m_cts.Token));

                // 初始连续宣告 3 次（RFC 推荐快速通知已在线设备）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        SendAnnouncement(ttl: 120);
                        await Task.Delay(250, m_cts.Token);
                        SendAnnouncement(ttl: 120);
                        await Task.Delay(500, m_cts.Token);
                        SendAnnouncement(ttl: 120);
                    }
                    catch { }
                });

                // 心跳定时器：每 60 秒刷新宣告一次，维持客户端缓存有效性
                m_heartbeatTimer = new Timer(_ =>
                {
                    if (m_isRunning)
                    {
                        try { SendAnnouncement(ttl: 120); }
                        catch { }
                    }
                }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            }
            catch (Exception ex)
            {
                m_isRunning = false;
                Log($"启动 mDNS 服务失败: {ex.Message}，局域网苹果/安卓设备自发现可能受限。", "Warning");
            }
        }

        public void Stop()
        {
            if (!m_isRunning) return;
            m_isRunning = false;

            try
            {
                m_heartbeatTimer?.Dispose();
                m_heartbeatTimer = null;

                // 发送 Goodbye 数据包 (TTL=0)，通知客户端立即清除缓存
                try { SendAnnouncement(ttl: 0); } catch { }

                try { m_cts?.Cancel(); } catch { }
                try
                {
                    m_socket?.Close();
                    m_socket?.Dispose();
                    m_socket = null;
                }
                catch { }

                try { m_cts?.Dispose(); } catch { }
                m_cts = null;

                Log("mDNS 服务已停止并发送设备下线宣告。");
            }
            catch (Exception ex)
            {
                Log($"停止 mDNS 服务异常: {ex.Message}", "Warning");
            }
        }

        /// <summary>
        /// 监听局域网客户端发来的 mDNS 查询并进行响应
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[4096];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (!ct.IsCancellationRequested && m_socket != null)
            {
                try
                {
                    var result = await m_socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp);
                    if (result.ReceivedBytes < 12) continue;

                    ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2));
                    // 仅处理查询请求 (QR 位为 0)
                    if ((flags & 0x8000) != 0) continue;

                    ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2));
                    if (qdCount == 0) continue;

                    if (IsRelevantQuery(buffer, result.ReceivedBytes, qdCount))
                    {
                        SendAnnouncement(ttl: 120);
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || m_socket == null) break;
                    Log($"mDNS 数据包处理异常: {ex.Message}", "Debug");
                }
            }
        }

        /// <summary>
        /// 检查收到的 DNS 查询是否涉及 SMB 服务或当前主机
        /// </summary>
        private bool IsRelevantQuery(byte[] buffer, int length, int qdCount)
        {
            try
            {
                int offset = 12;
                for (int i = 0; i < qdCount; i++)
                {
                    string name = ReadDnsName(buffer, ref offset, length);
                    if (offset + 4 > length) break;

                    ushort qtype = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
                    offset += 4; // 跳过 qtype(2) 和 qclass(2)

                    if (name.Contains("_smb._tcp", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("_services._dns-sd", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(m_hostName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 构造并多播发送完整的 mDNS 响应包（包含 PTR, SRV, TXT, A 记录）
        /// </summary>
        private void SendAnnouncement(uint ttl)
        {
            if (m_socket == null) return;

            try
            {
                var ips = DiscoveryHelper.GetLocalIPv4Addresses();
                if (ips.Count == 0) return;

                byte[] packet = BuildDnsResponsePacket(ips, ttl);
                IPEndPoint dest = new IPEndPoint(IPAddress.Parse(MulticastIPv4), MdnsPort);
                m_socket.SendTo(packet, dest);
            }
            catch { }
        }

        private byte[] BuildDnsResponsePacket(List<IPAddress> ips, uint ttl)
        {
            using MemoryStream ms = new MemoryStream();

            // 1. DNS 报头 (12 字节)
            ms.WriteByte(0); ms.WriteByte(0); // ID: 0
            ms.WriteByte(0x84); ms.WriteByte(0x00); // Flags: 0x8400 (Response, Authoritative)
            ms.WriteByte(0); ms.WriteByte(0); // Questions: 0

            ushort answerCount = (ushort)(3 + ips.Count); // PTR + SRV + TXT + N * A
            WriteUInt16(ms, answerCount);
            ms.WriteByte(0); ms.WriteByte(0); // Authority: 0
            ms.WriteByte(0); ms.WriteByte(0); // Additional: 0

            string serviceType = "_smb._tcp.local";
            string serviceInstance = $"{m_instanceName}.{serviceType}";
            string hostTarget = $"{m_hostName}.local";

            // 2. PTR 记录: _smb._tcp.local -> <instance>._smb._tcp.local
            WriteDnsName(ms, serviceType);
            WriteUInt16(ms, TypePtr);
            WriteUInt16(ms, ClassInFlush);
            WriteUInt32(ms, ttl);
            byte[] ptrRdata = EncodeDnsName(serviceInstance);
            WriteUInt16(ms, (ushort)ptrRdata.Length);
            ms.Write(ptrRdata, 0, ptrRdata.Length);

            // 3. SRV 记录: <instance>._smb._tcp.local -> 端口 445, Target: <host>.local
            WriteDnsName(ms, serviceInstance);
            WriteUInt16(ms, TypeSrv);
            WriteUInt16(ms, ClassInFlush);
            WriteUInt32(ms, ttl);
            byte[] hostTargetBytes = EncodeDnsName(hostTarget);
            ushort srvLen = (ushort)(6 + hostTargetBytes.Length);
            WriteUInt16(ms, srvLen);
            WriteUInt16(ms, 0); // Priority
            WriteUInt16(ms, 0); // Weight
            WriteUInt16(ms, (ushort)m_smbPort); // Port
            ms.Write(hostTargetBytes, 0, hostTargetBytes.Length);

            // 4. TXT 记录: <instance>._smb._tcp.local -> model=Mac (优化在苹果客户端中的图标展示)
            WriteDnsName(ms, serviceInstance);
            WriteUInt16(ms, TypeTxt);
            WriteUInt16(ms, ClassInFlush);
            WriteUInt32(ms, ttl);
            byte[] txtAttr = Encoding.ASCII.GetBytes("model=RackMac");
            ushort txtRdataLen = (ushort)(1 + txtAttr.Length);
            WriteUInt16(ms, txtRdataLen);
            ms.WriteByte((byte)txtAttr.Length);
            ms.Write(txtAttr, 0, txtAttr.Length);

            // 5. A 记录: 为本机所有活动 IPv4 地址分别创建 <host>.local -> IP 映射
            foreach (var ip in ips)
            {
                WriteDnsName(ms, hostTarget);
                WriteUInt16(ms, TypeA);
                WriteUInt16(ms, ClassInFlush);
                WriteUInt32(ms, ttl);
                WriteUInt16(ms, 4);
                byte[] ipBytes = ip.GetAddressBytes();
                ms.Write(ipBytes, 0, 4);
            }

            return ms.ToArray();
        }

        private static void WriteUInt16(Stream s, ushort val)
        {
            s.WriteByte((byte)(val >> 8));
            s.WriteByte((byte)(val & 0xFF));
        }

        private static void WriteUInt32(Stream s, uint val)
        {
            s.WriteByte((byte)(val >> 24));
            s.WriteByte((byte)((val >> 16) & 0xFF));
            s.WriteByte((byte)((val >> 8) & 0xFF));
            s.WriteByte((byte)(val & 0xFF));
        }

        private static void WriteDnsName(Stream s, string name)
        {
            byte[] encoded = EncodeDnsName(name);
            s.Write(encoded, 0, encoded.Length);
        }

        private static byte[] EncodeDnsName(string name)
        {
            using MemoryStream ms = new MemoryStream();
            string[] parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(part);
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes, 0, bytes.Length);
            }
            ms.WriteByte(0);
            return ms.ToArray();
        }

        private static string ReadDnsName(byte[] buffer, ref int offset, int length)
        {
            StringBuilder sb = new StringBuilder();
            while (offset < length)
            {
                byte len = buffer[offset++];
                if (len == 0) break;

                // 遇到压缩指针 (0xC0)
                if ((len & 0xC0) == 0xC0)
                {
                    if (offset >= length) break;
                    offset++; // 跳过指针第二字节
                    break;
                }

                if (offset + len > length) break;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.UTF8.GetString(buffer, offset, len));
                offset += len;
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
