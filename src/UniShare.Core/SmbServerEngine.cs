using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Collections.Generic;
using SMBLibrary;
using SMBLibrary.Server;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Win32;
using UniShare.Core.Models;
using UniShare.Core.Discovery;

namespace UniShare.Core
{
    public class LogMessageEventArgs : EventArgs
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public string Severity { get; set; } = "Info";
        public string Source { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class SmbServerEngine
    {
        /// <summary>
        /// 运行期生效的共享快照：共享配置 + 实际挂载的物理路径
        /// </summary>
        private sealed class ActiveShare
        {
            public ActiveShare(ShareConfig config, string fullPath)
            {
                Config = config;
                FullPath = fullPath;
            }

            public ShareConfig Config { get; }
            public string FullPath { get; }
        }

        private CustomSMBServer? m_server;
        private readonly ServerConfig m_config;
        private readonly NetworkDiscoveryService m_discoveryService = new NetworkDiscoveryService();
        private bool m_isRunning = false;
        private bool m_smb1Enabled = false;

        private readonly object m_configLock = new object();

        // 串行化服务停止与自发现启停，避免异步启动 / 网络变化刷新在服务停止后又把自发现拉起
        private readonly object m_discoveryLock = new object();
        private Dictionary<string, string> m_userMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool m_anyShareAllowsGuest = false;

        // 权限回调按共享名实时查询该表，热重载替换整张表后，已建立 TreeConnect 的旧连接也会立即按新配置判定
        private Dictionary<string, ActiveShare> m_activeShares = new Dictionary<string, ActiveShare>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<LogMessageEventArgs>? LogReceived;
        public event EventHandler<bool>? StatusChanged;

        public bool IsRunning => m_isRunning;
        public ServerConfig Config => m_config;
        public NetworkDiscoveryService DiscoveryService => m_discoveryService;

        public SmbServerEngine(ServerConfig config)
        {
            m_config = config ?? throw new ArgumentNullException(nameof(config));
            m_discoveryService.LogReceived += (sender, args) =>
            {
                LogReceived?.Invoke(this, args);
            };
        }

        private void Log(string message, string severity = "Info", string source = "ServerEngine")
        {
            LogReceived?.Invoke(this, new LogMessageEventArgs
            {
                Time = DateTime.Now,
                Severity = severity,
                Source = source,
                Message = message
            });
        }

        /// <summary>
        /// 组装共享文件夹、细粒度权限以及独立用户认证字典
        /// </summary>
        private (SMBShareCollection Shares, Dictionary<string, ActiveShare> ActiveShares, Dictionary<string, string> UserMap, bool AnyShareAllowsGuest) BuildSharesAndUsers(ServerConfig config)
        {
            SMBShareCollection shares = new SMBShareCollection();
            var activeShares = new Dictionary<string, ActiveShare>(StringComparer.OrdinalIgnoreCase);

            foreach (var share in config.Shares)
            {
                if (string.IsNullOrWhiteSpace(share.LocalPath))
                {
                    Log($"共享 [{share.ShareName}] 本地路径为空，跳过挂载。", "Warning");
                    continue;
                }

                if (!share.AllowGuest && !ShareAccessEvaluator.HasConfiguredUsers(share))
                {
                    Log($"共享 [{share.ShareName}] 未添加任何用户且未开启匿名访问，跳过挂载。", "Warning");
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(share.LocalPath);
                }
                catch (Exception ex)
                {
                    Log($"共享 [{share.ShareName}] 本地路径无效: {ex.Message}，跳过挂载。", "Error");
                    continue;
                }

                // 不自动创建目录：移动硬盘未接入或路径输入错误时，自动创建会产生错误的空目录
                if (!Directory.Exists(fullPath))
                {
                    Log($"共享 [{share.ShareName}] 的本地目录不存在或暂不可用: {fullPath}，跳过挂载。", "Warning");
                    continue;
                }

                try
                {
                    NTDirectoryFileSystem fs = new NTDirectoryFileSystem(fullPath);
                    FileSystemShare fileShare = new FileSystemShare(share.ShareName, fs);

                    string shareName = share.ShareName;
                    string mountedPath = fullPath;
                    fileShare.AccessRequested += (sender, args) => OnAccessRequested(shareName, mountedPath, args);

                    shares.Add(fileShare);
                    activeShares[share.ShareName] = new ActiveShare(share, fullPath);
                    string accessMode = share.IsReadOnly ? "只读" : "读写";
                    string guestMode = share.AllowGuest ? "允许匿名" : "需鉴权";
                    Log($"成功挂载共享: [{share.ShareName}] -> {fullPath} [{accessMode} | {guestMode}]");
                }
                catch (Exception ex)
                {
                    Log($"挂载共享 [{share.ShareName}] 出错: {ex.Message}", "Error");
                }
            }

            var userMap = ShareAccessEvaluator.BuildUserPasswordMap(config, out var conflicts);
            foreach (var name in conflicts)
            {
                Log($"用户 [{name}] 在多个共享中配置了不同的密码。SMB 认证发生在访问共享之前，同一用户名只能有一个密码，当前仅首个共享中的密码有效，请在共享设置中统一该用户的密码。", "Error");
            }

            bool anyShareAllowsGuest = activeShares.Values.Any(s => s.Config.AllowGuest);
            return (shares, activeShares, userMap, anyShareAllowsGuest);
        }

        /// <summary>
        /// 运行时权限拦截：每次按共享名查询当前生效配置，保证热重载后已连接客户端同样即时受新权限约束
        /// </summary>
        private void OnAccessRequested(string shareName, string mountedPath, AccessRequestArgs args)
        {
            ActiveShare? active;
            lock (m_configLock)
            {
                m_activeShares.TryGetValue(shareName, out active);
            }

            if (active == null || !string.Equals(active.FullPath, mountedPath, StringComparison.OrdinalIgnoreCase))
            {
                args.Allow = false;
                Log($"[权限拦截] 共享 [{shareName}] 已被移除或其本地路径已变更，拒绝旧连接继续访问 (用户: {args.UserName}, 路径: {args.Path})", "Warning");
                return;
            }

            args.Allow = ShareAccessEvaluator.Evaluate(active.Config, args.UserName, args.RequestedAccess, out string denyReason);
            if (!args.Allow)
            {
                Log($"[权限拦截] {denyReason} (路径: {args.Path})", "Warning");
            }
        }

        /// <summary>
        /// NTLM 密码查询委托：返回 null 表示“非本服务用户”（若开放匿名则由协议栈降级为 Guest 会话），
        /// 返回 string.Empty 表示该账户为空密码 / 匿名账户。
        /// </summary>
        private string? GetUserPasswordHandler(string userName)
        {
            lock (m_configLock)
            {
                bool isGuestName = string.IsNullOrEmpty(userName) || string.Equals(userName, "guest", StringComparison.OrdinalIgnoreCase);
                if (isGuestName)
                {
                    // 注意：协议栈每次认证都会以 "Guest" 查询是否启用来宾登录，此处不记日志以免刷屏
                    return m_anyShareAllowsGuest ? string.Empty : null;
                }

                string bareName = ShareAccessEvaluator.GetBareUserName(userName);
                if (m_userMap.TryGetValue(userName, out string? password) ||
                    m_userMap.TryGetValue(bareName, out password))
                {
                    return password;
                }

                if (m_anyShareAllowsGuest)
                {
                    Log($"收到未知用户 [{userName}] 登录请求，当前存在开放匿名访问的共享，按来宾 (Guest) 身份放行。");
                }
                else
                {
                    Log($"用户 [{userName}] 不在授权用户列表中，且当前未开放匿名访问，拒绝握手。", "Warning");
                }
                // 返回 null：开放匿名时由 IndependentNTLMAuthenticationProvider 建立 Guest 会话，否则返回 LOGON_FAILURE
                return null;
            }
        }

        public void Start()
        {
            if (m_isRunning)
            {
                Log("服务已经在运行中。", "Warning");
                return;
            }

            bool enableSMB1 = ShareAccessEvaluator.IsSmb1Required(m_config);
            bool enableSMB2 = true; // 新协议直接开启，不让用户选择
            bool enableSMB3 = true; // 新协议直接开启，不让用户选择

            Log($"正在准备启动 SMB 服务 [SMB1.0: {enableSMB1}, SMB2/3: 开启]...");

            // 1. 组装共享文件夹与用户认证
            var (shares, activeShares, userMap, anyGuest) = BuildSharesAndUsers(m_config);
            if (shares.Count == 0)
            {
                throw new InvalidOperationException("未配置任何有效的共享目录（请检查共享目录是否存在），无法启动服务。");
            }

            lock (m_configLock)
            {
                m_activeShares = activeShares;
                m_userMap = userMap;
                m_anyShareAllowsGuest = anyGuest;
            }

            // 2. 动态密码解析委托，支持后续热重载
            IndependentNTLMAuthenticationProvider ntlmProvider =
                new IndependentNTLMAuthenticationProvider(GetUserPasswordHandler);

            GSSProvider gssProvider = new GSSProvider(ntlmProvider);

            // 3. 创建 SMB 服务端实例 (继承自 SMBServer，安全指定监听端口，避免反射 readonly 静态字段)
            m_server = new CustomSMBServer(shares, gssProvider);

            // 绑定底层日志事件
            m_server.LogEntryAdded += (sender, args) =>
            {
                if (m_config.EnableVerboseLog)
                {
                    Log(args.Message, args.Severity.ToString(), args.Source);
                }
            };

            // 4. 配置端口并启动
            int port = m_config.ListenPort <= 0 ? 445 : m_config.ListenPort;

            try
            {
                m_server.Start(IPAddress.Any, SMBTransportType.DirectTCPTransport, port, enableSMB1, enableSMB2, enableSMB3);
                m_smb1Enabled = enableSMB1;
                m_isRunning = true;
                StatusChanged?.Invoke(this, true);
                Log($"SMB 服务已成功启动！正在监听端口: {port} (SMB 1.0: {enableSMB1}, SMB 2.0/3.0: 开启)");

                // 启动局域网设备自发现（mDNS / WS-Discovery，异步启动避免阻塞 SMB 主服务上线）
                if (m_config.EnableNetworkDiscovery)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            lock (m_discoveryLock)
                            {
                                if (m_isRunning && m_config.EnableNetworkDiscovery)
                                {
                                    m_discoveryService.Start(port);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"启动局域网自发现服务异常: {ex.Message}", "Warning");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                m_server = null;
                m_isRunning = false;
                StatusChanged?.Invoke(this, false);
                Log($"启动 SMB 服务失败: {ex.Message}", "Error");
                throw;
            }
        }

        /// <summary>
        /// 动态热更新共享目录配置与用户权限（服务不重启，445 端口不释放，客户端连接不中断，毫秒级生效）。
        /// 返回 false 表示无法热更新（如 SMB 1.0 开关变化或没有有效共享），调用方应改为重启服务。
        /// </summary>
        public bool ReloadConfig(ServerConfig newConfig)
        {
            if (newConfig == null) return false;

            try
            {
                bool smb1Required = ShareAccessEvaluator.IsSmb1Required(newConfig);
                if (m_isRunning && smb1Required != m_smb1Enabled)
                {
                    Log($"SMB 1.0 协议开关已变更 ({m_smb1Enabled} -> {smb1Required})，协议协商能力需重启服务后生效。");
                    return false;
                }

                var (newShares, newActiveShares, newUserMap, anyGuest) = BuildSharesAndUsers(newConfig);
                if (newShares.Count == 0)
                {
                    Log("热重载共享配置失败：未包含任何有效共享目录。", "Warning");
                    return false;
                }

                lock (m_configLock)
                {
                    m_activeShares = newActiveShares;
                    m_userMap = newUserMap;
                    m_anyShareAllowsGuest = anyGuest;
                }

                if (m_server != null && m_isRunning)
                {
                    m_server.UpdateShares(newShares);
                    Log($"[热重载] 共享目录与权限已成功热更新生效 ({newShares.Count} 个共享目录，已连接客户端同步按新权限判定)，服务持续运行无中断！");
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"热更新共享配置失败: {ex.Message}", "Error");
                return false;
            }
        }

        public void Stop()
        {
            if (!m_isRunning || m_server == null)
            {
                return;
            }

            try
            {
                // 停止自发现广播并发送下线通知
                lock (m_discoveryLock)
                {
                    m_isRunning = false;
                    try
                    {
                        m_discoveryService.Stop();
                    }
                    catch { }
                }

                m_server.Stop();
                m_server = null;
                Log("SMB 服务已停止。");
            }
            catch (Exception ex)
            {
                Log($"停止 SMB 服务异常: {ex.Message}", "Warning");
            }
            finally
            {
                m_isRunning = false;
                StatusChanged?.Invoke(this, false);
            }
        }

        /// <summary>
        /// 动态切换局域网设备自发现（无需重启 SMB 服务主进程）
        /// </summary>
        public void SetNetworkDiscoveryEnabled(bool enabled)
        {
            lock (m_discoveryLock)
            {
                m_config.EnableNetworkDiscovery = enabled;
                if (!m_isRunning) return;

                if (enabled)
                {
                    int port = m_config.ListenPort <= 0 ? 445 : m_config.ListenPort;
                    m_discoveryService.Start(port);
                }
                else
                {
                    m_discoveryService.Stop();
                }
            }
        }

        /// <summary>
        /// 网络环境变化（网卡上下线、IP 变更）后重启自发现服务，重新加入各网卡多播组并宣告新地址
        /// </summary>
        public void RefreshNetworkDiscovery()
        {
            lock (m_discoveryLock)
            {
                DiscoveryHelper.InvalidateInterfaceCache();
                if (!m_isRunning || !m_config.EnableNetworkDiscovery) return;

                int port = m_config.ListenPort <= 0 ? 445 : m_config.ListenPort;
                m_discoveryService.Stop();
                m_discoveryService.Start(port);
            }
        }
    }

    /// <summary>
    /// 自定义 SMBServer 派生类，用于显式指定监听端口，避免在 .NET 8 下通过反射修改 initonly 静态字段引发 FieldAccessException。
    /// </summary>
    internal class CustomSMBServer : SMBServer
    {
        public CustomSMBServer(SMBShareCollection shares, GSSProvider securityProvider)
            : base(shares, securityProvider)
        {
        }

        public void Start(IPAddress serverAddress, SMBTransportType transport, int port, bool enableSMB1, bool enableSMB2, bool enableSMB3)
        {
            base.Start(serverAddress, transport, port, enableSMB1, enableSMB2, enableSMB3, null);
        }
    }
}
