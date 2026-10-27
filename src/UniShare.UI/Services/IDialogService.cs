using System;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using UniShare.UI.ViewModels;
using UniShare.UI.Views;

namespace UniShare.UI.Services
{
    /// <summary>
    /// 视图模型使用的弹窗抽象，使视图模型不直接依赖具体窗口
    /// </summary>
    public interface IDialogService
    {
        void ShowInfo(string message, string title);
        void ShowWarning(string message, string title);
        void ShowError(string message, string title);
        bool Confirm(string message, string title, string confirmText, string cancelText, bool isDanger = false);
        void ShowToast(string text);
        string? PickFolder(string title);

        /// <summary>
        /// 打开新建 / 编辑共享对话框，返回 true 表示用户已保存
        /// </summary>
        bool ShowShareEditor(ShareEditorViewModel editor);
    }

    /// <summary>
    /// 基于 MessageDialog 的默认实现；owner 为空时由 MessageDialog 自动选择当前活动窗口
    /// </summary>
    public class DialogService : IDialogService
    {
        private readonly Func<Window?> m_ownerProvider;

        public DialogService(Func<Window?>? ownerProvider = null)
        {
            m_ownerProvider = ownerProvider ?? (() => null);
        }

        private Window? Owner => m_ownerProvider();

        public void ShowInfo(string message, string title) => MessageDialog.ShowInfo(message, title, owner: Owner);

        public void ShowWarning(string message, string title) => MessageDialog.ShowWarning(message, title, owner: Owner);

        public void ShowError(string message, string title) => MessageDialog.ShowError(message, title, owner: Owner);

        public bool Confirm(string message, string title, string confirmText, string cancelText, bool isDanger = false)
        {
            return MessageDialog.ShowConfirm(message, title, confirmText: confirmText, cancelText: cancelText, isDanger: isDanger, owner: Owner);
        }

        public void ShowToast(string text) => MessageDialog.ShowToast(text);

        public string? PickFolder(string title)
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                Multiselect = false
            };

            Window? owner = Owner ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            bool? result = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return result == true ? dialog.FolderName : null;
        }

        public bool ShowShareEditor(ShareEditorViewModel editor)
        {
            var dialog = new ShareEditorDialog(editor)
            {
                Owner = Owner ?? Application.Current?.MainWindow
            };
            return dialog.ShowDialog() == true;
        }
    }
}
