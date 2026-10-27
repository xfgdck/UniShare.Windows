using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using UniShare.Core;
using UniShare.Core.Models;
using UniShare.UI.Helpers;
using UniShare.UI.ViewModels;

namespace UniShare.UI.Services
{
    /// <summary>
    /// SMB 服务宿主：持有配置、引擎与 445 端口，负责服务启停、热重载、共享增删与退出还原。
    /// 界面层只通过本类的属性与方法操作服务，不直接接触引擎。
    /// </summary>
    public class ServerHost : ViewModelBase
    {
        private readonly IDialogService m_dialogs;
        private readonly ServerConfig m_config;
        private readonly string m_configPath;
        private readonly PortReservation m_portReservation = new PortReservation();

        // 所有服务启停 / 重启 / 热重载操作串行执行，避免并发操作互相覆盖或被静默丢弃
        private readonly SemaphoreSlim m_serviceLock = new SemaphoreSlim(1, 1);

        // 网络变化事件在短时间内会连续触发，延迟合并后统一刷新
        private readonly DispatcherTimer m_networkRefreshTimer;

        private SmbServerEngine? m_engine;
        private bool m_isRunning;
        private bool m_isBusy;
        private string m_busyMessage = string.Empty;
        private string m_statusDetail = "正在准备...";
        private bool m_isShutdown;

        public ObservableCollection<ShareConfig> Shares { get; } = new ObservableCollection<ShareConfig>();

        /// <summary>
        /// 当前可用于访问本机的局域网 IPv4 网卡（首个为主地址）
        /// </summary>
        public IReadOnlyList<NetworkInterfaceInfo> NetworkInterfaces { get; private set; } = Array.Empty<NetworkInterfaceInfo>();

        /// <summary>
        /// 网卡地址变化后触发（界面线程）
        /// </summary>
        public event EventHandler? NetworkInterfacesChanged;

        public ServerConfig Config => m_config;

        /// <summary>
        /// 启动时配置文件读取失败的提示信息（无异常时为 null），由主窗口显示后清空
        /// </summary>
        public string? StartupWarning { get; set; }

        public bool IsRunning
        {
            get => m_isRunning;
            private set => SetProperty(ref m_isRunning, value);
        }

        public bool IsBusy
        {
            get => m_isBusy;
            private set => SetProperty(ref m_isBusy, value);
        }

        public string BusyMessage
        {
            get => m_busyMessage;
            private set => SetProperty(ref m_busyMessage, value);
        }

        /// <summary>
        /// 面向普通用户的状态说明（不出现端口、协议等技术细节）
        /// </summary>
        public string StatusDetail
        {
            get => m_statusDetail;
            private set => SetProperty(ref m_statusDetail, value);
        }

        public ServerHost(IDialogService dialogs)
        {
            m_dialogs = dialogs;
            m_configPath = ConfigManager.GetDefaultConfigPath();
            m_config = ConfigManager.LoadOrRecover(m_configPath, out string? recoveryMessage);
            StartupWarning = recoveryMessage;

            // 端口锁定为 445 标准端口
            m_config.ListenPort = PortHelper.DefaultSmbPort;

            if (ApplyLegacyUpgradeBackup(m_config))
            {
                SaveConfig();
            }

            SyncCollectionsFromConfig();
            NetworkInterfaces = NetworkHelper.GetActiveIPv4Interfaces();

            m_networkRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            m_networkRefreshTimer.Tick += (s, e) =>
            {
                m_networkRefreshTimer.Stop();
                OnNetworkEnvironmentChanged();
            };
            NetworkChange.NetworkAddressChanged += OnNetworkChangeEvent;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChangeEvent;

            Log("[系统] UniShare 初始化就绪，标准 TCP 445 端口模式");
            Log($"[配置] 已加载 {Shares.Count} 个共享目录 (配置文件: {m_configPath})");
            if (recoveryMessage != null)
            {
                Log($"[警告] {recoveryMessage.Replace('\n', ' ')}", "Warning");
            }
        }

        // ==================== 设置项 ====================

        /// <summary>
        /// 切换局域网设备自发现；服务运行中时立即生效
        /// </summary>
        public void SetNetworkDiscoveryEnabled(bool enabled)
        {
            if (m_config.EnableNetworkDiscovery == enabled) return;

            m_config.EnableNetworkDiscovery = enabled;
            SaveConfig();
            Log($"[设置] 局域网设备自发现已{(enabled ? "开启" : "关闭")}");

            var engine = m_engine;
            if (engine != null && IsRunning)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        engine.SetNetworkDiscoveryEnabled(enabled);
                    }
                    catch (Exception ex)
                    {
                        Log($"[设置] 切换自发现服务异常: {ex.Message}", "Warning");
                    }
                });
            }
        }

        // ==================== 服务生命周期 ====================

        /// <summary>
        /// 串行执行服务类操作：等待前序操作完成后再执行，期间保持忙碌状态
        /// </summary>
        private async Task RunServiceOperationAsync(string busyMessage, Func<Task> operation)
        {
            await m_serviceLock.WaitAsync();
            try
            {
                if (m_isShutdown) return;
                IsBusy = true;
                BusyMessage = busyMessage;
                await operation();
            }
            catch (Exception ex)
            {
                Log($"[异常] {busyMessage.TrimEnd('.', '。')} 失败: {ex.Message}", "Error");
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
                m_serviceLock.Release();
            }
        }

        /// <summary>
        /// 停用 Windows 系统自带 SMB 以接管 445 端口；首次改动系统服务前持久化记录其原始状态，供退出 / 卸载时按原样还原
        /// </summary>
        private async Task<(bool Success, string Message)> TakeOverPort445Async()
        {
            if (m_config.SystemSmbBackup == null)
            {
                // 在后台查询系统服务状态，但配置的修改与保存留在界面线程，避免与界面对配置的修改并发
                var state = await Task.Run(PortHelper.CaptureSystemSmbState);
                m_config.SystemSmbBackup = state;
                SaveConfig();
                Log($"[445端口] 已记录系统共享服务原始状态 (启动类型: {state.LanmanStartType}{(state.LanmanDelayedAutoStart ? " 延迟" : "")}, 运行中: {state.WasRunning})，退出时将按原样还原。");
            }

            return await Task.Run(() => PortHelper.TryStopLanmanServer(s => Log(s)));
        }

        /// <summary>
        /// 从旧版本升级：旧版本启动时总会禁用 LanmanServer 且不记录原始状态。
        /// 若检测到旧版配置迁移且系统共享服务处于禁用状态，视为旧版本遗留，按系统默认（自动并运行）记录待还原状态。
        /// </summary>
        private static bool ApplyLegacyUpgradeBackup(ServerConfig config)
        {
            if (ConfigManager.MigratedFromLegacy && config.SystemSmbBackup == null &&
                PortHelper.CaptureSystemSmbState().LanmanStartType == 4)
            {
                config.SystemSmbBackup = new SystemSmbState { LanmanStartType = 2, WasRunning = true };
                return true;
            }
            return false;
        }

        /// <summary>
        /// 软件启动时生命周期：首先解除系统 445 端口占用，然后和本软件绑定（保持运行期间持续绑定）
        /// </summary>
        public Task InitializeStartupPortAsync()
        {
            return RunServiceOperationAsync("正在准备", async () =>
            {
                StatusDetail = "正在准备共享服务...";

                // 0. 确保防火墙放行规则（后台执行，不阻塞端口接管）
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    _ = Task.Run(() =>
                    {
                        var (ok, msg) = FirewallHelper.EnsureRules(exePath);
                        Log($"[防火墙] {msg}", ok ? "Info" : "Warning");
                    });
                }

                // 1. 软件启动时：在后台线程检测 445 端口是否可直接绑定，且检查系统 SMB 驱动是否处于活跃态（防止快速重启时 START_PENDING 过渡态）
                bool canBindDirectly = await Task.Run(() => PortHelper.CanBindPort(PortHelper.DefaultSmbPort));
                bool sysSmbActive = await Task.Run(() => PortHelper.IsSystemSmbActive());

                if (!canBindDirectly || sysSmbActive)
                {
                    Log("[445端口] 软件启动检测到系统原生 SMB 服务或驱动占用（或处于启动过渡态），自动执行解绑接管...");

                    var (success, msg) = await TakeOverPort445Async();
                    Log(msg);

                    if (!success)
                    {
                        Log("[警告] 解绑系统 SMB 驱动链未完全成功，445 端口可能仍被占用。", "Warning");
                    }
                }
                else
                {
                    Log("[445端口] 检测到 445 端口空闲，可直接由本软件接管绑定。");
                }

                // 2. 然后和本软件绑定 (若已有共享目录且上次状态为开启，直接启动 SMB 服务绑定 445；否则由持有器独占绑定 445 并保持停止状态)
                if (Shares.Count > 0 && m_config.IsServiceEnabled)
                {
                    Log("[445端口] 正在恢复上次运行状态，由 UniShare 服务直接绑定 445 端口并启动共享...");
                    await StartServerCoreAsync();
                }
                else
                {
                    await HoldPortWhileStoppedAsync();
                }
            });
        }

        private async Task HoldPortWhileStoppedAsync()
        {
            bool held = await Task.Run(() => m_portReservation.TryHold(PortHelper.DefaultSmbPort));
            if (held)
            {
                Log("[445端口] 服务未运行，TCP 445 端口由本软件保持独占绑定。");
                StatusDetail = StoppedDetail;
            }
            else
            {
                Log("[警告] 绑定 445 端口失败，可能仍有驱动残留占用。", "Warning");
                StatusDetail = "共享服务准备失败，请重启软件后再试";
            }
        }

        private string StoppedDetail => Shares.Count > 0
            ? "局域网设备暂时无法访问共享"
            : "新建共享目录后即可开启服务";

        /// <summary>
        /// 启动服务：释放占位持有器，由 SMB 服务引擎接管 445 端口
        /// </summary>
        public Task StartServerAsync()
        {
            return RunServiceOperationAsync("正在启动服务", async () =>
            {
                if (IsRunning || Shares.Count == 0) return;
                StatusDetail = "请稍候...";
                await StartServerCoreAsync();
            });
        }

        private async Task StartServerCoreAsync()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // 1. 释放占位持有器，让出 445 端口给 SMB 服务引擎
                m_portReservation.Release();

                // 2. 检查 445 端口可用性及系统 SMB 服务状态，若仍被系统驱动占用则自动执行接管
                bool canBind = await Task.Run(() => PortHelper.CanBindPort(PortHelper.DefaultSmbPort));
                bool sysSmbActive = await Task.Run(() => PortHelper.IsSystemSmbActive());
                if (!canBind || sysSmbActive)
                {
                    Log("[445端口] 检测到 445 端口未就绪或系统 SMB 服务活跃，正在重新接管端口...");
                    var (stopOk, stopMsg) = await TakeOverPort445Async();
                    Log(stopMsg);
                    if (!stopOk)
                    {
                        throw new InvalidOperationException($"无法接管 TCP 445 端口：{stopMsg}");
                    }
                }

                // 引擎使用配置快照，避免与界面线程对配置的修改并发冲突
                ServerConfig snapshot = ConfigManager.Clone(m_config);
                await Task.Run(() =>
                {
                    var engine = new SmbServerEngine(snapshot);
                    engine.LogReceived += OnEngineLogReceived;
                    try
                    {
                        engine.Start();
                    }
                    catch
                    {
                        engine.LogReceived -= OnEngineLogReceived;
                        throw;
                    }
                    m_engine = engine;
                });

                sw.Stop();
                IsRunning = true;
                if (!m_config.IsServiceEnabled)
                {
                    m_config.IsServiceEnabled = true;
                    SaveConfig();
                }
                StatusDetail = "局域网内的设备现在可以访问共享";
                Log($"[服务] SMB 文件共享已成功启动 (端口: 445，就绪耗时: {sw.ElapsedMilliseconds} ms)！");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log($"[错误] 启动服务失败 (耗时: {sw.ElapsedMilliseconds} ms): {ex.Message}", "Error");
                // 启动失败时，重新由占位器抢回 445 端口，确保本软件继续持有
                await Task.Run(() => m_portReservation.TryHold(PortHelper.DefaultSmbPort));
                IsRunning = false;
                StatusDetail = "启动失败，请重启软件后再试";
                // 技术细节写入日志文件，界面只给出普通用户能理解的提示
                CrashLogger.Write("启动共享服务失败", ex);
                m_dialogs.ShowError("共享服务启动失败，可能被系统或其他软件占用。\n请重启软件或电脑后再试。", "启动失败");
            }
        }

        /// <summary>
        /// 关闭服务：停止 SMB 共享，但 445 端口由本软件继续保持绑定，不交还给系统（退出程序时才还原系统共享服务）
        /// </summary>
        public Task StopServerAsync()
        {
            return RunServiceOperationAsync("正在停止服务", async () =>
            {
                if (!IsRunning) return;
                await StopServerCoreAsync(persistStoppedState: true);
            });
        }

        /// <param name="persistStoppedState">是否将停止状态记录到配置文件（移除最后一个共享时传 false，用户手动停止时传 true）</param>
        private async Task StopServerCoreAsync(bool persistStoppedState)
        {
            StatusDetail = "请稍候...";
            var sw = Stopwatch.StartNew();

            bool held = await Task.Run(() =>
            {
                StopEngine();
                // 软件运行期间一直保持绑定：服务停止后，立即由持有器接管 445 端口，防止系统驱动重新夺取
                return m_portReservation.TryHold(PortHelper.DefaultSmbPort);
            });

            sw.Stop();
            IsRunning = false;
            if (persistStoppedState && m_config.IsServiceEnabled)
            {
                m_config.IsServiceEnabled = false;
                SaveConfig();
            }
            Log($"[服务] UniShare SMB 服务已停止 (耗时: {sw.ElapsedMilliseconds} ms)。");

            if (held)
            {
                Log("[445端口] 服务已停止，TCP 445 端口继续由本软件保持独占绑定。");
                StatusDetail = StoppedDetail;
            }
            else
            {
                Log("[警告] 服务停止后重新绑定 445 端口失败。", "Warning");
                StatusDetail = StoppedDetail;
            }
        }

        private async Task RestartServerCoreAsync()
        {
            StatusDetail = "正在应用新的共享设置...";
            var sw = Stopwatch.StartNew();

            await Task.Run(StopEngine);
            IsRunning = false;

            if (Shares.Count > 0)
            {
                // 启动失败时 StartServerCoreAsync 内部会抢回 445 端口并提示
                await StartServerCoreAsync();
                sw.Stop();
                if (IsRunning)
                {
                    Log($"[服务] UniShare SMB 服务已成功重新启动 (总耗时: {sw.ElapsedMilliseconds} ms)！");
                }
            }
            else
            {
                // 若无共享目录，由占位持有器接管 445 端口
                await HoldPortWhileStoppedAsync();
            }
        }

        private void StopEngine()
        {
            var engine = m_engine;
            m_engine = null;
            if (engine != null)
            {
                engine.Stop();
                engine.LogReceived -= OnEngineLogReceived;
            }
        }

        /// <summary>
        /// 退出程序生命周期：停止服务、解绑 445 端口、按原始状态恢复系统原生共享（不改变配置中记录的上次服务启停状态）。
        /// 可重复调用，仅首次生效。
        /// </summary>
        public void ShutdownAndRestore()
        {
            if (m_isShutdown) return;
            m_isShutdown = true;

            try
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkChangeEvent;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkChangeEvent;

                try
                {
                    StopEngine();
                }
                catch { }

                // 释放本软件对 445 端口的绑定
                m_portReservation.Release();

                // 仅当本软件改动过系统服务时，按记录的原始状态还原
                if (m_config.SystemSmbBackup != null)
                {
                    PortHelper.RestoreSystemSmb(m_config.SystemSmbBackup);
                    m_config.SystemSmbBackup = null;
                    SaveConfig();
                }
            }
            catch { }
        }

        /// <summary>
        /// 供卸载程序调用（UniShare.exe --restore-system-smb）：按配置中记录的原始状态还原系统共享服务并清除记录
        /// </summary>
        public static void RestoreSystemSmbFromConfig()
        {
            // 从未运行过（无配置文件）时无需还原，也不应在卸载过程中新建配置
            if (!File.Exists(ConfigManager.GetDefaultConfigPath()) && !File.Exists(ConfigManager.GetLegacyConfigPath()))
            {
                return;
            }

            ServerConfig config = ConfigManager.LoadOrRecover(null, out _);
            ApplyLegacyUpgradeBackup(config);
            if (config.SystemSmbBackup != null)
            {
                PortHelper.RestoreSystemSmb(config.SystemSmbBackup);
                config.SystemSmbBackup = null;
                try { ConfigManager.Save(config); } catch { }
            }
        }

        // ==================== 共享目录 ====================

        public void AddShare(ShareConfig share)
        {
            bool wasEmpty = Shares.Count == 0;
            m_config.Shares.Add(share);
            Shares.Add(share);
            SaveConfig();
            int userCount = share.Users?.Count ?? 0;
            Log($"[共享] 已添加新共享目录: [{share.ShareName}] -> {share.LocalPath} (匿名: {(share.AllowGuest ? share.GuestAccess.ToString() : "禁止")}, 用户: {userCount}个)");

            _ = ApplyRuntimeConfigAsync(autoStartIfFirstShare: wasEmpty);
        }

        public void UpdateShare(ShareConfig oldShare, ShareConfig newShare)
        {
            int indexConfig = m_config.Shares.IndexOf(oldShare);
            if (indexConfig >= 0)
            {
                m_config.Shares[indexConfig] = newShare;
            }

            int indexUi = Shares.IndexOf(oldShare);
            if (indexUi >= 0)
            {
                Shares[indexUi] = newShare;
            }

            SaveConfig();
            Log($"[共享] 共享目录 [{newShare.ShareName}] 配置与用户权限已更新！");

            _ = ApplyRuntimeConfigAsync(autoStartIfFirstShare: false);
        }

        public void RemoveShare(ShareConfig share)
        {
            m_config.Shares.Remove(share);
            Shares.Remove(share);
            SaveConfig();
            Log($"[共享] 已移除共享: [{share.ShareName}]");

            _ = ApplyRuntimeConfigAsync(autoStartIfFirstShare: false);
        }

        /// <summary>
        /// 把最新共享配置应用到运行中的服务：优先热重载（不断开连接），不支持热重载的变更（如 SMB 1.0 开关）自动重启服务
        /// </summary>
        private Task ApplyRuntimeConfigAsync(bool autoStartIfFirstShare)
        {
            return RunServiceOperationAsync("正在应用设置", async () =>
            {
                if (IsRunning)
                {
                    if (Shares.Count == 0)
                    {
                        // 当最后一个共享目录被移除时，停止当前运行，保留用户原本的开启偏好以便下次添加共享时自动启动
                        await StopServerCoreAsync(persistStoppedState: false);
                        return;
                    }

                    var engine = m_engine;
                    ServerConfig snapshot = ConfigManager.Clone(m_config);
                    bool reloaded = engine != null && await Task.Run(() => engine.ReloadConfig(snapshot));
                    if (reloaded)
                    {
                        Log("[提示] 共享配置已动态热加载生效！");
                    }
                    else
                    {
                        Log("[提示] 当前变更无法热加载，正在重启服务使其生效...");
                        await RestartServerCoreAsync();
                    }
                }
                else if (autoStartIfFirstShare && Shares.Count > 0 && m_config.IsServiceEnabled)
                {
                    Log("[提示] 已添加首个共享目录，自动启动共享服务...");
                    await StartServerCoreAsync();
                }
                else if (!IsRunning)
                {
                    StatusDetail = StoppedDetail;
                }
            });
        }

        public void SaveConfig()
        {
            try
            {
                ConfigManager.Save(m_config, m_configPath);
            }
            catch (Exception ex)
            {
                Log($"[错误] 保存配置文件失败: {ex.Message}", "Error");
            }
        }

        private void SyncCollectionsFromConfig()
        {
            // 过滤掉既未开启匿名访问、又没有任何授权用户的无意义共享配置，并校准只读显示状态
            var validShares = m_config.Shares
                .Where(s => s.AllowGuest || ShareAccessEvaluator.HasConfiguredUsers(s))
                .ToList();

            foreach (var s in validShares)
            {
                bool hasWrite = (s.AllowGuest && s.GuestAccess == ShareAccessLevel.ReadWrite) ||
                                (s.Users != null && s.Users.Any(u => u.AccessLevel == ShareAccessLevel.ReadWrite)) ||
                                (s.UserPermissions != null && s.UserPermissions.Any(p => p.AccessLevel == ShareAccessLevel.ReadWrite));
                if ((s.Users != null && s.Users.Count > 0) || (s.UserPermissions != null && s.UserPermissions.Count > 0) || s.AllowGuest)
                {
                    s.IsReadOnly = !hasWrite;
                }
            }

            if (validShares.Count != m_config.Shares.Count)
            {
                m_config.Shares = validShares;
                SaveConfig();
            }

            Shares.Clear();
            foreach (var s in m_config.Shares)
            {
                Shares.Add(s);
            }

            // 清理并过滤掉无效的 guest 虚拟用户（免密来宾通过 GuestAccess 独立配置）
            var cleanedUsers = m_config.Users
                .Where(u => !string.IsNullOrWhiteSpace(u.Username) &&
                            !string.Equals(u.Username, "guest", StringComparison.OrdinalIgnoreCase) &&
                            !u.IsGuest)
                .ToList();

            if (cleanedUsers.Count != m_config.Users.Count)
            {
                m_config.Users = cleanedUsers;
                SaveConfig();
            }
        }

        // ==================== 网络环境 ====================

        private void OnNetworkChangeEvent(object? sender, EventArgs e)
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (m_isShutdown) return;
                m_networkRefreshTimer.Stop();
                m_networkRefreshTimer.Start();
            });
        }

        /// <summary>
        /// 网卡上下线 / IP 变化：刷新地址列表，并让自发现服务重新加入多播组、宣告新地址
        /// </summary>
        private void OnNetworkEnvironmentChanged()
        {
            NetworkInterfaces = NetworkHelper.GetActiveIPv4Interfaces();
            NetworkInterfacesChanged?.Invoke(this, EventArgs.Empty);
            Log($"[网络] 检测到网络环境变化，当前地址: {string.Join(", ", NetworkInterfaces.Select(n => n.IpAddress))}");

            var engine = m_engine;
            if (engine != null && IsRunning)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        engine.RefreshNetworkDiscovery();
                    }
                    catch (Exception ex)
                    {
                        Log($"[网络] 刷新自发现服务异常: {ex.Message}", "Warning");
                    }
                });
            }
        }

        // ==================== 日志 ====================

        private void OnEngineLogReceived(object? sender, LogMessageEventArgs e)
        {
            Debug.WriteLine($"[{e.Time:HH:mm:ss}] [{e.Severity}] {e.Message}");
        }

        /// <summary>
        /// 运行日志仅输出到调试器（界面不展示日志），异常仍由 CrashLogger 落盘
        /// </summary>
        public void Log(string message, string severity = "Info")
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{severity}] {message}");
        }
    }
}
