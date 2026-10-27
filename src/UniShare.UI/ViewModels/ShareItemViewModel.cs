using System;
using System.Linq;
using System.Windows.Input;
using UniShare.Core.Models;

namespace UniShare.UI.ViewModels
{
    /// <summary>
    /// 共享卡片的展示模型：标签与图标色块
    /// </summary>
    public class ShareItemViewModel : ViewModelBase
    {
        public ShareConfig Share { get; }

        public string Name => Share.ShareName;
        public string LocalPath => Share.LocalPath;
        public bool IsReadOnly => Share.IsReadOnly;
        public bool AllowGuest => Share.AllowGuest;
        public bool EnableSmb1 => Share.EnableSmb1;

        /// <summary>
        /// 图标色块编号（0-4），按共享名稳定分配，让不同共享一眼可区分
        /// </summary>
        public int TileIndex { get; }

        public ICommand OpenFolderCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand RemoveCommand { get; }

        public ShareItemViewModel(
            ShareConfig share,
            Action<ShareItemViewModel> open,
            Action<ShareItemViewModel> edit,
            Action<ShareItemViewModel> remove)
        {
            Share = share;
            TileIndex = (share.ShareName ?? string.Empty).Sum(c => c) % 5;

            OpenFolderCommand = new RelayCommand(() => open(this));
            EditCommand = new RelayCommand(() => edit(this));
            RemoveCommand = new RelayCommand(() => remove(this));
        }
    }
}
