using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UniShare.Core.Discovery;

namespace UniShare.Core.Tests
{
    [TestClass]
    public class DiscoveryProtocolTests
    {
        [TestMethod]
        public void Mdns_ReadDnsName_FollowsCompressionPointer()
        {
            // 偏移 12: "_smb._tcp.local"；之后的名称 "host" + 指向偏移 17 ("_tcp.local") 的压缩指针
            byte[] first = MdnsDiscoveryServer.EncodeDnsName("_smb._tcp.local");
            byte[] buffer = new byte[12 + first.Length + 7];
            System.Array.Copy(first, 0, buffer, 12, first.Length);
            int second = 12 + first.Length;
            buffer[second] = 4;
            Encoding.ASCII.GetBytes("host").CopyTo(buffer, second + 1);
            buffer[second + 5] = 0xC0;
            buffer[second + 6] = 12 + 5; // "_tcp" 标签起始位置

            int offset = 12;
            Assert.AreEqual("_smb._tcp.local", MdnsDiscoveryServer.ReadDnsName(buffer, ref offset, buffer.Length));
            Assert.AreEqual(second, offset);

            Assert.AreEqual("host._tcp.local", MdnsDiscoveryServer.ReadDnsName(buffer, ref offset, buffer.Length));
            Assert.AreEqual(buffer.Length, offset, "压缩指针之后 offset 应停在指针的 2 字节之后");
        }

        [TestMethod]
        public void Mdns_ReadDnsName_PointerLoopDoesNotHang()
        {
            byte[] buffer = new byte[14];
            buffer[12] = 0xC0;
            buffer[13] = 12; // 指向自身
            int offset = 12;
            MdnsDiscoveryServer.ReadDnsName(buffer, ref offset, buffer.Length);
            Assert.AreEqual(14, offset);
        }

        private static XElement ParseProbe(string types, string scopes = "")
        {
            string xml = $@"<soap:Envelope xmlns:soap=""http://www.w3.org/2003/05/soap-envelope""
  xmlns:wsa=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
  xmlns:wsd=""http://schemas.xmlsoap.org/ws/2005/04/discovery""
  xmlns:wsdp=""http://schemas.xmlsoap.org/ws/2006/02/devprof""
  xmlns:pub=""http://schemas.microsoft.com/windows/pub/2005/07""
  xmlns:wprt=""http://schemas.microsoft.com/windows/2006/08/wdp/print""
  xmlns:dn=""http://www.onvif.org/ver10/network/wsdl"">
  <soap:Header>
    <wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</wsa:Action>
    <wsa:MessageID>urn:uuid:11111111-2222-3333-4444-555555555555</wsa:MessageID>
  </soap:Header>
  <soap:Body>
    <wsd:Probe>{(types == null ? "" : $"<wsd:Types>{types}</wsd:Types>")}{(string.IsNullOrEmpty(scopes) ? "" : $"<wsd:Scopes>{scopes}</wsd:Scopes>")}</wsd:Probe>
  </soap:Body>
</soap:Envelope>";
            var request = WsDiscoveryServer.ParseSoap(xml);
            Assert.IsNotNull(request);
            Assert.AreEqual("urn:uuid:11111111-2222-3333-4444-555555555555", request!.MessageId);
            return request.BodyElement!;
        }

        [TestMethod]
        public void Wsd_ProbeMatching_FollowsDeclaredTypes()
        {
            Assert.IsTrue(WsDiscoveryServer.ProbeMatchesDevice(ParseProbe(null!)), "未指定类型应匹配");
            Assert.IsTrue(WsDiscoveryServer.ProbeMatchesDevice(ParseProbe("")));
            Assert.IsTrue(WsDiscoveryServer.ProbeMatchesDevice(ParseProbe("wsdp:Device")));
            Assert.IsTrue(WsDiscoveryServer.ProbeMatchesDevice(ParseProbe("wsdp:Device pub:Computer")));
            Assert.IsFalse(WsDiscoveryServer.ProbeMatchesDevice(ParseProbe("wsdp:Device wprt:PrintDeviceType")), "打印机探针不应匹配");
            Assert.IsFalse(WsDiscoveryServer.ProbeMatchesDevice(ParseProbe("dn:NetworkVideoTransmitter")), "ONVIF 摄像头探针不应匹配");
            Assert.IsFalse(WsDiscoveryServer.ProbeMatchesDevice(ParseProbe("wsdp:Device", "onvif://www.onvif.org/type")), "带作用域的探针不应匹配");
        }

        [TestMethod]
        public void Wsd_ParseSoap_RejectsInvalidXml()
        {
            Assert.IsNull(WsDiscoveryServer.ParseSoap("<not-closed"));
            Assert.IsNull(WsDiscoveryServer.ParseSoap("<!DOCTYPE x [<!ENTITY a \"b\">]><Envelope/>"), "禁止 DTD 处理");
        }

        [TestMethod]
        public void NetBios_HostAnnouncement_IsWellFormed()
        {
            var server = new NetBiosDiscoveryServer();
            byte[] packet = server.BuildHostAnnouncementPacket(IPAddress.Parse("192.168.1.10"));

            // RFC 1002 §4.4.1 数据报头
            Assert.AreEqual(0x11, packet[0]);
            CollectionAssert.AreEqual(new byte[] { 192, 168, 1, 10 }, packet[4..8]);
            Assert.AreEqual(138, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2)));
            Assert.AreEqual(packet.Length - 14, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10, 2)), "DGM_LENGTH");
            Assert.AreEqual(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12, 2)), "PACKET_OFFSET");
            Assert.AreEqual(32, packet[14]);
            Assert.AreEqual(32, packet[48]);

            // SMB_COM_TRANSACTION 邮槽写入
            int smb = 14 + 34 + 34;
            Assert.AreEqual(0xFF, packet[smb]);
            Assert.AreEqual(0x25, packet[smb + 4]);
            Assert.AreEqual(17, packet[smb + 32], "WordCount");
            int words = smb + 33;
            ushort dataCount = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(words + 22, 2));
            ushort dataOffset = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(words + 24, 2));
            Assert.AreEqual(3, packet[words + 26], "SetupCount");
            Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(words + 28, 2)), "Mailslot Write opcode");

            int nameStart = words + 34 + 2;
            Assert.AreEqual("\\MAILSLOT\\BROWSE\0", Encoding.ASCII.GetString(packet, nameStart, 17));

            // [MS-BRWS] 2.2.1 HostAnnouncement
            int announce = smb + dataOffset;
            Assert.AreEqual(packet.Length - announce, dataCount);
            Assert.AreEqual(0x01, packet[announce], "Opcode");
            Assert.AreEqual(0x0F, packet[announce + 28], "BrowserConfigVersionMajor");
            Assert.AreEqual(0x01, packet[announce + 29], "BrowserConfigVersionMinor");
            Assert.AreEqual(0xAA55, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(announce + 30, 2)), "Signature");
            Assert.AreEqual(0, packet[^1], "注释以 NULL 结尾");
        }

        [TestMethod]
        public void LocalInterfaceAddress_SubnetMatching()
        {
            var local = new LocalInterfaceAddress(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"),
                IPAddress.Parse("192.168.1.255"), 1, new byte[6]);
            Assert.IsTrue(local.IsSameSubnet(IPAddress.Parse("192.168.1.200")));
            Assert.IsFalse(local.IsSameSubnet(IPAddress.Parse("192.168.2.10")));
        }
    }
}
