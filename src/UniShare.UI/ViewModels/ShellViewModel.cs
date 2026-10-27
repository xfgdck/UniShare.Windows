using System.Collections.Generic;
using UniShare.UI.Services;
using Wpf.Ui.Controls;

namespace UniShare.UI.ViewModels
{
    public class NavigationItem
    {
        public NavigationItem(string title, SymbolRegular icon, ViewModelBase page)
        {
            Title = title;
            Icon = icon;
            Page = page;
        }

        public string Title { get; }
        public SymbolRegular Icon { get; }
        public ViewModelBase Page { get; }
    }

    /// <summary>
    /// 主窗口外壳：左侧导航与当前页面
    /// </summary>
    public class ShellViewModel : ViewModelBase
    {
        private NavigationItem m_selectedItem;

        public ServerHost Host { get; }
        public SharesViewModel Shares { get; }
        public SettingsViewModel Settings { get; }
        public AboutViewModel About { get; }

        public IReadOnlyList<NavigationItem> NavigationItems { get; }

        public NavigationItem SelectedItem
        {
            get => m_selectedItem;
            set
            {
                // 列表重新获得焦点时可能短暂传入 null，保持当前页面不变
                if (value != null && SetProperty(ref m_selectedItem, value))
                {
                    OnPropertyChanged(nameof(CurrentPage));
                }
            }
        }

        public ViewModelBase CurrentPage => m_selectedItem.Page;

        public ShellViewModel(IDialogService dialogs)
        {
            Host = new ServerHost(dialogs);
            Shares = new SharesViewModel(Host, dialogs);
            Settings = new SettingsViewModel(Host, dialogs);
            About = new AboutViewModel(dialogs);

            NavigationItems = new[]
            {
                new NavigationItem("共享", SymbolRegular.FolderOpen24, Shares),
                new NavigationItem("设置", SymbolRegular.Settings24, Settings),
                new NavigationItem("关于", SymbolRegular.Info24, About)
            };
            m_selectedItem = NavigationItems[0];
        }

        public void NavigateToShares() => SelectedItem = NavigationItems[0];
    }
}
