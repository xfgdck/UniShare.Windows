using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using UniShare.UI.Services;

namespace UniShare.UI.ViewModels
{
    /// <summary>
    /// “共享”页：服务状态与启停、共享目录列表及增删改
    /// </summary>
    public class SharesViewModel : ViewModelBase
    {
        private readonly ServerHost m_host;
        private readonly IDialogService m_dialogs;

        /// <summary>
        /// 共享列表各行
        /// </summary>
        public ObservableCollection<ShareItemViewModel> Items { get; } = new ObservableCollection<ShareItemViewModel>();

        public bool HasShares => m_host.Shares.Count > 0;
        public int ShareCount => m_host.Shares.Count;

        public bool IsRunning => m_host.IsRunning;
        public bool IsBusy => m_host.IsBusy;

        public string StatusText => m_host.IsBusy
            ? m_host.BusyMessage
            : m_host.IsRunning ? "共享已开启" : "共享已关闭";

        public string StatusDetail => m_host.StatusDetail;

        public string ToggleButtonText => m_host.IsRunning ? "关闭共享" : "开启共享";

        public ICommand ToggleServerCommand { get; }
        public ICommand AddShareCommand { get; }

        public SharesViewModel(ServerHost host, IDialogService dialogs)
        {
            m_host = host;
            m_dialogs = dialogs;

            ToggleServerCommand = new RelayCommand(async () =>
            {
                if (m_host.IsRunning)
                {
                    await m_host.StopServerAsync();
                }
                else if (m_host.Shares.Count == 0)
                {
                    m_dialogs.ShowInfo("当前尚未添加任何共享目录。\n请先新建一个共享目录，再开启共享。", "还没有共享目录");
                }
                else
                {
                    await m_host.StartServerAsync();
                }
            }, () => !m_host.IsBusy);

            AddShareCommand = new RelayCommand(AddShare);

            m_host.PropertyChanged += OnHostPropertyChanged;
            m_host.Shares.CollectionChanged += OnSharesChanged;

            RebuildItems();
        }

        private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ServerHost.IsRunning):
                    OnPropertyChanged(nameof(IsRunning));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(ToggleButtonText));
                    break;
                case nameof(ServerHost.IsBusy):
                case nameof(ServerHost.BusyMessage):
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(StatusText));
                    (ToggleServerCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    break;
                case nameof(ServerHost.StatusDetail):
                    OnPropertyChanged(nameof(StatusDetail));
                    break;
            }
        }

        private void OnSharesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildItems();

        private void RebuildItems()
        {
            Items.Clear();
            foreach (var share in m_host.Shares)
            {
                Items.Add(new ShareItemViewModel(share, OpenFolder, EditShare, RemoveShare));
            }
            OnPropertyChanged(nameof(HasShares));
            OnPropertyChanged(nameof(ShareCount));
        }

        private void AddShare()
        {
            var editor = new ShareEditorViewModel(null, m_host.Shares, m_dialogs);
            if (m_dialogs.ShowShareEditor(editor) && editor.Result != null)
            {
                m_host.AddShare(editor.Result);
            }
        }

        private void EditShare(ShareItemViewModel item)
        {
            var editor = new ShareEditorViewModel(item.Share, m_host.Shares, m_dialogs);
            if (m_dialogs.ShowShareEditor(editor) && editor.Result != null)
            {
                m_host.UpdateShare(item.Share, editor.Result);
            }
        }

        private void RemoveShare(ShareItemViewModel item)
        {
            bool confirmed = m_dialogs.Confirm(
                $"确定要移除共享 [{item.Name}] 吗？\n（仅取消共享映射，不会删除本地物理文件）",
                "确认移除共享",
                confirmText: "确认移除",
                cancelText: "取消",
                isDanger: true);

            if (confirmed)
            {
                m_host.RemoveShare(item.Share);
            }
        }

        private void OpenFolder(ShareItemViewModel item)
        {
            string path = item.LocalPath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                m_dialogs.ShowWarning($"本地目录不存在：\n{path}", "路径无效");
                return;
            }

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
                m_dialogs.ShowWarning($"打开本地文件夹失败：\n{ex.Message}", "无法打开目录");
            }
        }
    }
}
