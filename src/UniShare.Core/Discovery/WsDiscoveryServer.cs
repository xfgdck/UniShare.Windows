using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace UniShare.Core.Discovery
{
    /// <summary>
    /// 轻量级纯托管 WS-Discovery 服务端 (Web Services Dynamic Discovery)。
    /// 符合 WS-Discovery 2005/04 及 OASIS WSDD 1.1 标准：
    /// 1. UDP 3702: 在每个有效网卡上监听多播探针 (Probe) 与地址解析 (Resolve)，
    ///    仅当探针类型匹配 wsdp:Device / pub:Computer（或未指定类型）时回复 ProbeMatches (OASIS WSDD 1.1 §5.3)，
    ///    多播探针的回复随机延迟 0~500ms (APP_MAX_DELAY, §3.1.3)，XAddrs 使用与请求方同网段的本机地址。
    /// 2. TCP 5357: 提供轻量级 HTTP SOAP 元数据交换端点 (WS-Transfer Get)，
    ///    向 Windows 资源管理器等返回包含 FriendlyName、pub:Computer (主机名/工作组) 的设备元数据。
    /// </summary>
    public class WsDiscoveryServer : IDisposable
    {
        private const string MulticastIPv4 = "239.255.255.250";
        private const int WsdUdpPort = 3702;
        private const int DefaultHttpPort = 5357;
        private const int AppMaxDelayMs = 500;

        private const string NsSoap = "http://www.w3.org/2003/05/soap-envelope";
        private const string NsWsd2005 = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
        private const string NsWsdOasis = "http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01";
        private const string NsWsa2004 = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
        private const string NsWsa2005 = "http://www.w3.org/2005/08/addressing";
        private const string NsDevProf = "http://schemas.xmlsoap.org/ws/2006/02/devprof";
        private const string NsPub = "http://schemas.microsoft.com/windows/pub/2005/07";

        private readonly string m_deviceUuid;
        private readonly string m_instanceId;
        private readonly string m_machineName;
        private readonly string m_workgroup;
        private readonly Action<string, string>? m_logger;
        private readonly object m_sendLock = new object();
        private int m_messageNumber = 1;
        private int m_httpPort = DefaultHttpPort;

        private Socket? m_socket;
        private HttpListener? m_httpListener;
        private CancellationTokenSource? m_cts;
        private bool m_isRunning = false;

        public bool IsRunning => m_isRunning;
        public string DeviceUuid => m_deviceUuid;
        public int HttpPort => m_httpPort;

        public WsDiscoveryServer(Action<string, string>? logger = null)
        {
            m_logger = logger;
            m_machineName = Environment.MachineName.Trim();
            m_workgroup = DiscoveryHelper.GetWorkgroupName();
            // 为当前机器生成稳定的基于机器名的 UUID，避免重启导致设备 ID 变化而在网络邻居生成重复条目
            m_deviceUuid = GenerateStableUuid(m_machineName);
            m_instanceId = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        }

        private static string GenerateStableUuid(string input)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input + "_UniShare_WSD"));
            return new Guid(hash).ToString();
        }

        private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

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

                // 1. 启动 TCP 5357 HTTP SOAP 元数据监听服务 (WS-Transfer Get)
                StartHttpMetadataServer();

                // 2. 启动 UDP 3702 多播探测监听 Socket
                StartUdpSocket();

                m_isRunning = true;
                Log($"WS-Discovery 服务已启动，正在监听 UDP 3702 与 TCP {m_httpPort} (Windows 资源管理器及现代网络设备发现已激活)");

                // 启动后台 UDP 接收循环
                _ = Task.Run(() => ReceiveLoopAsync(m_cts.Token));

                // 发送上线 Hello 宣告
                _ = Task.Run(SendHelloAnnouncement);
            }
            catch (Exception ex)
            {
                m_isRunning = false;
                Log($"启动 WS-Discovery 服务失败: {ex.Message}，Windows 网络邻居自发现可能受限。", "Warning");
            }
        }

        public void Stop()
        {
            if (!m_isRunning) return;
            m_isRunning = false;

            try
            {
                // 发送下线 Bye 宣告
                try { SendByeAnnouncement(); } catch { }

                try { m_cts?.Cancel(); } catch { }

                // 停止 HTTP 服务（直接 Abort 立即强制释放，避免 Stop 同步等待排空）
                try
                {
                    m_httpListener?.Abort();
                    m_httpListener?.Close();
                    m_httpListener = null;
                }
                catch { }

                // 停止 UDP Socket
                try
                {
                    m_socket?.Close();
                    m_socket?.Dispose();
                    m_socket = null;
                }
                catch { }

                try { m_cts?.Dispose(); } catch { }
                m_cts = null;

                Log("WS-Discovery 服务已停止并发送 Bye 下线通知。");
            }
            catch (Exception ex)
            {
                Log($"停止 WS-Discovery 服务异常: {ex.Message}", "Warning");
            }
        }

        #region HTTP SOAP 元数据传输服务 (TCP 5357 / WS-Transfer Get)
        private void StartHttpMetadataServer()
        {
            int[] tryPorts = new[] { DefaultHttpPort, 5358, 5359, 8765 };
            foreach (int port in tryPorts)
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://*:{port}/{m_deviceUuid}/");
                    listener.Start();

                    m_httpListener = listener;
                    m_httpPort = port;
                    _ = Task.Run(() => HttpListenLoopAsync(m_cts!.Token));
                    return;
                }
                catch
                {
                    // 若当前端口被占用或受限，顺延尝试备用端口
                }
            }

            Log("启动 WS-Discovery HTTP 元数据监听失败，网络发现将仅依赖 UDP 探测回应。", "Warning");
        }

        private async Task HttpListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && m_httpListener != null && m_httpListener.IsListening)
            {
                try
                {
                    var context = await m_httpListener.GetContextAsync();
                    _ = Task.Run(() => HandleHttpRequestAsync(context));
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (InvalidOperationException) { break; }
                catch (Exception)
                {
                    if (ct.IsCancellationRequested || m_httpListener == null || !m_httpListener.IsListening) break;
                }
            }
        }

        private async Task HandleHttpRequestAsync(HttpListenerContext context)
        {
            try
            {
                if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    string body = await reader.ReadToEndAsync();
                    var request = ParseSoap(body);

                    // 按请求使用的 WS-Addressing 版本回复
                    string wsaNs = request?.AddressingNamespace ?? NsWsa2004;
                    string responseXml = BuildMetadataGetResponseXml(request?.MessageId ?? string.Empty, wsaNs);
                    byte[] bytes = Encoding.UTF8.GetBytes(responseXml);

                    context.Response.ContentType = "application/soap+xml; charset=utf-8";
                    context.Response.StatusCode = 200;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    context.Response.OutputStream.Close();
                    return;
                }

                context.Response.StatusCode = 405;
                context.Response.Close();
            }
            catch
            {
                try { context.Response.Abort(); } catch { }
            }
        }

        private string BuildMetadataGetResponseXml(string relatesToMessageId, string wsaNs)
        {
            string msgId = Guid.NewGuid().ToString();
            string anonTo = wsaNs == NsWsa2005 ? "http://www.w3.org/2005/08/addressing/anonymous" : "http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous";
            string machine = Escape(m_machineName);
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""{NsSoap}""
               xmlns:wsa=""{wsaNs}""
               xmlns:wsx=""http://schemas.xmlsoap.org/ws/2004/09/mex""
               xmlns:wsdp=""{NsDevProf}""
               xmlns:pub=""{NsPub}"">
  <soap:Header>
    <wsa:To>{anonTo}</wsa:To>
    <wsa:Action>http://schemas.xmlsoap.org/ws/2004/09/transfer/GetResponse</wsa:Action>
    <wsa:MessageID>urn:uuid:{msgId}</wsa:MessageID>
    {(string.IsNullOrEmpty(relatesToMessageId) ? "" : $"<wsa:RelatesTo>{Escape(relatesToMessageId)}</wsa:RelatesTo>")}
  </soap:Header>
  <soap:Body>
    <wsx:Metadata>
      <wsx:MetadataSection Dialect=""{NsDevProf}/ThisDevice"">
        <wsdp:ThisDevice>
          <wsdp:FriendlyName>UniShare ({machine})</wsdp:FriendlyName>
          <wsdp:FirmwareVersion>{Escape(AppVersionInfo.Current)}</wsdp:FirmwareVersion>
          <wsdp:SerialNumber>{m_deviceUuid}</wsdp:SerialNumber>
        </wsdp:ThisDevice>
      </wsx:MetadataSection>
      <wsx:MetadataSection Dialect=""{NsDevProf}/ThisModel"">
        <wsdp:ThisModel>
          <wsdp:Manufacturer>UniShare</wsdp:Manufacturer>
          <wsdp:ModelName>UniShare File Server</wsdp:ModelName>
          <wsdp:ModelNumber>1.0</wsdp:ModelNumber>
        </wsdp:ThisModel>
      </wsx:MetadataSection>
      <wsx:MetadataSection Dialect=""{NsDevProf}/Relationship"">
        <wsdp:Relationship Type=""{NsDevProf}/host"">
          <wsdp:Host>
            <wsa:EndpointReference>
              <wsa:Address>urn:uuid:{m_deviceUuid}</wsa:Address>
            </wsa:EndpointReference>
            <wsdp:Types>pub:Computer</wsdp:Types>
            <wsdp:ServiceId>urn:uuid:{m_deviceUuid}</wsdp:ServiceId>
            <pub:Computer>{machine}/Workgroup:{Escape(m_workgroup)}</pub:Computer>
          </wsdp:Host>
        </wsdp:Relationship>
      </wsx:MetadataSection>
    </wsx:Metadata>
  </soap:Body>
</soap:Envelope>";
        }
        #endregion

        #region SOAP 报文解析
        internal sealed class SoapRequest
        {
            public string Action { get; set; } = string.Empty;
            public string MessageId { get; set; } = string.Empty;
            public string AddressingNamespace { get; set; } = NsWsa2004;
            public XElement? BodyElement { get; set; }
        }

        internal static SoapRequest? ParseSoap(string xml)
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var reader = XmlReader.Create(new StringReader(xml), settings);
                var doc = XDocument.Load(reader);
                var envelope = doc.Root;
                if (envelope == null || envelope.Name.LocalName != "Envelope") return null;

                var header = envelope.Elements().FirstOrDefault(e => e.Name.LocalName == "Header");
                var body = envelope.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
                var action = header?.Elements().FirstOrDefault(e => e.Name.LocalName == "Action");
                var messageId = header?.Elements().FirstOrDefault(e => e.Name.LocalName == "MessageID");

                return new SoapRequest
                {
                    Action = action?.Value.Trim() ?? string.Empty,
                    MessageId = messageId?.Value.Trim() ?? string.Empty,
                    AddressingNamespace = (action ?? messageId)?.Name.NamespaceName == NsWsa2005 ? NsWsa2005 : NsWsa2004,
                    BodyElement = body?.Elements().FirstOrDefault()
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 判断 Probe 是否匹配本设备 (OASIS WSDD 1.1 §5.1)：
        /// Types 未指定或其中每个类型都属于本设备所声明的 {wsdp:Device, pub:Computer}；指定了 Scopes 时本设备不声明作用域，不匹配。
        /// </summary>
        internal static bool ProbeMatchesDevice(XElement probe)
        {
            var typesElement = probe.Elements().FirstOrDefault(e => e.Name.LocalName == "Types");
            var scopesElement = probe.Elements().FirstOrDefault(e => e.Name.LocalName == "Scopes");
            if (scopesElement != null && !string.IsNullOrWhiteSpace(scopesElement.Value))
            {
                return false;
            }

            if (typesElement == null || string.IsNullOrWhiteSpace(typesElement.Value))
            {
                return true;
            }

            foreach (string qname in typesElement.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string prefix = string.Empty;
                string localName = qname;
                int colon = qname.IndexOf(':');
                if (colon >= 0)
                {
                    prefix = qname.Substring(0, colon);
                    localName = qname.Substring(colon + 1);
                }

                XNamespace? ns = typesElement.GetNamespaceOfPrefix(prefix);
                if (ns == null && prefix.Length == 0)
                {
                    ns = typesElement.GetDefaultNamespace();
                }
                string nsName = ns?.NamespaceName ?? string.Empty;

                bool supported = (nsName == NsDevProf && localName == "Device") ||
                                 (nsName == NsPub && localName == "Computer");
                if (!supported)
                {
                    return false;
                }
            }
            return true;
        }
        #endregion

        #region UDP 3702 探测与解析
        private void StartUdpSocket()
        {
            m_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            m_socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            m_socket.Bind(new IPEndPoint(IPAddress.Any, WsdUdpPort));
            m_socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);

            // 在每个有效网卡上分别加入 WS-Discovery IPv4 多播组
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
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[65536];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (!ct.IsCancellationRequested && m_socket != null)
            {
                try
                {
                    var result = await m_socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp);
                    if (result.ReceivedBytes <= 0) continue;
                    if (result.RemoteEndPoint is not IPEndPoint remote) continue;

                    string xml = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
                    ProcessIncomingMessage(xml, remote, ct);
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
                    Log($"WS-Discovery 处理异常: {ex.Message}", "Debug");
                }
            }
        }

        private void ProcessIncomingMessage(string xml, IPEndPoint remote, CancellationToken ct)
        {
            var request = ParseSoap(xml);
            if (request?.BodyElement == null || string.IsNullOrEmpty(request.MessageId)) return;

            var body = request.BodyElement;
            string wsdNs = body.Name.NamespaceName;
            if (wsdNs != NsWsd2005 && wsdNs != NsWsdOasis) return;
            bool isOasis11 = wsdNs == NsWsdOasis;

            if (body.Name.LocalName == "Probe")
            {
                if (!ProbeMatchesDevice(body)) return;

                // 多播探针需在 0~APP_MAX_DELAY 内随机延迟后回复，避免局域网响应风暴
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(Random.Shared.Next(0, AppMaxDelayMs), ct);
                        SendMatch(remote, request.MessageId, isOasis11, isResolve: false);
                    }
                    catch { }
                });
            }
            else if (body.Name.LocalName == "Resolve")
            {
                var address = body.Descendants().FirstOrDefault(e => e.Name.LocalName == "Address");
                if (address != null && string.Equals(address.Value.Trim(), $"urn:uuid:{m_deviceUuid}", StringComparison.OrdinalIgnoreCase))
                {
                    SendMatch(remote, request.MessageId, isOasis11, isResolve: true);
                }
            }
        }

        private void SendMatch(IPEndPoint remote, string relatesToMessageId, bool isOasis11, bool isResolve)
        {
            if (m_socket == null) return;

            string localIp = DiscoveryHelper.SelectLocalAddressFor(remote.Address).ToString();
            int msgNum = Interlocked.Increment(ref m_messageNumber);
            string msgId = Guid.NewGuid().ToString();

            string wsdNs = isOasis11 ? NsWsdOasis : NsWsd2005;
            string wsaNs = isOasis11 ? NsWsa2005 : NsWsa2004;
            string kind = isResolve ? "ResolveMatches" : "ProbeMatches";
            string item = isResolve ? "ResolveMatch" : "ProbeMatch";
            string action = $"{wsdNs}/{kind}";
            string anonTo = isOasis11 ? "http://www.w3.org/2005/08/addressing/anonymous" : "http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous";

            string responseXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""{NsSoap}""
               xmlns:wsa=""{wsaNs}""
               xmlns:wsd=""{wsdNs}""
               xmlns:wsdp=""{NsDevProf}""
               xmlns:pub=""{NsPub}"">
  <soap:Header>
    <wsa:To>{anonTo}</wsa:To>
    <wsa:Action>{action}</wsa:Action>
    <wsa:MessageID>urn:uuid:{msgId}</wsa:MessageID>
    <wsa:RelatesTo>{Escape(relatesToMessageId)}</wsa:RelatesTo>
    <wsd:AppSequence InstanceId=""{m_instanceId}"" MessageNumber=""{msgNum}""/>
  </soap:Header>
  <soap:Body>
    <wsd:{kind}>
      <wsd:{item}>
        <wsa:EndpointReference>
          <wsa:Address>urn:uuid:{m_deviceUuid}</wsa:Address>
        </wsa:EndpointReference>
        <wsd:Types>wsdp:Device pub:Computer</wsd:Types>
        <wsd:XAddrs>http://{localIp}:{m_httpPort}/{m_deviceUuid}/</wsd:XAddrs>
        <wsd:MetadataVersion>1</wsd:MetadataVersion>
      </wsd:{item}>
    </wsd:{kind}>
  </soap:Body>
</soap:Envelope>";

            SendTo(Encoding.UTF8.GetBytes(responseXml), remote, null);
        }

        private void SendHelloAnnouncement()
        {
            foreach (var local in DiscoveryHelper.GetLocalInterfaceAddresses())
            {
                int msgNum = Interlocked.Increment(ref m_messageNumber);
                string helloXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""{NsSoap}""
               xmlns:wsa=""{NsWsa2004}""
               xmlns:wsd=""{NsWsd2005}""
               xmlns:wsdp=""{NsDevProf}""
               xmlns:pub=""{NsPub}"">
  <soap:Header>
    <wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>
    <wsa:Action>{NsWsd2005}/Hello</wsa:Action>
    <wsa:MessageID>urn:uuid:{Guid.NewGuid()}</wsa:MessageID>
    <wsd:AppSequence InstanceId=""{m_instanceId}"" MessageNumber=""{msgNum}""/>
  </soap:Header>
  <soap:Body>
    <wsd:Hello>
      <wsa:EndpointReference>
        <wsa:Address>urn:uuid:{m_deviceUuid}</wsa:Address>
      </wsa:EndpointReference>
      <wsd:Types>wsdp:Device pub:Computer</wsd:Types>
      <wsd:XAddrs>http://{local.Address}:{m_httpPort}/{m_deviceUuid}/</wsd:XAddrs>
      <wsd:MetadataVersion>1</wsd:MetadataVersion>
    </wsd:Hello>
  </soap:Body>
</soap:Envelope>";

                SendTo(Encoding.UTF8.GetBytes(helloXml), new IPEndPoint(IPAddress.Parse(MulticastIPv4), WsdUdpPort), local.Address);
            }
        }

        private void SendByeAnnouncement()
        {
            foreach (var local in DiscoveryHelper.GetLocalInterfaceAddresses())
            {
                int msgNum = Interlocked.Increment(ref m_messageNumber);
                string byeXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""{NsSoap}""
               xmlns:wsa=""{NsWsa2004}""
               xmlns:wsd=""{NsWsd2005}"">
  <soap:Header>
    <wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>
    <wsa:Action>{NsWsd2005}/Bye</wsa:Action>
    <wsa:MessageID>urn:uuid:{Guid.NewGuid()}</wsa:MessageID>
    <wsd:AppSequence InstanceId=""{m_instanceId}"" MessageNumber=""{msgNum}""/>
  </soap:Header>
  <soap:Body>
    <wsd:Bye>
      <wsa:EndpointReference>
        <wsa:Address>urn:uuid:{m_deviceUuid}</wsa:Address>
      </wsa:EndpointReference>
    </wsd:Bye>
  </soap:Body>
</soap:Envelope>";

                SendTo(Encoding.UTF8.GetBytes(byeXml), new IPEndPoint(IPAddress.Parse(MulticastIPv4), WsdUdpPort), local.Address);
            }
        }

        private void SendTo(byte[] bytes, IPEndPoint destination, IPAddress? multicastInterface)
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
                    socket.SendTo(bytes, destination);
                }
            }
            catch { }
        }
        #endregion

        public void Dispose()
        {
            Stop();
        }
    }
}
