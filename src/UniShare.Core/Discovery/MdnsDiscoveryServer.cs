using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UniShare.Core.Discovery
{
    /// <summary>
    /// 基于 RFC 6762 (Multicast DNS) 与 RFC 6763 (DNS-SD) 的轻量级纯托管 mDNS 服务端。
    /// 向局域网广播与响应 _smb._tcp.local 服务，让 Android 设备与安卓智能电视 (Kodi/VLC) 等能自动发现当前主机。
    /// 规范要点：
    /// - 在每个有效网卡上加入 224.0.0.251 多播组，并按收到查询的网卡回复该网卡自身的 A 记录 (RFC 6762 §6.2 / §15)；
    /// - 共享记录 (PTR) 不设置 cache-flush 位，唯一记录 (SRV/TXT/A) 设置 cache-flush 位 (RFC 6762 §10.2)；
    /// - QU 位查询单播回复 (§5.4)；源端口非 5353 的传统单次查询以单播回复，回显 ID 与问题段且 TTL ≤ 10 秒 (§6.7)；
    /// - 同一网卡上的多播回复间隔不少于 1 秒 (§6)；
    /// - 支持 _services._dns-sd._udp.local 服务类型枚举 (RFC 6763 §9)。
    /// </summary>
    public class MdnsDiscoveryServer : IDisposable
    {
        private const string MulticastIPv4 = "224.0.0.251";
        private const int MdnsPort = 5353;
        private const ushort TypeA = 1;
        private const ushort TypePtr = 12;
        private const ushort TypeTxt = 16;
        private const ushort TypeSrv = 33;
        private const ushort TypeAny = 255;
        private const ushort ClassIn = 1;
        private const ushort ClassInFlush = 0x8001; // Class IN with Cache-Flush bit
        private const uint DefaultTtl = 120;
        private const uint LegacyUnicastTtl = 10;

        private const string ServiceType = "_smb._tcp.local";
        private const string ServicesEnumeration = "_services._dns-sd._udp.local";

        private readonly string m_instanceName;
        private readonly string m_hostName;
        private readonly int m_smbPort;
        private readonly Action<string, string>? m_logger;
        private readonly object m_sendLock = new object();
        private readonly Dictionary<string, DateTime> m_lastMulticast = new Dictionary<string, DateTime>();

        private Socket? m_socket;
        private CancellationTokenSource? m_cts;
        private Timer? m_heartbeatTimer;
        private bool m_isRunning = false;

        public bool IsRunning => m_isRunning;

        private string ServiceInstance => $"{m_instanceName}.{ServiceType}";
        private string HostTarget => $"{m_hostName}.local";

        public MdnsDiscoveryServer(int smbPort = 445, Action<string, string>? logger = null)
        {
            m_smbPort = smbPort;
            m_logger = logger;

            string rawMachineName = Environment.MachineName;
            string name = string.IsNullOrWhiteSpace(rawMachineName) ? "UniShare" : rawMachineName.Trim();
            // DNS 单个标签最长 63 字节 (RFC 1035 §2.3.4)
            m_hostName = TruncateLabel(name);
            m_instanceName = m_hostName;
        }

        private static string TruncateLabel(string label)
        {
            while (Encoding.UTF8.GetByteCount(label) > 63)
            {
                label = label.Substring(0, label.Length - 1);
            }
            return label;
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
                m_socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                m_socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

                // 在每个有效网卡上分别加入 mDNS IPv4 多播组（仅用 IPAddress.Any 时系统只会在默认网卡上加入）
                var group = IPAddress.Parse(MulticastIPv4);
                int joined = 0;
                foreach (var local in DiscoveryHelper.GetLocalInterfaceAddresses())
                {
                    try
                    {
                        m_socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, local.Address));
                        joined++;
                    }
                    catch { }
                }
                if (joined == 0)
                {
                    m_socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, IPAddress.Any));
                }

                m_isRunning = true;
                Log($"mDNS 服务已启动，正在监听 UDP 5353 (广播服务: {ServiceInstance}，已在 {Math.Max(joined, 1)} 个网卡上加入多播组)");

                // 启动后台监听循环
                _ = Task.Run(() => ReceiveLoopAsync(m_cts.Token));

                // 初始连续宣告（RFC 6762 §8.3：至少两次，间隔逐次加倍）
                var token = m_cts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        AnnounceOnAllInterfaces(DefaultTtl);
                        await Task.Delay(1000, token);
                        AnnounceOnAllInterfaces(DefaultTtl);
                        await Task.Delay(2000, token);
                        AnnounceOnAllInterfaces(DefaultTtl);
                    }
                    catch { }
                });

                // 心跳定时器：每 60 秒刷新宣告一次，维持客户端缓存有效性（TTL 120 秒）
                m_heartbeatTimer = new Timer(_ =>
                {
                    if (m_isRunning)
                    {
                        try { AnnounceOnAllInterfaces(DefaultTtl); }
                        catch { }
                    }
                }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            }
            catch (Exception ex)
            {
                m_isRunning = false;
                try { m_socket?.Dispose(); } catch { }
                m_socket = null;
                Log($"启动 mDNS 服务失败: {ex.Message}，局域网安卓与电视设备自发现可能受限。", "Warning");
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

                // 发送 Goodbye 数据包 (TTL=0)，通知客户端立即清除缓存 (RFC 6762 §10.1)
                try { AnnounceOnAllInterfaces(0); } catch { }

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
            byte[] buffer = new byte[9000];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (!ct.IsCancellationRequested && m_socket != null)
            {
                try
                {
                    var result = await m_socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, remoteEp);
                    if (result.ReceivedBytes < 12) continue;
                    if (result.RemoteEndPoint is not IPEndPoint remote) continue;

                    HandleQuery(buffer, result.ReceivedBytes, remote, result.PacketInformation.Interface);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.MessageSize)
                {
                    // ICMP 端口不可达等瞬时错误，继续接收
                }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || m_socket == null) break;
                    Log($"mDNS 数据包处理异常: {ex.Message}", "Debug");
                }
            }
        }

        [Flags]
        private enum AnswerSet
        {
            None = 0,
            ServicePtr = 1,
            SrvTxt = 2,
            HostA = 4,
            ServicesEnum = 8
        }

        private void HandleQuery(byte[] buffer, int length, IPEndPoint remote, int interfaceIndex)
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2));
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2));
            // 仅处理标准查询 (QR=0, OPCODE=0)，忽略其他响应者的应答报文
            if ((flags & 0x8000) != 0 || (flags & 0x7800) != 0) return;

            ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2));
            if (qdCount == 0) return;

            var answers = AnswerSet.None;
            bool unicastRequested = false;
            var answeredQuestions = new List<(string Name, ushort Type)>();

            int offset = 12;
            for (int i = 0; i < qdCount; i++)
            {
                string name = ReadDnsName(buffer, ref offset, length);
                if (offset + 4 > length) break;
                ushort qtype = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
                ushort qclass = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 2, 2));
                offset += 4;

                var matched = MatchQuestion(name, qtype);
                if (matched != AnswerSet.None)
                {
                    answers |= matched;
                    answeredQuestions.Add((name, qtype));
                    if ((qclass & 0x8000) != 0) unicastRequested = true; // QU 位
                }
            }

            if (answers == AnswerSet.None) return;

            // 回复中的地址记录必须是收到查询的网卡自身地址
            var interfaces = DiscoveryHelper.GetLocalInterfaceAddresses();
            var local = interfaces.FirstOrDefault(a => a.InterfaceIndex == interfaceIndex && a.IsSameSubnet(remote.Address))
                        ?? interfaces.FirstOrDefault(a => a.InterfaceIndex == interfaceIndex)
                        ?? DiscoveryHelper.SelectInterfaceFor(remote.Address, interfaces);
            if (local == null) return;

            bool legacyUnicast = remote.Port != MdnsPort;
            if (legacyUnicast)
            {
                byte[] packet = BuildResponse(answers, local.Address, LegacyUnicastTtl, legacy: true, id, answeredQuestions);
                SendTo(packet, remote, null);
            }
            else if (unicastRequested)
            {
                byte[] packet = BuildResponse(answers, local.Address, DefaultTtl, legacy: false, 0, null);
                SendTo(packet, remote, null);
            }
            else
            {
                // 限速按“网卡 + 回答的记录集合”区分（RFC 6762 §6 针对同一记录），避免不同查询的回复互相抑制
                string key = $"{local.Address}|{(int)answers}";
                lock (m_lastMulticast)
                {
                    if (m_lastMulticast.TryGetValue(key, out DateTime last) && (DateTime.UtcNow - last).TotalMilliseconds < 1000)
                    {
                        return;
                    }
                    m_lastMulticast[key] = DateTime.UtcNow;
                }
                byte[] packet = BuildResponse(answers, local.Address, DefaultTtl, legacy: false, 0, null);
                SendTo(packet, new IPEndPoint(IPAddress.Parse(MulticastIPv4), MdnsPort), local.Address);
            }
        }

        private AnswerSet MatchQuestion(string name, ushort qtype)
        {
            bool any = qtype == TypeAny;
            if (NameEquals(name, ServiceType) && (qtype == TypePtr || any))
            {
                return AnswerSet.ServicePtr;
            }
            if (NameEquals(name, ServicesEnumeration) && (qtype == TypePtr || any))
            {
                return AnswerSet.ServicesEnum;
            }
            if (NameEquals(name, ServiceInstance) && (qtype == TypeSrv || qtype == TypeTxt || any))
            {
                return AnswerSet.SrvTxt;
            }
            if (NameEquals(name, HostTarget) && (qtype == TypeA || any))
            {
                return AnswerSet.HostA;
            }
            return AnswerSet.None;
        }

        private static bool NameEquals(string a, string b)
        {
            return string.Equals(a.TrimEnd('.'), b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 在每个网卡上分别多播完整的服务宣告（各网卡只宣告自身地址）
        /// </summary>
        private void AnnounceOnAllInterfaces(uint ttl)
        {
            if (m_socket == null) return;

            var group = new IPEndPoint(IPAddress.Parse(MulticastIPv4), MdnsPort);
            foreach (var local in DiscoveryHelper.GetLocalInterfaceAddresses())
            {
                byte[] packet = BuildResponse(AnswerSet.ServicePtr, local.Address, ttl, legacy: false, 0, null);
                SendTo(packet, group, local.Address);
            }
        }

        private void SendTo(byte[] packet, IPEndPoint destination, IPAddress? multicastInterface)
        {
            var socket = m_socket;
            if (socket == null) return;

            try
            {
                lock (m_sendLock)
                {
                    if (multicastInterface != null)
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, multicastInterface.GetAddressBytes());
                    }
                    socket.SendTo(packet, destination);
                }
            }
            catch { }
        }

        /// <summary>
        /// 构造 mDNS 响应报文。ServicePtr 查询时 PTR 作为回答，SRV/TXT/A 放入附加记录段 (RFC 6763 §12.1)。
        /// </summary>
        private byte[] BuildResponse(AnswerSet answers, IPAddress address, uint ttl, bool legacy, ushort id, List<(string Name, ushort Type)>? questions)
        {
            var answerRecords = new List<byte[]>();
            var additionalRecords = new List<byte[]>();
            // 传统单播回复不得设置 cache-flush 位 (RFC 6762 §6.7)
            ushort uniqueClass = legacy ? ClassIn : ClassInFlush;

            if (answers.HasFlag(AnswerSet.ServicesEnum))
            {
                answerRecords.Add(BuildRecord(ServicesEnumeration, TypePtr, ClassIn, ttl, EncodeDnsName(ServiceType)));
            }

            if (answers.HasFlag(AnswerSet.ServicePtr))
            {
                answerRecords.Add(BuildRecord(ServiceType, TypePtr, ClassIn, ttl, EncodeDnsName(ServiceInstance)));
                additionalRecords.Add(BuildSrv(uniqueClass, ttl));
                additionalRecords.Add(BuildTxt(uniqueClass, ttl));
                additionalRecords.Add(BuildA(uniqueClass, ttl, address));
            }
            else if (answers.HasFlag(AnswerSet.SrvTxt))
            {
                answerRecords.Add(BuildSrv(uniqueClass, ttl));
                answerRecords.Add(BuildTxt(uniqueClass, ttl));
                additionalRecords.Add(BuildA(uniqueClass, ttl, address));
            }

            if (answers.HasFlag(AnswerSet.HostA) && !answers.HasFlag(AnswerSet.ServicePtr) && !answers.HasFlag(AnswerSet.SrvTxt))
            {
                answerRecords.Add(BuildA(uniqueClass, ttl, address));
            }
            else if (answers.HasFlag(AnswerSet.HostA))
            {
                // A 记录已在附加段中，提升为回答
                var a = additionalRecords[additionalRecords.Count - 1];
                additionalRecords.RemoveAt(additionalRecords.Count - 1);
                answerRecords.Add(a);
            }

            using MemoryStream ms = new MemoryStream();
            WriteUInt16(ms, legacy ? id : (ushort)0);
            WriteUInt16(ms, 0x8400); // Response, Authoritative
            WriteUInt16(ms, (ushort)(legacy && questions != null ? questions.Count : 0));
            WriteUInt16(ms, (ushort)answerRecords.Count);
            WriteUInt16(ms, 0);
            WriteUInt16(ms, (ushort)additionalRecords.Count);

            if (legacy && questions != null)
            {
                foreach (var (name, type) in questions)
                {
                    WriteDnsName(ms, name.TrimEnd('.'));
                    WriteUInt16(ms, type);
                    WriteUInt16(ms, ClassIn);
                }
            }

            foreach (var r in answerRecords) ms.Write(r, 0, r.Length);
            foreach (var r in additionalRecords) ms.Write(r, 0, r.Length);
            return ms.ToArray();
        }

        private byte[] BuildSrv(ushort rrClass, uint ttl)
        {
            byte[] target = EncodeDnsName(HostTarget);
            byte[] rdata = new byte[6 + target.Length];
            BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(0, 2), 0); // Priority
            BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(2, 2), 0); // Weight
            BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(4, 2), (ushort)m_smbPort);
            Array.Copy(target, 0, rdata, 6, target.Length);
            return BuildRecord(ServiceInstance, TypeSrv, rrClass, ttl, rdata);
        }

        private byte[] BuildTxt(ushort rrClass, uint ttl)
        {
            // 无附加属性时 TXT 记录必须包含单个零长度字符串 (RFC 6763 §6.1)
            return BuildRecord(ServiceInstance, TypeTxt, rrClass, ttl, new byte[] { 0 });
        }

        private byte[] BuildA(ushort rrClass, uint ttl, IPAddress address)
        {
            return BuildRecord(HostTarget, TypeA, rrClass, ttl, address.GetAddressBytes());
        }

        private static byte[] BuildRecord(string name, ushort type, ushort rrClass, uint ttl, byte[] rdata)
        {
            using MemoryStream ms = new MemoryStream();
            WriteDnsName(ms, name);
            WriteUInt16(ms, type);
            WriteUInt16(ms, rrClass);
            WriteUInt32(ms, ttl);
            WriteUInt16(ms, (ushort)rdata.Length);
            ms.Write(rdata, 0, rdata.Length);
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

        internal static byte[] EncodeDnsName(string name)
        {
            using MemoryStream ms = new MemoryStream();
            string[] parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(part);
                int len = Math.Min(bytes.Length, 63);
                ms.WriteByte((byte)len);
                ms.Write(bytes, 0, len);
            }
            ms.WriteByte(0);
            return ms.ToArray();
        }

        /// <summary>
        /// 读取 DNS 名称，支持压缩指针 (RFC 1035 §4.1.4)；offset 前进到名称在原位置之后
        /// </summary>
        internal static string ReadDnsName(byte[] buffer, ref int offset, int length)
        {
            StringBuilder sb = new StringBuilder();
            int pos = offset;
            bool jumped = false;
            int jumps = 0;

            while (pos < length)
            {
                byte len = buffer[pos];
                if (len == 0)
                {
                    pos++;
                    break;
                }

                if ((len & 0xC0) == 0xC0)
                {
                    if (pos + 1 >= length || ++jumps > 16) break;
                    int pointer = ((len & 0x3F) << 8) | buffer[pos + 1];
                    if (!jumped)
                    {
                        offset = pos + 2;
                        jumped = true;
                    }
                    pos = pointer;
                    continue;
                }

                pos++;
                if (pos + len > length) break;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.UTF8.GetString(buffer, pos, len));
                pos += len;
            }

            if (!jumped)
            {
                offset = pos;
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
