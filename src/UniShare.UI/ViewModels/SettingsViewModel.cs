using System;
using System.Threading.Tasks;
using UniShare.UI.Helpers;
using UniShare.UI.Services;

namespace UniShare.UI.ViewModels
{
    /// <summary>
    /// “设置”页：开机自启动与局域网设备自发现
    /// </summary>
    public class SettingsViewModel : ViewModelBase
    {
        private readonly ServerHost m_host;
        private readonly IDialogService m_dialogs;
        private bool m_isAutoStart;

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
            get => m_host.Config.EnableNetworkDiscovery;
            set
            {
                if (m_host.Config.EnableNetworkDiscovery != value)
                {
                    m_host.SetNetworkDiscoveryEnabled(value);
                    OnPropertyChanged();
                }
            }
        }

        public SettingsViewModel(ServerHost host, IDialogService dialogs)
        {
            m_host = host;
            m_dialogs = dialogs;

            _ = InitializeAutoStartStateAsync();
        }

        /// <summary>
        /// 以任务计划中的实际状态为准校准配置；配置要求自启但任务丢失时自动补建
        /// </summary>
        private async Task InitializeAutoStartStateAsync()
        {
            var config = m_host.Config;
            m_isAutoStart = config.AutoStart;
            OnPropertyChanged(nameof(IsAutoStart));

            bool wantAutoStart = config.AutoStart;

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
            if (config.AutoStart != taskExists)
            {
                config.AutoStart = taskExists;
                m_host.SaveConfig();
            }

            OnPropertyChanged(nameof(IsAutoStart));
        }

        private async void ApplyAutoStartSetting(bool enable)
        {
            SetProperty(ref m_isAutoStart, enable, nameof(IsAutoStart));

            var (success, msg) = await Task.Run(() => AutoStartHelper.SetAutoStart(enable, startMinimized: true));
            if (success)
            {
                m_host.Config.AutoStart = enable;
                m_host.Config.StartMinimized = true;
                m_host.SaveConfig();
                m_host.Log($"[设置] {msg}");
            }
            else
            {
                SetProperty(ref m_isAutoStart, !enable, nameof(IsAutoStart));
                m_dialogs.ShowWarning(msg, "开机自启动设置失败");
            }
        }
    }
}
