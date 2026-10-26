using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UniShare.Core.Discovery
{
    /// <summary>
    /// 局域网 NetBIOS (NBNS / 浏览服务) 自发现服务端（RFC 1001/1002、[MS-NBTE]、[MS-BRWS]）。
    /// 解决安防摄像头 / NVR 及旧版 Linux 客户端通过 NetBIOS (UDP 137) 扫描局域网 NAS 时，
    /// 因系统 Server 服务被停用、缺少 0x20 (文件服务器) 名称而搜不到本设备的问题：
    /// 1. 托管式 UDP 137 名称服务响应器：响应针对本机名 (&lt;00&gt; / &lt;20&gt;) 的名称查询，
    ///    以及针对通配名 '*' 或本机名的节点状态 (NBSTAT) 查询，回复中包含 [主机名&lt;20&gt;] 文件服务器标识；
    ///    地址与 MAC 按请求方所在网段选择对应网卡。
    /// 2. 定时向各网卡子网广播 UDP 138 浏览服务主机通告 (HostAnnouncement, [MS-BRWS] 2.2.1)，使主机呈现在网上邻居列表。
    /// </summary>
    public class NetBiosDiscoveryServer : IDisposable
    {
        private const int NetBiosNamePort = 137;
        private const int NetBiosDatagramPort = 138;
        private const uint AnnouncePeriodMs = 60 * 1000;

        private readonly string m_hostName;
        private readonly string m_workgroup;
        private readonly Action<string, string>? m_logger;

        private Socket? m_nameSocket;
        private Socket? m_dgmSocket;
        private CancellationTokenSource? m_cts;
        private Timer? m_announceTimer;
        private bool m_isRunning = false;

        public bool IsRunning => m_isRunning;
        public string HostName => m_hostName;
        public string Workgroup => m_workgroup;

        public NetBiosDiscoveryServer(Action<string, string>? logger = null)
        {
            m_logger = logger;
            string rawName = Environment.MachineName;
            m_hostName = string.IsNullOrWhiteSpace(rawName) ? "UNISHARE" : rawName.Trim().ToUpperInvariant();
            m_workgroup = DiscoveryHelper.GetWorkgroupName().ToUpperInvariant();
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

                // 1. 启动 UDP 137 名字服务监听与响应循环 (处理安防摄像头及设备 NBSTAT [20h] 和 NB 名字查询)
                StartNameServiceSocket();

                // 2. 启动 UDP 138 网上邻居浏览器通告定时器 (MS-BRWS 主机通告)
                StartDatagramService();

                m_isRunning = true;
                Log($"NetBIOS 服务已启动，已激活 [{m_hostName}<20>] 文件服务器标识响应并监听 UDP 137/138 (安防摄像头扫描支持已就绪)");
            }
            catch (Exception ex)
            {
                m_isRunning = false;
                Log($"启动 NetBIOS 自发现服务异常: {ex.Message}，部分摄像头局域网扫描可能受限。", "Warning");
            }
        }

        public void Stop()
        {
            if (!m_isRunning) return;
            m_isRunning = false;

            try
            {
                m_announceTimer?.Dispose();
                m_announceTimer = null;

                try { m_cts?.Cancel(); } catch { }

                // 关闭 Socket
                try { m_nameSocket?.Close(); m_nameSocket?.Dispose(); m_nameSocket = null; } catch { }
                try { m_dgmSocket?.Close(); m_dgmSocket?.Dispose(); m_dgmSocket = null; } catch { }

                try { m_cts?.Dispose(); } catch { }
                m_cts = null;

                Log("NetBIOS 服务已停止。");
            }
            catch (Exception ex)
            {
                Log($"停止 NetBIOS 服务异常: {ex.Message}", "Warning");
            }
        }

        #region UDP 137 名字服务监听与处理
        private void StartNameServiceSocket()
        {
            try
            {
                m_nameSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                m_nameSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                m_nameSocket.EnableBroadcast = true;
                m_nameSocket.Bind(new IPEndPoint(IPAddress.Any, NetBiosNamePort));

                _ = Task.Run(() => NameReceiveLoopAsync(m_cts!.Token));
            }
            catch (Exception ex)
            {
                Log($"绑定 NetBIOS UDP 137 端口受限: {ex.Message}，内核驱动将继续提供单播节点响应。", "Debug");
            }
        }

        private async Task NameReceiveLoopAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[2048];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (!ct.IsCancellationRequested && m_nameSocket != null)
            {
                try
                {
                    var result = await m_nameSocket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp);
                    if (result.ReceivedBytes < 12) continue;

                    ProcessNameRequest(buffer, result.ReceivedBytes, result.RemoteEndPoint);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || m_nameSocket == null) break;
                    Log($"NetBIOS 数据包解析异常: {ex.Message}", "Debug");
                }
            }
        }

        private void ProcessNameRequest(byte[] buffer, int length, EndPoint remoteEndPoint)
        {
            try
            {
                ushort trnId = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2));
                ushort flags = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2));

                // 仅处理请求 (QR 位为 0)
                if ((flags & 0x8000) != 0) return;

                ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2));
                if (qdCount == 0) return;

                int offset = 12;
                if (offset >= length) return;

                byte labelLen = buffer[offset];
                if (labelLen != 32 || offset + 1 + labelLen > length) return;

                // 提取 RFC 1001/1002 编码的问题名称并解码
                byte[] rawEncoded = new byte[34];
                Array.Copy(buffer, offset, rawEncoded, 0, Math.Min(34, length - offset));

                byte[] name16 = DecodeNetBiosName(buffer, offset + 1, 32);
                offset += 1 + labelLen;

                // 跳过 0 字节结尾
                if (offset < length && buffer[offset] == 0) offset++;
                if (offset + 4 > length) return;

                ushort qtype = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
                ushort qclass = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 2, 2));

                string nameStr = Encoding.ASCII.GetString(name16, 0, 15).Trim();
                byte suffix = name16[15];

                // 仅处理名称查询操作码 (OPCODE = 0)
                if (((flags >> 11) & 0x0F) != 0) return;

                IPAddress? remoteAddress = (remoteEndPoint as IPEndPoint)?.Address;
                var local = DiscoveryHelper.SelectInterfaceFor(remoteAddress);
                if (local == null) return;

                bool isWildcard = name16[0] == (byte)'*';
                bool isHostName = string.Equals(nameStr, m_hostName, StringComparison.OrdinalIgnoreCase);

                // 1. 处理 NBSTAT (节点状态探测，如摄像头扫描局域网各 IP 是否为文件服务器)：通配名 '*' 或本机名
                if (qtype == 0x0021)
                {
                    if (isWildcard || isHostName)
                    {
                        byte[] response = BuildNodeStatusResponse(trnId, rawEncoded, local.Mac);
                        m_nameSocket?.SendTo(response, remoteEndPoint);
                    }
                }
                // 2. 处理 NB 名称查询：仅回答本机名的工作站 <00> 与文件服务器 <20> 名称（通配名只适用于 NBSTAT，RFC 1002 §4.2.12）
                else if (qtype == 0x0020)
                {
                    if (isHostName && (suffix == 0x00 || suffix == 0x20))
                    {
                        bool recursionDesired = (flags & 0x0100) != 0;
                        byte[] response = BuildNameQueryResponse(trnId, rawEncoded, local.Address, recursionDesired);
                        m_nameSocket?.SendTo(response, remoteEndPoint);
                    }
                }
            }
            catch { }
        }

        private static byte[] DecodeNetBiosName(byte[] buffer, int offset, int length)
        {
            byte[] result = new byte[16];
            for (int i = 0; i < 16 && (offset + i * 2 + 1) < buffer.Length; i++)
            {
                int c1 = buffer[offset + i * 2] - 'A';
                int c2 = buffer[offset + i * 2 + 1] - 'A';
                result[i] = (byte)(((c1 & 0x0F) << 4) | (c2 & 0x0F));
            }
            return result;
        }

        /// <summary>
        /// 构造 RFC 1002 节点状态响应包 (NBSTAT: 包含 0x20 文件服务器标识及 MAC 地址)
        /// </summary>
        private byte[] BuildNodeStatusResponse(ushort trnId, byte[] rawQuestionName, byte[] mac)
        {
            byte[] serverName = DiscoveryHelper.FormatNetBiosName(m_hostName, 0x20); // 0x20: File Server
            byte[] workstationName = DiscoveryHelper.FormatNetBiosName(m_hostName, 0x00); // 0x00: Workstation
            byte[] workgroupName = DiscoveryHelper.FormatNetBiosName(m_workgroup, 0x00); // 0x00: Workgroup
            if (mac == null || mac.Length != 6) mac = DiscoveryHelper.GetPrimaryMacAddress();

            // 头部 (12) + 名字(34) + 类型(2) + 类别(2) + TTL(4) + 长度(2) + 数据[数量(1) + 3*18 + 统计(46)]
            int rdLength = 1 + (3 * 18) + 46;
            byte[] packet = new byte[12 + 34 + 2 + 2 + 4 + 2 + rdLength];
            int offset = 0;

            // 1. Header
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), trnId); offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0x8400); offset += 2; // Response, Authoritative
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0); offset += 2;      // QDCount
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 1); offset += 2;      // ANCount
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0); offset += 2;      // NSCount
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0); offset += 2;      // ARCount

            // 2. Answer RR
            int nameLen = Math.Min(34, rawQuestionName.Length);
            Array.Copy(rawQuestionName, 0, packet, offset, nameLen);
            offset += 34;

            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0x0021); offset += 2; // NBSTAT
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0x0001); offset += 2; // Class IN
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(offset, 4), 0); offset += 4;      // TTL
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), (ushort)rdLength); offset += 2;

            // 3. RDATA
            packet[offset++] = 3; // Name count: 3

            // 条目 1: Workstation <00>
            Array.Copy(workstationName, 0, packet, offset, 16); offset += 16;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0x0400); offset += 2; // Unique, Active

            // 条目 2: Server Service <20> (关键所在：声明本机提供 SMB 文件共享服务)
            Array.Copy(serverName, 0, packet, offset, 16); offset += 16;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0x0400); offset += 2; // Unique, Active

            // 条目 3: Workgroup <00>
            Array.Copy(workgroupName, 0, packet, offset, 16); offset += 16;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0x8400); offset += 2; // Group, Active

            // 4. Statistics (46 字节，前 6 字节为 MAC)
            Array.Copy(mac, 0, packet, offset, 6);
            offset += 46;

            return packet;
        }

        /// <summary>
        /// 构造正向名字查询响应包 (NB: 返回本机局域网 IPv4 地址)
        /// </summary>
        private static byte[] BuildNameQueryResponse(ushort trnId, byte[] rawQuestionName, IPAddress address, bool recursionDesired)
        {
            byte[] ipBytes = address.GetAddressBytes();
            byte[] packet = new byte[12 + 34 + 2 + 2 + 4 + 2 + 6];
            int offset = 0;

            // Header
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), trnId); offset += 2;
            // POSITIVE NAME QUERY RESPONSE (RFC 1002 §4.2.13)：R=1, AA=1，RD 回显请求值
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), (ushort)(recursionDesired ? 0x8500 : 0x8400)); offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0); offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 1); offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0); offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0); offset += 2;

            // Answer RR
            int nameLen = Math.Min(34, rawQuestionName.Length);
            Array.Copy(rawQuestionName, 0, packet, offset, nameLen);
            offset += 34;

            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0x0020); offset += 2; // NB
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0x0001); offset += 2; // Class IN
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(offset, 4), 300); offset += 4;    // TTL: 300s
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 6); offset += 2;      // RDLENGTH: 6

            // RDATA: Flags(2) + IP(4)
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0x0000); offset += 2; // B-node
            Array.Copy(ipBytes, 0, packet, offset, 4);

            return packet;
        }
        #endregion

        #region UDP 138 网上邻居浏览器主机通告 (MS-BRWS)
        private void StartDatagramService()
        {
            try
            {
                m_dgmSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                m_dgmSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                m_dgmSocket.EnableBroadcast = true;

                // 启动即刻连续广播 3 次，随后每 60 秒刷新一次
                _ = Task.Run(async () =>
                {
                    try
                    {
                        SendHostAnnouncement();
                        await Task.Delay(200, m_cts!.Token);
                        SendHostAnnouncement();
                        await Task.Delay(500, m_cts!.Token);
                        SendHostAnnouncement();
                    }
                    catch { }
                });

                m_announceTimer = new Timer(_ =>
                {
                    if (m_isRunning)
                    {
                        try { SendHostAnnouncement(); }
                        catch { }
                    }
                }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            }
            catch { }
        }

        private void SendHostAnnouncement()
        {
            if (m_dgmSocket == null) return;

            // 逐网卡向各自子网定向广播，数据报头中的源 IP 使用该网卡地址
            foreach (var local in DiscoveryHelper.GetLocalInterfaceAddresses())
            {
                try
                {
                    byte[] packet = BuildHostAnnouncementPacket(local.Address);
                    m_dgmSocket.SendTo(packet, new IPEndPoint(local.Broadcast, NetBiosDatagramPort));
                }
                catch { }
            }
        }

        /// <summary>
        /// 组装 DIRECT_GROUP 数据报 (RFC 1002 §4.4.1)，承载发往 &lt;工作组&gt;&lt;1D&gt; 的 \MAILSLOT\BROWSE 主机通告
        /// </summary>
        internal byte[] BuildHostAnnouncementPacket(IPAddress sourceIp)
        {
            byte[] host16 = DiscoveryHelper.FormatNetBiosName(m_hostName, 0x00);
            byte[] workgroup16 = DiscoveryHelper.FormatNetBiosName(m_workgroup, 0x1D); // 1D: Local Master Browser
            byte[] encSource = EncodeFirstLevelNetBios(host16);
            byte[] encDest = EncodeFirstLevelNetBios(workgroup16);

            byte[] smbPayload = BuildMailslotPayload(Encoding.ASCII.GetBytes("UniShare SMB Server\0"));

            const int headerLength = 14;
            int dgmLength = 34 + 34 + smbPayload.Length; // DGM_LENGTH：源名称 + 目的名称 + 用户数据
            byte[] packet = new byte[headerLength + dgmLength];
            int offset = 0;

            // 1. NetBIOS Datagram Header (14 bytes)
            packet[offset++] = 0x11; // MSG_TYPE: DIRECT_GROUP DATAGRAM
            packet[offset++] = 0x02; // FLAGS: F=1 (首个分片), M=0, SNT=00 (B 节点)
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), (ushort)Random.Shared.Next(1, 65535)); offset += 2; // DGM_ID
            Array.Copy(sourceIp.GetAddressBytes(), 0, packet, offset, 4); offset += 4; // SOURCE_IP
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), NetBiosDatagramPort); offset += 2; // SOURCE_PORT
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), (ushort)dgmLength); offset += 2; // DGM_LENGTH
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0); offset += 2; // PACKET_OFFSET

            // 2. Source Name (34 bytes)
            packet[offset++] = 32;
            Array.Copy(encSource, 0, packet, offset, 32); offset += 32;
            packet[offset++] = 0;

            // 3. Destination Name (34 bytes)
            packet[offset++] = 32;
            Array.Copy(encDest, 0, packet, offset, 32); offset += 32;
            packet[offset++] = 0;

            // 4. SMB Payload
            Array.Copy(smbPayload, 0, packet, offset, smbPayload.Length);

            return packet;
        }

        /// <summary>
        /// 组装 SMB_COM_TRANSACTION 邮槽写入请求 ([MS-CIFS] 2.2.4.33.1)，数据为 HostAnnouncement ([MS-BRWS] 2.2.1)
        /// </summary>
        internal byte[] BuildMailslotPayload(byte[] commentBytes)
        {
            byte[] mailslotBytes = Encoding.ASCII.GetBytes("\\MAILSLOT\\BROWSE\0");

            // HostAnnouncement 数据段：固定 32 字节 + 以 NULL 结尾的注释
            byte[] hostAnnounce = new byte[32 + commentBytes.Length];
            int aOff = 0;
            hostAnnounce[aOff++] = 0x01; // Opcode: HostAnnouncement
            hostAnnounce[aOff++] = 0x00; // UpdateCount（保留，置 0）
            BinaryPrimitives.WriteUInt32LittleEndian(hostAnnounce.AsSpan(aOff, 4), AnnouncePeriodMs); aOff += 4; // Periodicity (ms)

            byte[] srvNameBytes = new byte[16];
            byte[] rawName = Encoding.ASCII.GetBytes(m_hostName);
            Array.Copy(rawName, srvNameBytes, Math.Min(rawName.Length, 15));
            Array.Copy(srvNameBytes, 0, hostAnnounce, aOff, 16); aOff += 16; // ServerName（NULL 填充）

            hostAnnounce[aOff++] = 10; // OSVersionMajor
            hostAnnounce[aOff++] = 0;  // OSVersionMinor
            BinaryPrimitives.WriteUInt32LittleEndian(hostAnnounce.AsSpan(aOff, 4), 0x00001003); aOff += 4; // ServerType: Workstation | Server | NT
            hostAnnounce[aOff++] = 0x0F; // BrowserConfigVersionMajor: MUST be 0x0F
            hostAnnounce[aOff++] = 0x01; // BrowserConfigVersionMinor: MUST be 0x01
            BinaryPrimitives.WriteUInt16LittleEndian(hostAnnounce.AsSpan(aOff, 2), 0xAA55); aOff += 2; // Signature
            Array.Copy(commentBytes, 0, hostAnnounce, aOff, commentBytes.Length);

            const int smbHeaderLength = 32;
            const int wordCount = 17; // 14 个固定参数字 + 3 个 Setup 字
            int dataOffset = smbHeaderLength + 1 + wordCount * 2 + 2 + mailslotBytes.Length;
            byte[] smb = new byte[dataOffset + hostAnnounce.Length];
            int sOff = 0;

            // SMB Header (32 bytes)：Protocol + Command，其余字段为 0
            smb[sOff++] = 0xFF; smb[sOff++] = 0x53; smb[sOff++] = 0x4D; smb[sOff++] = 0x42;
            smb[sOff++] = 0x25; // SMB_COM_TRANSACTION
            sOff = smbHeaderLength;

            // SMB Parameters
            smb[sOff++] = wordCount;
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), 0); sOff += 2;                               // TotalParameterCount
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), (ushort)hostAnnounce.Length); sOff += 2;     // TotalDataCount
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), 0); sOff += 2;                               // MaxParameterCount
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), 0); sOff += 2;                               // MaxDataCount
            smb[sOff++] = 0;                                                                                           // MaxSetupCount
            smb[sOff++] = 0;                                                                                           // Reserved1
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), 0x0002); sOff += 2;                          // Flags: NO_RESPONSE
            BinaryPrimitives.WriteUInt32LittleEndian(smb.AsSpan(sOff, 4), 0); sOff += 4;                               // Timeout
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), 0); sOff += 2;                               // Reserved2
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), 0); sOff += 2;                               // ParameterCount
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), (ushort)dataOffset); sOff += 2;              // ParameterOffset
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), (ushort)hostAnnounce.Length); sOff += 2;     // DataCount
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), (ushort)dataOffset); sOff += 2;              // DataOffset
            smb[sOff++] = 3;                                                                                           // SetupCount
            smb[sOff++] = 0;                                                                                           // Reserved3
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), 0x0001); sOff += 2;                          // Setup[0]: Opcode = Mailslot Write
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), 0x0001); sOff += 2;                          // Setup[1]: Priority
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), 0x0002); sOff += 2;                          // Setup[2]: Class 2 (不可靠 / 广播)

            // SMB Data
            BinaryPrimitives.WriteUInt16LittleEndian(smb.AsSpan(sOff, 2), (ushort)(mailslotBytes.Length + hostAnnounce.Length)); sOff += 2; // ByteCount
            Array.Copy(mailslotBytes, 0, smb, sOff, mailslotBytes.Length); sOff += mailslotBytes.Length;
            Array.Copy(hostAnnounce, 0, smb, sOff, hostAnnounce.Length);

            return smb;
        }

        private static byte[] EncodeFirstLevelNetBios(byte[] name16)
        {
            byte[] encoded = new byte[32];
            for (int i = 0; i < 16; i++)
            {
                byte b = name16[i];
                encoded[i * 2] = (byte)('A' + ((b >> 4) & 0x0F));
                encoded[i * 2 + 1] = (byte)('A' + (b & 0x0F));
            }
            return encoded;
        }
        #endregion

        public void Dispose()
        {
            Stop();
        }
    }
}
