using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UniShare.Core.Discovery
{
    /// <summary>
    /// 轻量级纯托管 WS-Discovery 服务端 (Web Services Dynamic Discovery)。
    /// 符合 WS-Discovery 2005/04 及 OASIS 标准：
    /// 1. UDP 3702: 监听多播探针 (Probe) 与地址解析 (Resolve)，快速响应 ProbeMatches/ResolveMatches。
    /// 2. TCP 5357: 提供轻量级 HTTP SOAP 元数据交换端点 (WS-Transfer Get)，
    ///    向 Windows 资源管理器、安防摄像头及智能设备返回包含 FriendlyName、pub:Computer (主机名/工作组) 的设备元数据。
    /// </summary>
    public class WsDiscoveryServer : IDisposable
    {
        private const string MulticastIPv4 = "239.255.255.250";
        private const int WsdUdpPort = 3702;
        private const int DefaultHttpPort = 5357;

        private readonly string m_deviceUuid;
        private readonly string m_instanceId;
        private readonly string m_machineName;
        private readonly string m_workgroup;
        private readonly Action<string, string>? m_logger;
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
                _ = Task.Run(() => SendHelloAnnouncement());
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
                    string msgId = ExtractMessageId(body);

                    // 构造返回 WS-Transfer GetResponse 元数据 XML
                    string responseXml = BuildMetadataGetResponseXml(msgId);
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

        private string BuildMetadataGetResponseXml(string relatesToMessageId)
        {
            string msgId = Guid.NewGuid().ToString();
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://www.w3.org/2003/05/soap-envelope""
               xmlns:wsa=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
               xmlns:wsx=""http://schemas.xmlsoap.org/ws/2004/09/mex""
               xmlns:wsdp=""http://schemas.xmlsoap.org/ws/2006/02/devprof""
               xmlns:pub=""http://schemas.microsoft.com/windows/pub/2005/07"">
  <soap:Header>
    <wsa:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsa:To>
    <wsa:Action>http://schemas.xmlsoap.org/ws/2004/09/transfer/GetResponse</wsa:Action>
    <wsa:MessageID>urn:uuid:{msgId}</wsa:MessageID>
    {(string.IsNullOrEmpty(relatesToMessageId) ? "" : $"<wsa:RelatesTo>{relatesToMessageId}</wsa:RelatesTo>")}
  </soap:Header>
  <soap:Body>
    <wsx:Metadata>
      <wsx:MetadataSection Dialect=""http://schemas.xmlsoap.org/ws/2006/02/devprof/ThisDevice"">
        <wsdp:ThisDevice>
          <wsdp:FriendlyName>UniShare ({m_machineName})</wsdp:FriendlyName>
          <wsdp:FirmwareVersion>1.0</wsdp:FirmwareVersion>
          <wsdp:SerialNumber>{m_deviceUuid}</wsdp:SerialNumber>
        </wsdp:ThisDevice>
      </wsx:MetadataSection>
      <wsx:MetadataSection Dialect=""http://schemas.xmlsoap.org/ws/2006/02/devprof/ThisModel"">
        <wsdp:ThisModel>
          <wsdp:Manufacturer>UniShare</wsdp:Manufacturer>
          <wsdp:ModelName>UniShare File Server</wsdp:ModelName>
          <wsdp:ModelNumber>1.0</wsdp:ModelNumber>
        </wsdp:ThisModel>
      </wsx:MetadataSection>
      <wsx:MetadataSection Dialect=""http://schemas.xmlsoap.org/ws/2006/02/devprof/Relationship"">
        <wsdp:Relationship Type=""http://schemas.xmlsoap.org/ws/2006/02/devprof/host"">
          <wsdp:Host>
            <wsa:EndpointReference>
              <wsa:Address>urn:uuid:{m_deviceUuid}</wsa:Address>
            </wsa:EndpointReference>
            <wsdp:Types>pub:Computer</wsdp:Types>
            <wsdp:ServiceId>urn:uuid:{m_deviceUuid}</wsdp:ServiceId>
            <pub:Computer>{m_machineName}/Workgroup:{m_workgroup}</pub:Computer>
          </wsdp:Host>
        </wsdp:Relationship>
      </wsx:MetadataSection>
    </wsx:Metadata>
  </soap:Body>
</soap:Envelope>";
        }
        #endregion

        #region UDP 3702 探测与解析
        private void StartUdpSocket()
        {
            m_socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            m_socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            m_socket.Bind(new IPEndPoint(IPAddress.Any, WsdUdpPort));

            // 加入 WS-Discovery IPv4 多播组
            m_socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse(MulticastIPv4), IPAddress.Any));
            m_socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[8192];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (!ct.IsCancellationRequested && m_socket != null)
            {
                try
                {
                    var result = await m_socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp);
                    if (result.ReceivedBytes <= 0) continue;

                    string xml = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
                    ProcessIncomingMessage(xml, result.RemoteEndPoint);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || m_socket == null) break;
                    Log($"WS-Discovery 处理异常: {ex.Message}", "Debug");
                }
            }
        }

        private void ProcessIncomingMessage(string xml, EndPoint remoteEndPoint)
        {
            try
            {
                // 1. 处理 Probe 请求 (Windows 资源管理器搜索设备、摄像头扫描局域网共享)
                if (xml.Contains("Probe", StringComparison.OrdinalIgnoreCase) &&
                    !xml.Contains("ProbeMatches", StringComparison.OrdinalIgnoreCase))
                {
                    string messageId = ExtractMessageId(xml);
                    if (string.IsNullOrEmpty(messageId)) return;

                    // 兼容各厂商摄像头及 Windows 资源管理器的探针格式（无 Types 通配探测、pub:Computer、Device、Storage 等）
                    bool isTargetType = !xml.Contains("<wsd:Types>") && !xml.Contains("<d:Types>") && !xml.Contains("<Types>") && !xml.Contains(":Types>") ||
                                        xml.Contains("Computer", StringComparison.OrdinalIgnoreCase) ||
                                        xml.Contains("Device", StringComparison.OrdinalIgnoreCase) ||
                                        xml.Contains("Storage", StringComparison.OrdinalIgnoreCase) ||
                                        xml.Contains("NetworkVideoTransmitter", StringComparison.OrdinalIgnoreCase);

                    bool isOasis11 = xml.Contains("http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01", StringComparison.OrdinalIgnoreCase);
                    if (isTargetType)
                    {
                        SendProbeMatch(remoteEndPoint, messageId, isOasis11);
                    }
                }
                // 2. 处理 Resolve 请求 (针对特定 UUID 或计算机名进行终结点解析)
                else if (xml.Contains("Resolve", StringComparison.OrdinalIgnoreCase) &&
                         !xml.Contains("ResolveMatches", StringComparison.OrdinalIgnoreCase))
                {
                    if (xml.Contains(m_deviceUuid, StringComparison.OrdinalIgnoreCase) ||
                        xml.Contains(m_machineName, StringComparison.OrdinalIgnoreCase))
                    {
                        string messageId = ExtractMessageId(xml);
                        if (!string.IsNullOrEmpty(messageId))
                        {
                            bool isOasis11 = xml.Contains("http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01", StringComparison.OrdinalIgnoreCase);
                            SendResolveMatch(remoteEndPoint, messageId, isOasis11);
                        }
                    }
                }
            }
            catch { }
        }

        private static string ExtractMessageId(string xml)
        {
            var match = Regex.Match(xml, @"<(?:wsa:|a:)?MessageID[^>]*>([^<]+)</(?:wsa:|a:)?MessageID>", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private void SendProbeMatch(EndPoint remoteEndPoint, string relatesToMessageId, bool isOasis11 = false)
        {
            if (m_socket == null) return;

            string localIp = DiscoveryHelper.GetPrimaryIPv4Address().ToString();
            int msgNum = Interlocked.Increment(ref m_messageNumber);
            string msgId = Guid.NewGuid().ToString();

            string wsdNs = isOasis11 ? "http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01" : "http://schemas.xmlsoap.org/ws/2005/04/discovery";
            string wsaNs = isOasis11 ? "http://www.w3.org/2005/08/addressing" : "http://schemas.xmlsoap.org/ws/2004/08/addressing";
            string action = isOasis11 ? "http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01/ProbeMatches" : "http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches";
            string anonTo = isOasis11 ? "http://www.w3.org/2005/08/addressing/anonymous" : "http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous";

            string responseXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://www.w3.org/2003/05/soap-envelope""
               xmlns:wsa=""{wsaNs}""
               xmlns:wsd=""{wsdNs}""
               xmlns:wsdp=""http://schemas.xmlsoap.org/ws/2006/02/devprof""
               xmlns:pub=""http://schemas.microsoft.com/windows/pub/2005/07"">
  <soap:Header>
    <wsa:To>{anonTo}</wsa:To>
    <wsa:Action>{action}</wsa:Action>
    <wsa:MessageID>urn:uuid:{msgId}</wsa:MessageID>
    <wsa:RelatesTo>{relatesToMessageId}</wsa:RelatesTo>
    <wsd:AppSequence InstanceId=""{m_instanceId}"" MessageNumber=""{msgNum}""/>
  </soap:Header>
  <soap:Body>
    <wsd:ProbeMatches>
      <wsd:ProbeMatch>
        <wsa:EndpointReference>
          <wsa:Address>urn:uuid:{m_deviceUuid}</wsa:Address>
        </wsa:EndpointReference>
        <wsd:Types>wsdp:Device pub:Computer</wsd:Types>
        <wsd:Scopes>ldap: net.pipe:</wsd:Scopes>
        <wsd:XAddrs>http://{localIp}:{m_httpPort}/{m_deviceUuid}/</wsd:XAddrs>
        <wsd:MetadataVersion>1</wsd:MetadataVersion>
      </wsd:ProbeMatch>
    </wsd:ProbeMatches>
  </soap:Body>
</soap:Envelope>";

            byte[] bytes = Encoding.UTF8.GetBytes(responseXml);
            try
            {
                m_socket.SendTo(bytes, remoteEndPoint);
            }
            catch { }
        }

        private void SendResolveMatch(EndPoint remoteEndPoint, string relatesToMessageId, bool isOasis11 = false)
        {
            if (m_socket == null) return;

            string localIp = DiscoveryHelper.GetPrimaryIPv4Address().ToString();
            int msgNum = Interlocked.Increment(ref m_messageNumber);
            string msgId = Guid.NewGuid().ToString();

            string wsdNs = isOasis11 ? "http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01" : "http://schemas.xmlsoap.org/ws/2005/04/discovery";
            string wsaNs = isOasis11 ? "http://www.w3.org/2005/08/addressing" : "http://schemas.xmlsoap.org/ws/2004/08/addressing";
            string action = isOasis11 ? "http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01/ResolveMatches" : "http://schemas.xmlsoap.org/ws/2005/04/discovery/ResolveMatches";
            string anonTo = isOasis11 ? "http://www.w3.org/2005/08/addressing/anonymous" : "http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous";

            string responseXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://www.w3.org/2003/05/soap-envelope""
               xmlns:wsa=""{wsaNs}""
               xmlns:wsd=""{wsdNs}""
               xmlns:wsdp=""http://schemas.xmlsoap.org/ws/2006/02/devprof""
               xmlns:pub=""http://schemas.microsoft.com/windows/pub/2005/07"">
  <soap:Header>
    <wsa:To>{anonTo}</wsa:To>
    <wsa:Action>{action}</wsa:Action>
    <wsa:MessageID>urn:uuid:{msgId}</wsa:MessageID>
    <wsa:RelatesTo>{relatesToMessageId}</wsa:RelatesTo>
    <wsd:AppSequence InstanceId=""{m_instanceId}"" MessageNumber=""{msgNum}""/>
  </soap:Header>
  <soap:Body>
    <wsd:ResolveMatches>
      <wsd:ResolveMatch>
        <wsa:EndpointReference>
          <wsa:Address>urn:uuid:{m_deviceUuid}</wsa:Address>
        </wsa:EndpointReference>
        <wsd:Types>wsdp:Device pub:Computer</wsd:Types>
        <wsd:Scopes>ldap: net.pipe:</wsd:Scopes>
        <wsd:XAddrs>http://{localIp}:{m_httpPort}/{m_deviceUuid}/</wsd:XAddrs>
        <wsd:MetadataVersion>1</wsd:MetadataVersion>
      </wsd:ResolveMatch>
    </wsd:ResolveMatches>
  </soap:Body>
</soap:Envelope>";

            byte[] bytes = Encoding.UTF8.GetBytes(responseXml);
            try
            {
                m_socket.SendTo(bytes, remoteEndPoint);
            }
            catch { }
        }

        private void SendHelloAnnouncement()
        {
            if (m_socket == null) return;

            string localIp = DiscoveryHelper.GetPrimaryIPv4Address().ToString();
            int msgNum = Interlocked.Increment(ref m_messageNumber);
            string msgId = Guid.NewGuid().ToString();

            string helloXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://www.w3.org/2003/05/soap-envelope""
               xmlns:wsa=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
               xmlns:wsd=""http://schemas.xmlsoap.org/ws/2005/04/discovery""
               xmlns:wsdp=""http://schemas.xmlsoap.org/ws/2006/02/devprof""
               xmlns:pub=""http://schemas.microsoft.com/windows/pub/2005/07"">
  <soap:Header>
    <wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>
    <wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Hello</wsa:Action>
    <wsa:MessageID>urn:uuid:{msgId}</wsa:MessageID>
    <wsd:AppSequence InstanceId=""{m_instanceId}"" MessageNumber=""{msgNum}""/>
  </soap:Header>
  <soap:Body>
    <wsd:Hello>
      <wsa:EndpointReference>
        <wsa:Address>urn:uuid:{m_deviceUuid}</wsa:Address>
      </wsa:EndpointReference>
      <wsd:Types>wsdp:Device pub:Computer</wsd:Types>
      <wsd:Scopes>ldap: net.pipe:</wsd:Scopes>
      <wsd:XAddrs>http://{localIp}:{m_httpPort}/{m_deviceUuid}/</wsd:XAddrs>
      <wsd:MetadataVersion>1</wsd:MetadataVersion>
    </wsd:Hello>
  </soap:Body>
</soap:Envelope>";

            byte[] bytes = Encoding.UTF8.GetBytes(helloXml);
            IPEndPoint dest = new IPEndPoint(IPAddress.Parse(MulticastIPv4), WsdUdpPort);
            try
            {
                m_socket.SendTo(bytes, dest);
            }
            catch { }
        }

        private void SendByeAnnouncement()
        {
            if (m_socket == null) return;

            int msgNum = Interlocked.Increment(ref m_messageNumber);
            string msgId = Guid.NewGuid().ToString();

            string byeXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://www.w3.org/2003/05/soap-envelope""
               xmlns:wsa=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
               xmlns:wsd=""http://schemas.xmlsoap.org/ws/2005/04/discovery"">
  <soap:Header>
    <wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>
    <wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Bye</wsa:Action>
    <wsa:MessageID>urn:uuid:{msgId}</wsa:MessageID>
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

            byte[] bytes = Encoding.UTF8.GetBytes(byeXml);
            IPEndPoint dest = new IPEndPoint(IPAddress.Parse(MulticastIPv4), WsdUdpPort);
            try
            {
                m_socket.SendTo(bytes, dest);
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
