using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UniShare.UI.Helpers
{
    public class NetworkInterfaceInfo
    {
        public string Name { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        public override string ToString() => $"{Name}: {IpAddress}";
    }

    public static class NetworkHelper
    {
        /// <summary>
        /// 获取本机所有活动的物理网卡 IPv4 地址及网卡信息（过滤虚拟网卡、VPN、容器、隧道等）
        /// </summary>
        public static List<NetworkInterfaceInfo> GetActiveIPv4Interfaces()
        {
            var results = new List<NetworkInterfaceInfo>();

            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                var physicalInterfaces = new List<NetworkInterface>();
                var fallbackInterfaces = new List<NetworkInterface>();

                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    if (UniShare.Core.Discovery.DiscoveryHelper.IsPhysicalInterface(ni))
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
                    foreach (var addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            // 排除链路本地地址 169.254.x.x（DHCP 未获取到地址时的临时地址，其他设备无法访问）
                            byte[] bytes = addr.Address.GetAddressBytes();
                            if (bytes[0] == 169 && bytes[1] == 254)
                                continue;

                            results.Add(new NetworkInterfaceInfo
                            {
                                Name = ni.Name,
                                Description = ni.Description,
                                IpAddress = addr.Address.ToString()
                            });
                        }
                    }
                }
            }
            catch
            {
                // 网络查询异常回退
            }

            if (results.Count == 0)
            {
                results.Add(new NetworkInterfaceInfo
                {
                    Name = "本地回环",
                    Description = "Loopback",
                    IpAddress = "127.0.0.1"
                });
            }

            return results;
        }
    }
}
