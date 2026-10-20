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
        private CustomSMBServer? m_server;
        private readonly ServerConfig m_config;
        private readonly NetworkDiscoveryService m_discoveryService = new NetworkDiscoveryService();
        private bool m_isRunning = false;

        private readonly object m_configLock = new object();
        private Dictionary<string, string> m_userMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool m_anyShareAllowsGuest = false;

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
        private (SMBShareCollection Shares, Dictionary<string, string> UserMap, bool AnyShareAllowsGuest) BuildSharesAndUsers(ServerConfig config)
        {
            SMBShareCollection shares = new SMBShareCollection();
            foreach (var share in config.Shares)
            {
                if (string.IsNullOrWhiteSpace(share.LocalPath))
                {
                    Log($"共享 [{share.ShareName}] 本地路径为空，跳过挂载。", "Warning");
                    continue;
                }

                bool hasConfiguredUsers = (share.Users != null && share.Users.Count > 0) ||
                                          (share.UserPermissions != null && share.UserPermissions.Count > 0) ||
                                          (share.AllowedUsers != null && share.AllowedUsers.Count > 0);
                if (!share.AllowGuest && !hasConfiguredUsers)
                {
                    Log($"共享 [{share.ShareName}] 未添加任何用户且未开启匿名访问，跳过挂载。", "Warning");
                    continue;
                }

                string fullPath = Path.GetFullPath(share.LocalPath);

                if (!Directory.Exists(fullPath))
                {
                    try
                    {
                        Directory.CreateDirectory(fullPath);
                        Log($"本地目录不存在，已自动创建: {fullPath}");
                    }
                    catch (Exception ex)
                    {
                        Log($"创建本地目录 [{fullPath}] 失败: {ex.Message}", "Error");
                        continue;
                    }
                }

                try
                {
                    NTDirectoryFileSystem fs = new NTDirectoryFileSystem(fullPath);
                    FileSystemShare fileShare = new FileSystemShare(share.ShareName, fs);

                    fileShare.AccessRequested += (sender, args) =>
                    {
                        string userName = args.UserName ?? string.Empty;
                        string bareName = userName;
                        if (bareName.Contains('\\'))
                        {
                            bareName = bareName.Substring(bareName.LastIndexOf('\\') + 1);
                        }
                        else if (bareName.Contains('@'))
                        {
                            bareName = bareName.Substring(0, bareName.IndexOf('@'));
                        }

                        bool isGuestOrAnonymous = string.IsNullOrEmpty(userName) ||
                            string.Equals(userName, "guest", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(bareName, "guest", StringComparison.OrdinalIgnoreCase);

                        if (isGuestOrAnonymous)
                        {
                            if (!share.AllowGuest)
                            {
                                args.Allow = false;
                                Log($"[权限拦截] 拒绝匿名访问：共享 [{share.ShareName}] 未开放匿名访问", "Warning");
                                return;
                            }

                            if (share.GuestAccess == ShareAccessLevel.ReadOnly &&
                                (args.RequestedAccess == FileAccess.Write || args.RequestedAccess == FileAccess.ReadWrite))
                            {
                                args.Allow = false;
                                Log($"[权限拦截] 拒绝写入请求：共享 [{share.ShareName}] 的匿名权限为只读 (路径: {args.Path})", "Warning");
                                return;
                            }

                            args.Allow = true;
                            return;
                        }

                        if (share.Users != null && share.Users.Count > 0)
                        {
                            var shareUser = share.Users.FirstOrDefault(u => 
                                string.Equals(u.Username, userName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(u.Username, bareName, StringComparison.OrdinalIgnoreCase));
                            if (shareUser != null)
                            {
                                if (shareUser.AccessLevel == ShareAccessLevel.ReadOnly &&
                                    (args.RequestedAccess == FileAccess.Write || args.RequestedAccess == FileAccess.ReadWrite))
                                {
                                    args.Allow = false;
                                    Log($"[权限拦截] 拒绝写入请求：用户 [{userName}] 在共享 [{share.ShareName}] 上的权限为只读 (路径: {args.Path})", "Warning");
                                    return;
                                }

                                args.Allow = true;
                                return;
                            }
                        }

                        if (share.UserPermissions != null && share.UserPermissions.Count > 0)
                        {
                            var userPerm = share.UserPermissions.FirstOrDefault(p => 
                                string.Equals(p.Username, userName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(p.Username, bareName, StringComparison.OrdinalIgnoreCase));
                            if (userPerm != null)
                            {
                                if (userPerm.AccessLevel == ShareAccessLevel.ReadOnly &&
                                    (args.RequestedAccess == FileAccess.Write || args.RequestedAccess == FileAccess.ReadWrite))
                                {
                                    args.Allow = false;
                                    Log($"[权限拦截] 拒绝写入请求：用户 [{userName}] 在共享 [{share.ShareName}] 上的权限为只读 (路径: {args.Path})", "Warning");
                                    return;
                                }

                                args.Allow = true;
                                return;
                            }
                        }

                        if (share.AllowedUsers != null && share.AllowedUsers.Count > 0)
                        {
                            if (share.AllowedUsers.Contains(userName, StringComparer.OrdinalIgnoreCase) ||
                                share.AllowedUsers.Contains(bareName, StringComparer.OrdinalIgnoreCase))
                            {
                                if (share.IsReadOnly && (args.RequestedAccess == FileAccess.Write || args.RequestedAccess == FileAccess.ReadWrite))
                                {
                                    args.Allow = false;
                                    Log($"[权限拦截] 拒绝写入请求：共享 [{share.ShareName}] 设置为只读 (用户: {userName}, 路径: {args.Path})", "Warning");
                                    return;
                                }
                                args.Allow = true;
                                return;
                            }
                        }

                        if (share.AllowGuest)
                        {
                            if (share.GuestAccess == ShareAccessLevel.ReadOnly &&
                                (args.RequestedAccess == FileAccess.Write || args.RequestedAccess == FileAccess.ReadWrite))
                            {
                                args.Allow = false;
                                Log($"[权限拦截] 拒绝写入请求：未知用户 [{userName}] 降级为匿名访问，但共享 [{share.ShareName}] 的匿名权限为只读", "Warning");
                                return;
                            }
                            args.Allow = true;
                            return;
                        }

                        args.Allow = false;
                        Log($"[权限拦截] 拒绝未授权用户 [{userName}] 访问共享 [{share.ShareName}]", "Warning");
                    };

                    shares.Add(fileShare);
                    string accessMode = share.IsReadOnly ? "只读" : "读写";
                    string guestMode = share.AllowGuest ? "允许匿名" : "需鉴权";
                    Log($"成功挂载共享: [{share.ShareName}] -> {fullPath} [{accessMode} | {guestMode}]");
                }
                catch (Exception ex)
                {
                    Log($"挂载共享 [{share.ShareName}] 出错: {ex.Message}", "Error");
                }
            }

            Dictionary<string, string> userMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var share in config.Shares)
            {
                if (share.Users != null)
                {
                    foreach (var u in share.Users)
                    {
                        if (!string.IsNullOrWhiteSpace(u.Username))
                        {
                            userMap[u.Username] = u.Password ?? string.Empty;
                        }
                    }
                }
            }
            if (config.Users != null)
            {
                foreach (var u in config.Users)
                {
                    if (!string.IsNullOrWhiteSpace(u.Username) && !userMap.ContainsKey(u.Username))
                    {
                        userMap[u.Username] = u.Password ?? string.Empty;
                    }
                }
            }

            bool anyShareAllowsGuest = config.Shares.Any(s => s.AllowGuest);
            return (shares, userMap, anyShareAllowsGuest);
        }

        public void Start()
        {
            if (m_isRunning)
            {
                Log("服务已经在运行中。", "Warning");
                return;
            }

            bool enableSMB1 = m_config.Shares.Count > 0 ? m_config.Shares.Any(s => s.EnableSmb1) : m_config.EnableSmb1;
            bool enableSMB2 = true; // 新协议直接开启，不让用户选择
            bool enableSMB3 = true; // 新协议直接开启，不让用户选择

            Log($"正在准备启动 SMB 服务 [SMB1.0: {enableSMB1}, SMB2/3: 开启]...");

            // 1. 组装共享文件夹与用户认证
            var (shares, userMap, anyGuest) = BuildSharesAndUsers(m_config);
            if (shares.Count == 0)
            {
                throw new InvalidOperationException("未配置任何有效的共享目录，无法启动服务。");
            }

            lock (m_configLock)
            {
                m_userMap = userMap;
                m_anyShareAllowsGuest = anyGuest;
            }

            // 2. 动态密码解析委托，支持后续热重载
            string? GetUserPasswordHandler(string userName)
            {
                lock (m_configLock)
                {
                    if (string.IsNullOrEmpty(userName) || string.Equals(userName, "guest", StringComparison.OrdinalIgnoreCase))
                    {
                        if (m_anyShareAllowsGuest)
                        {
                            Log("收到匿名握手请求，已放行。");
                            return string.Empty;
                        }
                        Log("收到匿名握手请求，但当前无任何共享目录开启匿名访问，拒绝握手。", "Warning");
                        return null;
                    }

                    string bareName = userName;
                    if (bareName.Contains('\\'))
                    {
                        bareName = bareName.Substring(bareName.LastIndexOf('\\') + 1);
                    }
                    else if (bareName.Contains('@'))
                    {
                        bareName = bareName.Substring(0, bareName.IndexOf('@'));
                    }

                    if (m_userMap.TryGetValue(userName, out string? password) ||
                        m_userMap.TryGetValue(bareName, out password))
                    {
                        Log($"用户 [{userName}] 认证匹配成功。");
                        return password;
                    }

                    if (m_anyShareAllowsGuest)
                    {
                        Log($"收到未知用户 [{userName}] 登录请求，当前存在开放匿名访问的共享，映射为匿名身份放行握手。", "Info");
                        return string.Empty;
                    }

                    Log($"用户 [{userName}] 不在授权用户列表中，且当前未开放匿名访问，拒绝握手。", "Warning");
                    return null;
                }
            }

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
                            m_discoveryService.Start(port);
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
                m_isRunning = false;
                StatusChanged?.Invoke(this, false);
                Log($"启动 SMB 服务失败: {ex.Message}", "Error");
                throw;
            }
        }

        /// <summary>
        /// 动态热更新共享目录配置与用户权限（服务不重启，445 端口不释放，客户端连接不中断，毫秒级生效）
        /// </summary>
        public bool ReloadConfig(ServerConfig newConfig)
        {
            if (newConfig == null) return false;

            try
            {
                var (newShares, newUserMap, anyGuest) = BuildSharesAndUsers(newConfig);
                if (newShares.Count == 0)
                {
                    Log("热重载共享配置失败：未包含任何有效共享目录。", "Warning");
                    return false;
                }

                lock (m_configLock)
                {
                    m_userMap = newUserMap;
                    m_anyShareAllowsGuest = anyGuest;
                }

                if (m_server != null && m_isRunning)
                {
                    m_server.UpdateShares(newShares);
                    Log($"[热重载] 共享目录与权限已成功热更新生效 ({newShares.Count} 个共享目录)，服务持续运行无中断！");
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
                try
                {
                    m_discoveryService.Stop();
                }
                catch { }

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
