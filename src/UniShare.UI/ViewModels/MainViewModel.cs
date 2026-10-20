using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using UniShare.Core;
using UniShare.Core.Models;
using UniShare.UI.Helpers;
using UniShare.UI.Views;

namespace UniShare.UI.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private SmbServerEngine? m_engine;
        private ServerConfig m_config;
        private string m_configPath;

        private bool m_isRunning;
        private string m_statusText = "已停止";
        private string m_statusColor = "#94A3B8"; // Slate Gray
        private string m_statusSubtitle = "就绪，点击启动即可开启文件共享";
        private bool m_isBusy;
        private string m_busyMessage = string.Empty;
        private bool m_tookOverSystemSmb = false;
        private bool m_isLogsVisible = false;
        private bool m_isSettingsPageOpen = false;
        private bool m_isAutoStart = false;
        private string m_autoStartStatusText = "当前未开启开机自启动";
        private readonly PortReservation m_portReservation = new PortReservation();

        public ObservableCollection<ShareConfig> Shares { get; } = new ObservableCollection<ShareConfig>();
        public ObservableCollection<VirtualUser> Users { get; } = new ObservableCollection<VirtualUser>();
        public ObservableCollection<NetworkInterfaceInfo> NetworkInterfaces { get; } = new ObservableCollection<NetworkInterfaceInfo>();
        public ObservableCollection<string> UncAddresses { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> Logs { get; } = new ObservableCollection<string>();

        public ServerConfig Config => m_config;
        public string ConfigDirectoryPath => Path.GetDirectoryName(m_configPath) ?? AppDomain.CurrentDomain.BaseDirectory;

        public bool HasUsers => Users.Count > 0;
        public bool HasShares => Shares.Count > 0;

        public bool IsSettingsPageOpen
        {
            get => m_isSettingsPageOpen;
            set => SetProperty(ref m_isSettingsPageOpen, value);
        }

        public bool IsAutoStart
        {
            get => m_isAutoStart;
            set
            {
                if (m_isAutoStart != value)
                {
                    ApplyAutoStartSetting(value);
                }
            }
        }

        public bool IsNetworkDiscoveryEnabled
        {
            get => m_config.EnableNetworkDiscovery;
            set
            {
                if (m_config.EnableNetworkDiscovery != value)
                {
                    m_config.EnableNetworkDiscovery = value;
                    OnPropertyChanged(nameof(IsNetworkDiscoveryEnabled));
                    SaveConfig();
                    AddLog($"[设置] 局域网设备自发现已{(value ? "开启" : "关闭")}");
                    if (m_engine != null && IsRunning)
                    {
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                m_engine.SetNetworkDiscoveryEnabled(value);
                            }
                            catch (Exception ex)
                            {
                                AddLog($"[设置] 切换自发现服务异常: {ex.Message}");
                            }
                        });
                    }
                }
            }
        }

        public string AutoStartStatusText
        {
            get => m_autoStartStatusText;
            private set => SetProperty(ref m_autoStartStatusText, value);
        }

        public bool IsRunning
        {
            get => m_isRunning;
            private set
            {
                if (SetProperty(ref m_isRunning, value))
                {
                    StatusText = value ? "服务运行中" : "服务已停止";
                    StatusColor = value ? "#22C55E" : "#94A3B8"; // Green vs Gray
                    OnPropertyChanged(nameof(StatusButtonText));
                    OnPropertyChanged(nameof(StatusButtonAppearance));
                    OnPropertyChanged(nameof(StatusButtonHeight));
                    RefreshServerCommandsCanExecute();
                }
            }
        }

        public string StatusText
        {
            get => m_statusText;
            private set => SetProperty(ref m_statusText, value);
        }

        public string StatusColor
        {
            get => m_statusColor;
            private set => SetProperty(ref m_statusColor, value);
        }

        public string StatusSubtitle
        {
            get => m_statusSubtitle;
            private set => SetProperty(ref m_statusSubtitle, value);
        }

        public string StatusButtonText => IsRunning ? "停止服务" : "启动服务";
        public string StatusButtonAppearance => IsRunning ? "Secondary" : "Primary";
        public double StatusButtonHeight => IsRunning ? 42 : 40;

        public bool IsBusy
        {
            get => m_isBusy;
            private set
            {
                if (SetProperty(ref m_isBusy, value))
                {
                    RefreshServerCommandsCanExecute();
                }
            }
        }

        private void RefreshServerCommandsCanExecute()
        {
            (ToggleServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StartServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StopServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public string BusyMessage
        {
            get => m_busyMessage;
            private set => SetProperty(ref m_busyMessage, value);
        }

        public bool IsLogsVisible
        {
            get => m_isLogsVisible;
            set => SetProperty(ref m_isLogsVisible, value);
        }

        // 命令
        public ICommand ToggleServerCommand { get; }
        public ICommand StartServerCommand { get; }
        public ICommand StopServerCommand { get; }
        public ICommand ToggleLogsCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand CloseSettingsCommand { get; }
        public ICommand OpenConfigFolderCommand { get; }
        public ICommand CopyUncCommand { get; }
        public ICommand OpenShareFolderCommand { get; }
        public ICommand EditShareCommand { get; }
        public ICommand DeleteShareCommand { get; }
        public ICommand ClearLogsCommand { get; }

        public MainViewModel()
        {
            m_configPath = ConfigManager.GetDefaultConfigPath();
            m_config = ConfigManager.Load(m_configPath);

            // 端口锁定为 445 标准端口
            m_config.ListenPort = PortHelper.DefaultSmbPort;

            SyncCollectionsFromConfig();
            RefreshNetworkInterfaces();
            RefreshUncAddresses();

            ToggleServerCommand = new RelayCommand(async () =>
            {
                if (IsRunning) await StopServerAsync();
                else await StartServerAsync();
            }, () => !IsBusy);

            StartServerCommand = new RelayCommand(async () => await StartServerAsync(), () => !IsRunning && !IsBusy);
            StopServerCommand = new RelayCommand(async () => await StopServerAsync(), () => IsRunning && !IsBusy);
            ToggleLogsCommand = new RelayCommand(() => IsLogsVisible = !IsLogsVisible);
            OpenSettingsCommand = new RelayCommand(() => IsSettingsPageOpen = true);
            CloseSettingsCommand = new RelayCommand(() => IsSettingsPageOpen = false);
            OpenConfigFolderCommand = new RelayCommand(() => OpenFolder(ConfigDirectoryPath));
            CopyUncCommand = new RelayCommand(param => CopyToClipboard(param as string));
            OpenShareFolderCommand = new RelayCommand(param => OpenFolder(param as string));
            EditShareCommand = new RelayCommand(param => OpenEditShareDialog(param as ShareConfig));
            DeleteShareCommand = new RelayCommand(param => DeleteShare(param as ShareConfig));
            ClearLogsCommand = new RelayCommand(() => Logs.Clear());

            _ = InitializeAutoStartStateAsync();

            AddLog("[系统] UniShare 初始化就绪，标准 TCP 445 端口模式");
            AddLog($"[配置] 已加载 {Shares.Count} 个共享目录");
        }

        private async Task InitializeAutoStartStateAsync()
        {
            m_isAutoStart = m_config.AutoStart;
            UpdateAutoStartStatusText();
            OnPropertyChanged(nameof(IsAutoStart));

            bool wantAutoStart = m_config.AutoStart;

            bool taskExists = await Task.Run(() =>
            {
                bool exists = AutoStartHelper.IsAutoStartEnabled();
                if (wantAutoStart && !exists)
                {
                    var (ok, _) = AutoStartHelper.SetAutoStart(true, startMinimized: true);
                    exists = ok;
                }
                return exists;
            });

            m_isAutoStart = taskExists;
            if (m_config.AutoStart != taskExists)
            {
                m_config.AutoStart = taskExists;
                SaveConfig();
            }

            UpdateAutoStartStatusText();
            OnPropertyChanged(nameof(IsAutoStart));
        }

        private async void ApplyAutoStartSetting(bool enable)
        {
            SetProperty(ref m_isAutoStart, enable, nameof(IsAutoStart));
            UpdateAutoStartStatusText();

            var (success, msg) = await Task.Run(() => AutoStartHelper.SetAutoStart(enable, startMinimized: true));
            if (success)
            {
                m_config.AutoStart = enable;
                m_config.StartMinimized = true;
                SaveConfig();
                UpdateAutoStartStatusText();
                AddLog($"[设置] {msg}");
            }
            else
            {
                SetProperty(ref m_isAutoStart, !enable, nameof(IsAutoStart));
                UpdateAutoStartStatusText();
                MessageDialog.ShowWarning(msg, "开机自启动设置失败");
            }
        }

        private void UpdateAutoStartStatusText()
        {
            if (m_isAutoStart)
            {
                AutoStartStatusText = "✓ 已开启开机自启动";
            }
            else
            {
                AutoStartStatusText = "当前未开启开机自启动";
            }
        }

        /// <summary>
        /// 软件启动时生命周期：首先解除系统 445 端口占用，然后和本软件绑定（保持运行期间持续绑定）
        /// </summary>
        public async Task InitializeStartupPortAsync()
        {
            if (IsBusy) return;

            IsBusy = true;
            BusyMessage = "正在初始化并接管 445 端口...";
            StatusSubtitle = "正在接管 TCP 445 端口...";

            try
            {
                // 1. 软件启动时：在后台线程检测 445 端口是否可直接绑定，且检查系统 SMB 驱动是否处于活跃态（防止快速重启时 START_PENDING 过渡态）
                bool canBindDirectly = await Task.Run(() => PortHelper.CanBindPort(PortHelper.DefaultSmbPort));
                bool sysSmbActive = await Task.Run(() => PortHelper.IsSystemSmbActive());

                if (!canBindDirectly || sysSmbActive)
                {
                    AddLog("[445端口] 软件启动检测到系统原生 SMB 服务或驱动占用（或处于启动过渡态），自动执行解绑接管...");
                    m_tookOverSystemSmb = true;

                    var (success, msg) = await Task.Run(() => PortHelper.TryStopLanmanServer(s => AddLog(s)));
                    AddLog(msg);

                    if (!success)
                    {
                        AddLog("[警告] 解绑系统 SMB 驱动链未完全成功，445 端口可能仍被占用。", "Warning");
                    }
                }
                else
                {
                    AddLog("[445端口] 检测到 445 端口空闲，可直接由本软件接管绑定。");
                    m_tookOverSystemSmb = true;
                }

                // 2. 然后和本软件绑定 (若已有共享目录且上次状态为开启，直接启动 SMB 服务绑定 445；否则由持有器独占绑定 445 并保持停止状态)
                if (Shares.Count > 0 && m_config.IsServiceEnabled)
                {
                    AddLog("[445端口] 正在恢复上次运行状态，由 UniShare 服务直接绑定 445 端口并启动共享...");
                    await StartServerCoreAsync();
                }
                else
                {
                    bool held = await Task.Run(() => m_portReservation.TryHold(PortHelper.DefaultSmbPort));
                    if (held)
                    {
                        AddLog("[445端口] TCP 445 端口已成功与本软件独占绑定！");
                        StatusSubtitle = Shares.Count > 0
                            ? "服务已停止 · TCP 445 端口由本软件保持绑定"
                            : "TCP 445 端口已绑定锁定 · 请新建共享目录以开启服务";
                    }
                    else
                    {
                        AddLog("[警告] 绑定 445 端口失败，可能仍有驱动残留占用。", "Warning");
                        StatusSubtitle = "445 端口绑定异常，请检查占用";
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"[异常] 启动接管 445 端口发生异常: {ex.Message}", "Error");
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
        }

        public void RefreshNetworkInterfaces()
        {
            NetworkInterfaces.Clear();
            var list = NetworkHelper.GetActiveIPv4Interfaces();
            foreach (var item in list)
            {
                NetworkInterfaces.Add(item);
            }
        }

        public void RefreshUncAddresses()
        {
            UncAddresses.Clear();
            if (Shares.Count == 0)
            {
                return;
            }

            foreach (var net in NetworkInterfaces)
            {
                foreach (var share in Shares)
                {
                    UncAddresses.Add($@"\\{net.IpAddress}\{share.ShareName}");
                }
            }
        }

        /// <summary>
        /// 启动服务：释放占位持有器，由 SMB 服务引擎接管 445 端口
        /// </summary>
        public async Task StartServerAsync()
        {
            if (IsRunning || IsBusy) return;

            if (Shares.Count == 0)
            {
                MessageDialog.ShowInfo("当前尚未添加任何共享目录。\n请先点击“+ 新建共享”添加共享文件夹后再启动服务。", "无法启动服务");
                return;
            }

            IsBusy = true;
            BusyMessage = "正在启动服务...";
            StatusSubtitle = "正在启动服务并绑定 445 端口...";

            try
            {
                await StartServerCoreAsync();
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
        }

        private async Task StartServerCoreAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 1. 释放占位持有器，让出 445 端口给 SMB 服务引擎
                m_portReservation.Release();

                // 2. 检查 445 端口可用性及系统 SMB 服务状态，若仍被系统驱动占用则自动执行接管
                bool canBind = await Task.Run(() => PortHelper.CanBindPort(PortHelper.DefaultSmbPort));
                bool sysSmbActive = await Task.Run(() => PortHelper.IsSystemSmbActive());
                if (!canBind || sysSmbActive)
                {
                    AddLog("[445端口] 检测到 445 端口未就绪或系统 SMB 服务活跃，正在重新接管端口...");
                    var (stopOk, stopMsg) = await Task.Run(() => PortHelper.TryStopLanmanServer(s => AddLog(s)));
                    AddLog(stopMsg);
                    if (!stopOk)
                    {
                        throw new InvalidOperationException($"无法接管 TCP 445 端口：{stopMsg}");
                    }
                    m_tookOverSystemSmb = true;
                }

                await Task.Run(() =>
                {
                    m_engine = new SmbServerEngine(m_config);
                    m_engine.LogReceived += OnEngineLogReceived;
                    m_engine.Start();
                });

                sw.Stop();
                IsRunning = true;
                if (!m_config.IsServiceEnabled)
                {
                    m_config.IsServiceEnabled = true;
                    SaveConfig();
                }
                StatusSubtitle = "TCP 445 端口已绑定就绪 · 局域网服务运行中";
                AddLog($"[服务] SMB 文件共享已成功启动 (端口: 445，就绪耗时: {sw.ElapsedMilliseconds} ms)！");
            }
            catch (Exception ex)
            {
                sw.Stop();
                AddLog($"[错误] 启动服务失败 (耗时: {sw.ElapsedMilliseconds} ms): {ex.Message}");
                // 启动失败时，重新由占位器抢回 445 端口，确保本软件继续持有
                await Task.Run(() => m_portReservation.TryHold(PortHelper.DefaultSmbPort));
                MessageDialog.ShowError($"启动 SMB 服务失败：\n{ex.Message}", "启动失败");
                IsRunning = false;
                StatusSubtitle = "启动失败: " + ex.Message;
            }
        }

        /// <summary>
        /// 关闭服务：停止 SMB 共享，但 445 端口由本软件继续保持绑定，不交还给系统
        /// </summary>
        /// <param name="persistStoppedState">是否将停止状态记录到配置文件（热重启服务时传 false，用户手动停止时传 true）</param>
        public async Task StopServerAsync(bool persistStoppedState = true)
        {
            if (!IsRunning || IsBusy) return;

            IsBusy = true;
            BusyMessage = "正在停止服务...";
            StatusSubtitle = "正在停止服务...";
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                bool held = await Task.Run(() =>
                {
                    if (m_engine != null)
                    {
                        m_engine.Stop();
                        m_engine.LogReceived -= OnEngineLogReceived;
                        m_engine = null;
                    }

                    // 软件运行期间一直保持绑定：服务停止后，立即由持有器接管 445 端口，绝不交还给系统
                    return m_portReservation.TryHold(PortHelper.DefaultSmbPort);
                });

                sw.Stop();
                IsRunning = false;
                if (persistStoppedState && m_config.IsServiceEnabled)
                {
                    m_config.IsServiceEnabled = false;
                    SaveConfig();
                }
                AddLog($"[服务] UniShare SMB 服务已停止 (耗时: {sw.ElapsedMilliseconds} ms)。");

                if (held)
                {
                    AddLog("[445端口] 服务已停止，TCP 445 端口继续由本软件保持独占绑定。");
                    StatusSubtitle = Shares.Count > 0
                        ? "服务已停止 · TCP 445 端口由本软件保持绑定"
                        : "TCP 445 端口已绑定锁定 · 请新建共享目录以开启服务";
                }
                else
                {
                    AddLog("[警告] 服务停止后重新绑定 445 端口失败。", "Warning");
                    StatusSubtitle = "服务已停止";
                }
            }
            catch (Exception ex)
            {
                AddLog($"[异常] 停止服务出错: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
        }

        /// <summary>
        /// 退出程序生命周期：停止服务、解绑 445 端口、恢复系统原生共享（不改变配置中记录的上次服务启停状态）
        /// </summary>
        public void ShutdownAndRestore()
        {
            try
            {
                if (m_engine != null)
                {
                    try
                    {
                        m_engine.Stop();
                        m_engine.LogReceived -= OnEngineLogReceived;
                    }
                    catch { }
                    m_engine = null;
                }

                // 释放本软件对 445 端口的绑定
                m_portReservation.Release();

                // 退出时解绑：恢复 Windows 系统原生驱动栈
                if (m_tookOverSystemSmb)
                {
                    PortHelper.TryStartLanmanServer();
                }
            }
            catch { }
        }

        public void AddShare(ShareConfig share)
        {
            bool wasEmpty = Shares.Count == 0;
            m_config.Shares.Add(share);
            Shares.Add(share);
            OnPropertyChanged(nameof(HasShares));
            SaveConfig();
            RefreshUncAddresses();
            int userCount = share.Users?.Count ?? 0;
            AddLog($"[共享] 已添加新共享目录: [{share.ShareName}] -> {share.LocalPath} (匿名: {(share.AllowGuest ? share.GuestAccess.ToString() : "禁止")}, 用户: {userCount}个)");

            if (IsRunning)
            {
                // 优先热重载：毫秒级生效，不中断现有连接，445 端口无需释放
                if (m_engine != null && m_engine.ReloadConfig(m_config))
                {
                    AddLog("[提示] 共享目录配置已动态热加载生效！");
                }
                else
                {
                    _ = RestartServerAsync();
                }
            }
            else if (wasEmpty && m_config.IsServiceEnabled)
            {
                AddLog("[提示] 已添加首个共享目录，自动启动共享服务...");
                _ = StartServerAsync();
            }
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

            OnPropertyChanged(nameof(HasShares));
            SaveConfig();
            RefreshUncAddresses();
            AddLog($"[共享] 共享目录 [{newShare.ShareName}] 配置与用户权限已更新！");

            if (IsRunning)
            {
                // 优先热重载：毫秒级生效，不中断现有连接，445 端口无需释放
                if (m_engine != null && m_engine.ReloadConfig(m_config))
                {
                    AddLog("[提示] 共享配置已动态热加载生效！");
                }
                else
                {
                    _ = RestartServerAsync();
                }
            }
        }

        public void OpenEditShareDialog(ShareConfig? share)
        {
            if (share == null) return;

            var window = System.Windows.Application.Current.MainWindow;
            var dialog = new AddShareDialog(share, Shares)
            {
                Owner = window
            };

            if (dialog.ShowDialog() == true && dialog.Result != null)
            {
                UpdateShare(share, dialog.Result);
            }
        }

        public async Task RestartServerAsync()
        {
            if (IsBusy) return;

            IsBusy = true;
            BusyMessage = "正在重启服务...";
            StatusSubtitle = "正在重启服务并重新加载配置...";
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await Task.Run(() =>
                {
                    // 1. 停止旧引擎
                    if (m_engine != null)
                    {
                        m_engine.Stop();
                        m_engine.LogReceived -= OnEngineLogReceived;
                        m_engine = null;
                    }

                    // 2. 避免无意义的 PortReservation 抢占再释放，确保直接平滑过渡
                    Thread.Sleep(30);

                    // 3. 若有共享目录，直接启动新引擎绑定 445 端口
                    if (Shares.Count > 0)
                    {
                        m_portReservation.Release();

                        if (!PortHelper.CanBindPort(PortHelper.DefaultSmbPort) || PortHelper.IsSystemSmbActive())
                        {
                            PortHelper.TryStopLanmanServer();
                        }

                        m_engine = new SmbServerEngine(m_config);
                        m_engine.LogReceived += OnEngineLogReceived;
                        m_engine.Start();
                    }
                });

                sw.Stop();
                if (Shares.Count > 0)
                {
                    IsRunning = true;
                    if (!m_config.IsServiceEnabled)
                    {
                        m_config.IsServiceEnabled = true;
                        SaveConfig();
                    }
                    StatusSubtitle = "TCP 445 端口已绑定就绪 · 局域网服务运行中";
                    AddLog($"[服务] UniShare SMB 服务已成功重新启动 (端口: 445，总耗时: {sw.ElapsedMilliseconds} ms)！");
                }
                else
                {
                    // 若无共享目录，由占位持有器接管 445 端口
                    bool held = await Task.Run(() => m_portReservation.TryHold(PortHelper.DefaultSmbPort));
                    IsRunning = false;
                    StatusSubtitle = "TCP 445 端口已绑定锁定 · 请新建共享目录以开启服务";
                }
            }
            catch (Exception ex)
            {
                AddLog($"[错误] 重启服务失败: {ex.Message}");
                await Task.Run(() => m_portReservation.TryHold(PortHelper.DefaultSmbPort));
                MessageDialog.ShowError($"重启 SMB 服务失败：\n{ex.Message}", "重启失败");
                IsRunning = false;
                StatusSubtitle = "重启服务失败: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
        }

        public void DeleteShare(ShareConfig? share)
        {
            if (share == null) return;
            bool confirmed = MessageDialog.ShowConfirm(
                $"确定要移除共享 [{share.ShareName}] 吗？\n（仅取消共享映射，不会删除本地物理文件）",
                "确认移除共享",
                confirmText: "确认移除",
                cancelText: "取消",
                isDanger: true);

            if (confirmed)
            {
                m_config.Shares.Remove(share);
                Shares.Remove(share);
                OnPropertyChanged(nameof(HasShares));
                SaveConfig();
                RefreshUncAddresses();
                AddLog($"[共享] 已移除共享: [{share.ShareName}]");

                if (IsRunning)
                {
                    if (Shares.Count > 0)
                    {
                        if (m_engine != null && m_engine.ReloadConfig(m_config))
                        {
                            AddLog("[提示] 共享目录已热更新移除，服务持续运行中！");
                        }
                        else
                        {
                            _ = RestartServerAsync();
                        }
                    }
                    else
                    {
                        // 当最后一个共享目录被移除时，停止当前运行，保留用户原本的开启偏好以便下次添加共享时自动启动
                        _ = StopServerAsync(persistStoppedState: false);
                    }
                }
            }
        }

        public void SaveConfig()
        {
            try
            {
                ConfigManager.Save(m_config, m_configPath);
            }
            catch (Exception ex)
            {
                AddLog($"[错误] 保存配置文件失败: {ex.Message}");
            }
        }

        private void SyncCollectionsFromConfig()
        {
            // 过滤掉既未开启匿名访问、又没有任何授权用户的无意义共享配置，并校准只读显示状态
            var validShares = m_config.Shares
                .Where(s => s.AllowGuest ||
                            (s.Users != null && s.Users.Count > 0) ||
                            (s.UserPermissions != null && s.UserPermissions.Count > 0) ||
                            (s.AllowedUsers != null && s.AllowedUsers.Count > 0))
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

            Users.Clear();
            foreach (var u in m_config.Users)
            {
                Users.Add(u);
            }

            OnPropertyChanged(nameof(HasShares));
            OnPropertyChanged(nameof(HasUsers));
        }

        private void OnEngineLogReceived(object? sender, LogMessageEventArgs e)
        {
            AddLog($"[{e.Time:HH:mm:ss}] [{e.Severity}] {e.Message}");
        }

        public void AddLog(string message, string severity = "Info")
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                string line = message.StartsWith("[") ? message : $"[{DateTime.Now:HH:mm:ss}] [{severity}] {message}";
                Logs.Add(line);

                if (Logs.Count > 1000)
                {
                    Logs.RemoveAt(0);
                }
            });
        }

        private void CopyToClipboard(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                bool success = ClipboardHelper.SetText(text);
                if (success)
                {
                    MessageDialog.ShowToast("复制成功");
                }
                else
                {
                    AddLog($"[警告] 复制到剪贴板失败，系统剪贴板可能被其他程序独占", "Warning");
                    MessageDialog.ShowToast("复制失败，请重试");
                }
            }
            catch (Exception ex)
            {
                AddLog($"[异常] 复制操作异常: {ex.Message}", "Error");
            }
        }

        private void OpenFolder(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageDialog.ShowWarning($"打开本地文件夹失败：\n{ex.Message}", "无法打开目录");
                }
            }
            else
            {
                MessageDialog.ShowWarning($"本地目录不存在：\n{path}", "路径无效");
            }
        }
    }
}
