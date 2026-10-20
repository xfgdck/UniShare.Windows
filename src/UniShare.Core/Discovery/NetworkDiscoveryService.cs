using System;

namespace UniShare.Core.Discovery
{
    /// <summary>
    /// 局域网设备自发现统筹服务：
    /// 协同运行三维立体自发现体系：
    /// 1. NetBIOS (NBNS / WINS / MS-BRWS)：服务于各类安防摄像头 (海康/大华/小米/萤石等 IPC/NVR) 及旧版 Linux/Samba 客户端；
    /// 2. WS-Discovery (UDP 3702 + TCP 5357 HTTP)：服务于 Windows 10/11 资源管理器网络邻居与现代局域网设备；
    /// 3. mDNS (DNS-SD RFC 6762/6763)：服务于 Apple 生态 (Mac 访达、iPhone/iPad、Apple TV / Infuse) 与 Android TV。
    /// </summary>
    public class NetworkDiscoveryService : IDisposable
    {
        private NetBiosDiscoveryServer? m_netbios;
        private MdnsDiscoveryServer? m_mdns;
        private WsDiscoveryServer? m_wsd;
        private readonly object m_syncLock = new object();
        private bool m_isRunning = false;

        public event EventHandler<LogMessageEventArgs>? LogReceived;

        public bool IsRunning => m_isRunning;
        public bool IsNetBiosRunning => m_netbios?.IsRunning ?? false;
        public bool IsMdnsRunning => m_mdns?.IsRunning ?? false;
        public bool IsWsdRunning => m_wsd?.IsRunning ?? false;

        private void Log(string message, string severity = "Info")
        {
            LogReceived?.Invoke(this, new LogMessageEventArgs
            {
                Time = DateTime.Now,
                Severity = severity,
                Source = "DiscoveryService",
                Message = message
            });
        }

        /// <summary>
        /// 启动局域网设备自发现服务广播（NetBIOS / mDNS / WS-Discovery）
        /// </summary>
        public void Start(int smbPort = 445)
        {
            lock (m_syncLock)
            {
                if (m_isRunning) return;

                Log("正在启动局域网设备自发现体系 (NetBIOS / mDNS / WS-Discovery)...");

                // 1. 启动 NetBIOS 服务（核心解决摄像头与旧设备局域网扫描不到共享的问题）
                try
                {
                    m_netbios = new NetBiosDiscoveryServer((msg, sev) => Log(msg, sev));
                    m_netbios.Start();
                }
                catch (Exception ex)
                {
                    Log($"启动 NetBIOS 异常: {ex.Message}", "Warning");
                }

                // 2. 启动 mDNS 服务（服务于苹果、安卓电视等生态）
                try
                {
                    m_mdns = new MdnsDiscoveryServer(smbPort, (msg, sev) => Log(msg, sev));
                    m_mdns.Start();
                }
                catch (Exception ex)
                {
                    Log($"启动 mDNS 异常: {ex.Message}", "Warning");
                }

                // 3. 启动 WS-Discovery 服务（服务于 Windows 资源管理器网络邻居及现代设备）
                try
                {
                    m_wsd = new WsDiscoveryServer((msg, sev) => Log(msg, sev));
                    m_wsd.Start();
                }
                catch (Exception ex)
                {
                    Log($"启动 WS-Discovery 异常: {ex.Message}", "Warning");
                }

                m_isRunning = true;
                Log("局域网设备自发现体系已就绪（摄像头 NetBIOS 发现、Windows 邻居、苹果生态均已激活）。");
            }
        }

        /// <summary>
        /// 停止局域网设备自发现服务广播并注销所有网络标识
        /// </summary>
        public void Stop()
        {
            lock (m_syncLock)
            {
                if (!m_isRunning) return;

                try
                {
                    m_netbios?.Stop();
                    m_netbios?.Dispose();
                    m_netbios = null;
                }
                catch { }

                try
                {
                    m_mdns?.Stop();
                    m_mdns?.Dispose();
                    m_mdns = null;
                }
                catch { }

                try
                {
                    m_wsd?.Stop();
                    m_wsd?.Dispose();
                    m_wsd = null;
                }
                catch { }

                m_isRunning = false;
                Log("局域网设备自发现服务已停止并清理注册名称。");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
