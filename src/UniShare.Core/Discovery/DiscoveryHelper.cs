using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace UniShare.Core.Discovery
{
    public static class DiscoveryHelper
    {
        /// <summary>
        /// 判断指定网卡是否为物理网卡（过滤掉虚拟网卡、VPN、容器、隧道、蓝牙等适配器）
        /// </summary>
        public static bool IsPhysicalInterface(NetworkInterface ni)
        {
            if (ni == null) return false;

            // 1. 物理局域网网卡类型必须为以太网或无线网卡
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                ni.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet)
            {
                return false;
            }

            string desc = ni.Description?.ToLowerInvariant() ?? string.Empty;
            string name = ni.Name?.ToLowerInvariant() ?? string.Empty;

            // 2. 常见虚拟网卡、VPN、容器、隧道、蓝牙与热点关键字黑名单
            string[] virtualKeywords = new[]
            {
                "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "vbox",
                "zerotier", "tailscale", "wireguard", "wintun", "tap-", "tap0", "tun",
                "wsl", "bluetooth", "pseudo", "tunnel", "vpn", "host-only", "teredo",
                "isatap", "loopback", "wi-fi direct", "switch", "container", "docker",
                "npcap", "pcap", "wan miniport", "remote", "ras"
            };

            foreach (var kw in virtualKeywords)
            {
                if (desc.Contains(kw) || name.Contains(kw))
                {
                    return false;
                }
            }

            // 3. 深入 Windows 注册表校验 PnPInstanceId（物理网卡在 Windows 下通常以 PCI\ 或 USB\ 开头）
            try
            {
                string subKey = $@"SYSTEM\CurrentControlSet\Control\Network\{{4D36E972-E325-11CE-BFC1-08002BE10318}}\{ni.Id}\Connection";
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
                if (key != null)
                {
                    var pnpId = key.GetValue("PnPInstanceId") as string;
                    if (!string.IsNullOrEmpty(pnpId))
                    {
                        if (pnpId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
                            pnpId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase) ||
                            pnpId.StartsWith("BTH\\", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        if (pnpId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
                            pnpId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        // 虚拟网卡（例如 Hyper-V vEthernet）通常没有 PnPInstanceId
                        return false;
                    }
                }
            }
            catch
            {
                // 注册表读取受限时，基于上述严谨的关键字过滤
            }

            return true;
        }

        /// <summary>
        /// 获取本机所有活动的局域网物理网卡 IPv4 地址信息（含子网掩码、网卡索引、广播地址与 MAC），
        /// 排除本地回环与链路本地 169.254.x.x 地址；无物理网卡时回退到其他活动网卡
        /// </summary>
        public static List<LocalInterfaceAddress> GetLocalInterfaceAddresses()
        {
            // 自发现服务在每个收到的数据包上都会查询网卡（含注册表读取），短时缓存以降低繁忙局域网下的开销
            lock (s_interfaceCacheLock)
            {
                if (s_interfaceCache != null && DateTime.UtcNow - s_interfaceCacheTime < InterfaceCacheDuration)
                {
                    return new List<LocalInterfaceAddress>(s_interfaceCache);
                }
            }

            var results = QueryLocalInterfaceAddresses();
            lock (s_interfaceCacheLock)
            {
                s_interfaceCache = results;
                s_interfaceCacheTime = DateTime.UtcNow;
            }
            return new List<LocalInterfaceAddress>(results);
        }

        /// <summary>
        /// 清除网卡地址缓存（网络环境变化时调用）
        /// </summary>
        public static void InvalidateInterfaceCache()
        {
            lock (s_interfaceCacheLock)
            {
                s_interfaceCache = null;
            }
        }

        private static readonly object s_interfaceCacheLock = new object();
        private static readonly TimeSpan InterfaceCacheDuration = TimeSpan.FromSeconds(5);
        private static List<LocalInterfaceAddress>? s_interfaceCache;
        private static DateTime s_interfaceCacheTime;

        private static List<LocalInterfaceAddress> QueryLocalInterfaceAddresses()
        {
            var results = new List<LocalInterfaceAddress>();

            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                var physicalInterfaces = new List<NetworkInterface>();
                var fallbackInterfaces = new List<NetworkInterface>();

                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    if (IsPhysicalInterface(ni))
                    {
                        physicalInterfaces.Add(ni);
                    }
                    else
                    {
                        fallbackInterfaces.Add(ni);
                    }
                }

                var targetInterfaces = physicalInterfaces.Count > 0 ? physicalInterfaces : fallbackInterfaces;

                foreach (var ni in targetInterfaces)
                {
                    var ipProps = ni.GetIPProperties();
                    int index = -1;
                    try { index = ipProps.GetIPv4Properties()?.Index ?? -1; } catch { }
                    byte[] mac = ni.GetPhysicalAddress().GetAddressBytes();

                    foreach (var addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        byte[] bytes = addr.Address.GetAddressBytes();
                        // 排除 127.x.x.x 与 169.254.x.x
                        if (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254))
                            continue;

                        IPAddress mask = addr.IPv4Mask ?? IPAddress.Parse("255.255.255.0");
                        byte[] maskBytes = mask.GetAddressBytes();
                        byte[] broadcastBytes = new byte[4];
                        for (int i = 0; i < 4; i++)
                        {
                            broadcastBytes[i] = (byte)(bytes[i] | ~maskBytes[i]);
                        }

                        results.Add(new LocalInterfaceAddress(addr.Address, mask, new IPAddress(broadcastBytes), index,
                            mac.Length == 6 ? mac : new byte[6]));
                    }
                }
            }
            catch { }

            return results;
        }

        /// <summary>
        /// 获取本机所有活动的局域网物理网卡 IPv4 地址（排除本地回环 127.0.0.1 及链路本地 169.254.x.x 地址）
        /// </summary>
        public static List<IPAddress> GetLocalIPv4Addresses()
        {
            var results = GetLocalInterfaceAddresses().Select(a => a.Address).ToList();
            if (results.Count == 0)
            {
                results.Add(IPAddress.Loopback);
            }
            return results;
        }

        /// <summary>
        /// 获取首选的主要局域网 IPv4 地址
        /// </summary>
        public static IPAddress GetPrimaryIPv4Address()
        {
            var ips = GetLocalIPv4Addresses();
            return ips.Count > 0 ? ips[0] : IPAddress.Loopback;
        }

        /// <summary>
        /// 为指定的远端地址选择同一子网的本机网卡地址（多网卡时保证返回对方可达的 IP），找不到时返回首选地址
        /// </summary>
        public static LocalInterfaceAddress? SelectInterfaceFor(IPAddress? remote, List<LocalInterfaceAddress>? candidates = null)
        {
            var list = candidates ?? GetLocalInterfaceAddresses();
            if (list.Count == 0) return null;

            if (remote != null && remote.AddressFamily == AddressFamily.InterNetwork)
            {
                foreach (var local in list)
                {
                    if (local.IsSameSubnet(remote))
                    {
                        return local;
                    }
                }
            }
            return list[0];
        }

        /// <summary>
        /// 为指定的远端地址选择同一子网的本机 IPv4 地址
        /// </summary>
        public static IPAddress SelectLocalAddressFor(IPAddress? remote)
        {
            return SelectInterfaceFor(remote)?.Address ?? IPAddress.Loopback;
        }

        /// <summary>
        /// 获取局域网各有效物理网卡的子网定向广播地址
        /// </summary>
        public static List<IPAddress> GetBroadcastAddresses()
        {
            return GetLocalInterfaceAddresses().Select(a => a.Broadcast).Distinct().ToList();
        }

        /// <summary>
        /// 获取主物理活动网卡的 MAC 地址（6 字节）
        /// </summary>
        public static byte[] GetPrimaryMacAddress()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;

                    if (!IsPhysicalInterface(ni))
                        continue;

                    byte[] mac = ni.GetPhysicalAddress().GetAddressBytes();
                    if (mac != null && mac.Length == 6)
                    {
                        return mac;
                    }
                }
            }
            catch { }

            return new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        }

        /// <summary>
        /// 获取当前工作组名称（默认 WORKGROUP）
        /// </summary>
        public static string GetWorkgroupName()
        {
            try
            {
                string? domain = Environment.GetEnvironmentVariable("USERDOMAIN");
                if (!string.IsNullOrWhiteSpace(domain) &&
                    !string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    return domain.Trim();
                }
            }
            catch { }

            return "WORKGROUP";
        }

        /// <summary>
        /// 格式化 NetBIOS 16 字节专用名称（前 15 字符大写空格右补齐，第 16 字节为指定服务后缀）
        /// </summary>
        public static byte[] FormatNetBiosName(string name, byte suffix)
        {
            byte[] buffer = new byte[16];
            for (int i = 0; i < 15; i++) buffer[i] = (byte)' ';
            byte[] raw = Encoding.ASCII.GetBytes((name ?? string.Empty).Trim().ToUpperInvariant());
            int copyLen = Math.Min(raw.Length, 15);
            Array.Copy(raw, buffer, copyLen);
            buffer[15] = suffix;
            return buffer;
        }
    }

    /// <summary>
    /// 本机单个网卡 IPv4 地址信息
    /// </summary>
    public sealed class LocalInterfaceAddress
    {
        public LocalInterfaceAddress(IPAddress address, IPAddress mask, IPAddress broadcast, int interfaceIndex, byte[] mac)
        {
            Address = address;
            Mask = mask;
            Broadcast = broadcast;
            InterfaceIndex = interfaceIndex;
            Mac = mac;
        }

        public IPAddress Address { get; }
        public IPAddress Mask { get; }
        public IPAddress Broadcast { get; }
        public int InterfaceIndex { get; }
        public byte[] Mac { get; }

        public bool IsSameSubnet(IPAddress other)
        {
            byte[] a = Address.GetAddressBytes();
            byte[] b = other.GetAddressBytes();
            byte[] m = Mask.GetAddressBytes();
            if (b.Length != 4) return false;
            for (int i = 0; i < 4; i++)
            {
                if ((a[i] & m[i]) != (b[i] & m[i])) return false;
            }
            return true;
        }
    }
}
