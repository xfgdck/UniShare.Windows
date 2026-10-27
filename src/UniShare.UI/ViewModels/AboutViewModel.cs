using System;
using System.Diagnostics;
using System.Windows.Input;
using UniShare.Core;
using UniShare.UI.Services;

namespace UniShare.UI.ViewModels
{
    /// <summary>
    /// “关于”页：版本号、手动检查更新与项目主页
    /// </summary>
    public class AboutViewModel : ViewModelBase
    {
        private readonly IDialogService m_dialogs;
        private bool m_isChecking;
        private string m_updateStatus = string.Empty;
        private string? m_newVersionUrl;

        /// <summary>
        /// 程序版本（编译日期），与安装包版本一致
        /// </summary>
        public string AppVersion => AppVersionInfo.Current;

        public bool IsChecking
        {
            get => m_isChecking;
            private set
            {
                if (SetProperty(ref m_isChecking, value))
                {
                    (CheckUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// 检查结果，例如“已是最新版本”
        /// </summary>
        public string UpdateStatus
        {
            get => m_updateStatus;
            private set => SetProperty(ref m_updateStatus, value);
        }

        public bool HasNewVersion => m_newVersionUrl != null;

        public ICommand CheckUpdateCommand { get; }
        public ICommand DownloadUpdateCommand { get; }
        public ICommand OpenGitHubCommand { get; }

        public AboutViewModel(IDialogService dialogs)
        {
            m_dialogs = dialogs;
            CheckUpdateCommand = new RelayCommand(CheckUpdate, () => !IsChecking);
            DownloadUpdateCommand = new RelayCommand(() => OpenUrl(m_newVersionUrl ?? UpdateChecker.ReleasesUrl));
            OpenGitHubCommand = new RelayCommand(() => OpenUrl(UpdateChecker.RepositoryUrl));
        }

        private async void CheckUpdate()
        {
            IsChecking = true;
            UpdateStatus = "正在检查...";
            SetNewVersionUrl(null);

            try
            {
                var latest = await UpdateChecker.GetLatestReleaseAsync();
                if (UpdateChecker.IsNewer(latest.Version, AppVersion))
                {
                    UpdateStatus = $"发现新版本 v{latest.Version}";
                    SetNewVersionUrl(latest.PageUrl);
                }
                else
                {
                    UpdateStatus = "已是最新版本";
                }
            }
            catch
            {
                UpdateStatus = "检查失败，请检查网络后重试";
            }
            finally
            {
                IsChecking = false;
            }
        }

        private void SetNewVersionUrl(string? url)
        {
            m_newVersionUrl = url;
            OnPropertyChanged(nameof(HasNewVersion));
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                m_dialogs.ShowWarning($"无法打开浏览器：\n{ex.Message}", "打开失败");
            }
        }
    }
}
