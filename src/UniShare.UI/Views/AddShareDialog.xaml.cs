using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UniShare.Core;
using UniShare.Core.Models;
using Wpf.Ui.Controls;

namespace UniShare.UI.Views
{
    public class ShareUserItem : INotifyPropertyChanged
    {
        private int m_accessLevelIndex; // 0: 读写, 1: 只读

        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        public int AccessLevelIndex
        {
            get => m_accessLevelIndex;
            set
            {
                if (m_accessLevelIndex != value)
                {
                    m_accessLevelIndex = value;
                    OnPropertyChanged(nameof(AccessLevelIndex));
                    OnPropertyChanged(nameof(AccessLevel));
                }
            }
        }

        public ShareAccessLevel AccessLevel => AccessLevelIndex == 1 ? ShareAccessLevel.ReadOnly : ShareAccessLevel.ReadWrite;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class AddShareDialog : FluentWindow
    {
        // Windows 共享名禁用字符（参照 LanmanServer NetShareAdd 的命名限制）
        private static readonly char[] s_invalidShareNameChars = "\"/\\[]:|<>+=;,*?".ToCharArray();
        private static readonly string[] s_reservedShareNames = { "IPC$", "ADMIN$", "PRINT$" };
        private readonly ShareConfig? m_editingShare;
        private readonly List<ShareConfig> m_existingShares;
        public ObservableCollection<ShareUserItem> UserItems { get; } = new ObservableCollection<ShareUserItem>();

        public ShareConfig? Result { get; private set; }

        public AddShareDialog(ShareConfig? editingShare = null, IEnumerable<ShareConfig>? existingShares = null)
        {
            InitializeComponent();
            m_editingShare = editingShare;
            m_existingShares = existingShares != null ? existingShares.ToList() : new List<ShareConfig>();

            ItemsUserPermissions.ItemsSource = UserItems;

            if (m_editingShare != null)
            {
                // 编辑模式
                Title = "编辑共享";
                TitleBarControl.Title = string.Empty;
                TxtHeader.Text = $"编辑共享 - {m_editingShare.ShareName}";
                BtnSave.Content = "保存修改";

                TxtPath.Text = m_editingShare.LocalPath;
                TxtShareName.Text = m_editingShare.ShareName;
                ChkAllowGuest.IsChecked = m_editingShare.AllowGuest;
                CmbGuestPermission.SelectedIndex = m_editingShare.GuestAccess == ShareAccessLevel.ReadOnly ? 1 : 0;
                ChkEnableSmb1.IsChecked = m_editingShare.EnableSmb1;

                // 载入该共享专属用户
                if (m_editingShare.Users != null && m_editingShare.Users.Count > 0)
                {
                    foreach (var u in m_editingShare.Users)
                    {
                        UserItems.Add(new ShareUserItem
                        {
                            Username = u.Username,
                            Password = u.Password,
                            AccessLevelIndex = u.AccessLevel == ShareAccessLevel.ReadOnly ? 1 : 0
                        });
                    }
                }
                else if (m_editingShare.UserPermissions != null)
                {
                    foreach (var p in m_editingShare.UserPermissions)
                    {
                        UserItems.Add(new ShareUserItem
                        {
                            Username = p.Username,
                            Password = string.Empty,
                            AccessLevelIndex = p.AccessLevel == ShareAccessLevel.ReadOnly ? 1 : 0
                        });
                    }
                }
            }
            else
            {
                // 新建模式
                Title = "新建共享";
                TitleBarControl.Title = string.Empty;
                TxtHeader.Text = "新建共享";
                BtnSave.Content = "创建共享";

                ChkAllowGuest.IsChecked = false;
                CmbGuestPermission.SelectedIndex = 0; // 默认读写
                ChkEnableSmb1.IsChecked = false;
            }

            UpdateGuestState();
            UpdateUserItemsVisibility();
        }

        private void UpdateUserItemsVisibility()
        {
            if (TxtNoUsersHint != null)
            {
                TxtNoUsersHint.Visibility = UserItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void BtnDeleteUserFromItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ShareUserItem item)
            {
                bool confirmed = MessageDialog.ShowConfirm(
                    $"确定要从当前共享目录中移除用户 [{item.Username}] 吗？",
                    "删除用户确认",
                    confirmText: "确认删除",
                    cancelText: "取消",
                    isDanger: true,
                    owner: this);

                if (confirmed)
                {
                    UserItems.Remove(item);
                    UpdateUserItemsVisibility();
                }
            }
        }

        private void ChkAllowGuest_Changed(object sender, RoutedEventArgs e)
        {
            UpdateGuestState();
        }

        private void UpdateGuestState()
        {
            if (CmbGuestPermission != null)
            {
                CmbGuestPermission.IsEnabled = ChkAllowGuest.IsChecked == true;
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择要共享的本地文件夹",
                Multiselect = false
            };

            if (dialog.ShowDialog(this) == true)
            {
                TxtPath.Text = dialog.FolderName;
            }
        }

        private void TxtPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtShareName.Text) && !string.IsNullOrWhiteSpace(TxtPath.Text))
            {
                try
                {
                    string dirName = Path.GetFileName(TxtPath.Text.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(dirName))
                    {
                        TxtShareName.Text = dirName.ToLowerInvariant();
                    }
                }
                catch { }
            }
        }

        private void BtnAddNewUser_Click(object sender, RoutedEventArgs e)
        {
            string username = TxtNewUser.Text.Trim();
            string password = TxtNewPassword.Password;

            if (string.IsNullOrWhiteSpace(username))
            {
                MessageDialog.ShowWarning("请输入要添加的用户名。", "用户名不能为空", owner: this);
                return;
            }

            if (string.Equals(username, "guest", StringComparison.OrdinalIgnoreCase))
            {
                MessageDialog.ShowWarning("用户名 \"guest\" 为系统保留标识，请直接开启下方的匿名访问，或使用其他用户名。", "保留用户名", owner: this);
                return;
            }

            if (UserItems.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                MessageDialog.ShowInfo($"用户 [{username}] 已经在当前共享的用户列表中。", "重复添加提示", owner: this);
                return;
            }

            if (!ConfirmUserPassword(username, password))
            {
                return;
            }

            int levelIndex = CmbNewUserPermission.SelectedIndex;

            UserItems.Add(new ShareUserItem
            {
                Username = username,
                Password = password,
                AccessLevelIndex = levelIndex
            });

            TxtNewUser.Text = string.Empty;
            TxtNewPassword.Password = string.Empty;
            UpdateUserItemsVisibility();
        }

        /// <summary>
        /// 校验同名用户跨共享密码一致性及空密码风险提示，返回 false 表示不应添加
        /// </summary>
        private bool ConfirmUserPassword(string username, string password)
        {
            var conflict = ShareAccessEvaluator.FindPasswordConflict(m_existingShares, m_editingShare, username, password);
            if (conflict != null)
            {
                MessageDialog.ShowWarning(
                    $"用户名 [{username}] 已在共享 [{conflict.ShareName}] 中使用，但密码与此处输入的不同。\n\n设备登录发生在选择共享目录之前，同一个用户名在所有共享中只能使用同一个密码。请输入与该共享相同的密码，或改用其他用户名。",
                    "用户密码冲突",
                    owner: this);
                return false;
            }

            if (string.IsNullOrEmpty(password))
            {
                return MessageDialog.ShowConfirm(
                    $"用户 [{username}] 未设置密码。\n\nWindows 电脑默认禁止使用空密码账户进行网络登录，可能无法连接；手机、电视等设备一般不受影响。\n\n确定要添加空密码用户吗？",
                    "空密码提示",
                    confirmText: "仍然添加",
                    cancelText: "返回修改",
                    owner: this);
            }

            return true;
        }

        private static bool IsDriveRoot(string fullPath)
        {
            string? root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrEmpty(root) &&
                   string.Equals(root.TrimEnd('\\', '/'), fullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtPath.Text.Trim();
            string name = TxtShareName.Text.Trim();

            if (string.IsNullOrWhiteSpace(path))
            {
                MessageDialog.ShowWarning("请选择或输入本地物理文件夹路径。", "路径不能为空", owner: this);
                return;
            }

            try
            {
                if (!Path.IsPathFullyQualified(path))
                {
                    MessageDialog.ShowWarning("请输入完整的本地文件夹绝对路径（例如 D:\\Share）。", "路径格式无效", owner: this);
                    return;
                }
                path = Path.GetFullPath(path);
            }
            catch
            {
                MessageDialog.ShowWarning("本地文件夹路径格式无效，请重新选择。", "路径格式无效", owner: this);
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageDialog.ShowWarning("请输入共享访问名称。", "共享名称不能为空", owner: this);
                return;
            }

            if (name.Length > 80 || name.IndexOfAny(s_invalidShareNameChars) >= 0 || name.Any(char.IsControl))
            {
                MessageDialog.ShowWarning("共享名称不能超过 80 个字符，且不能包含以下字符：\" / \\ [ ] : | < > + = ; , * ?", "共享名称无效", owner: this);
                return;
            }

            if (s_reservedShareNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                (name.Length == 2 && char.IsLetter(name[0]) && name[1] == '$'))
            {
                MessageDialog.ShowWarning($"[{name}] 是 Windows 系统保留的共享名称，请更换其他名称。", "共享名称无效", owner: this);
                return;
            }

            // 校验是否与其他已有共享目录重名或重复路径
            foreach (var existing in m_existingShares)
            {
                if (ReferenceEquals(existing, m_editingShare))
                {
                    continue;
                }

                if (string.Equals(existing.ShareName, name, StringComparison.OrdinalIgnoreCase))
                {
                    MessageDialog.ShowWarning($"已存在同名共享 [{name}]，请更换其他共享名称。", "共享名称冲突", owner: this);
                    return;
                }

                try
                {
                    string existingFullPath = Path.GetFullPath(existing.LocalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string currentFullPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(existingFullPath, currentFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageDialog.ShowWarning($"该本地文件夹已在共享 [{existing.ShareName}] 中添加，无需重复创建。", "共享路径重复", owner: this);
                        return;
                    }
                }
                catch { }
            }

            // 检查用户在“+ 添加用户”输入框中是否填写了内容但忘记点击“添加”按钮
            string pendingUsername = TxtNewUser.Text.Trim();
            if (!string.IsNullOrWhiteSpace(pendingUsername))
            {
                MessageDialog.ShowWarning(
                    $"检测到您在“添加用户”输入框中填写了用户名 [{pendingUsername}]，但尚未点击右侧的【添加】按钮。\n\n请先点击【添加】按钮将其加入列表，或清空输入框后再保存。",
                    "存在未添加的用户输入",
                    owner: this);
                return;
            }

            bool allowGuest = ChkAllowGuest.IsChecked == true;

            // 同名用户跨共享密码一致性校验（兼容旧版配置中遗留的冲突数据）
            foreach (var item in UserItems)
            {
                var conflict = ShareAccessEvaluator.FindPasswordConflict(m_existingShares, m_editingShare, item.Username, item.Password);
                if (conflict != null)
                {
                    MessageDialog.ShowWarning(
                        $"用户 [{item.Username}] 在共享 [{conflict.ShareName}] 中的密码与此处不同。\n\n同一个用户名在所有共享中只能使用同一个密码，请删除该用户后用相同密码重新添加，或改用其他用户名。",
                        "用户密码冲突",
                        owner: this);
                    return;
                }
            }

            if (IsDriveRoot(path))
            {
                bool confirmedRoot = MessageDialog.ShowConfirm(
                    $"您选择的是整个磁盘分区 [{path}]。\n\n共享整个分区会让访问者看到该盘上的全部文件，建议只共享具体的文件夹。确定要继续吗？",
                    "共享整个磁盘",
                    confirmText: "继续共享",
                    cancelText: "返回修改",
                    isDanger: true,
                    owner: this);
                if (!confirmedRoot) return;
            }

            if (!Directory.Exists(path))
            {
                bool create = MessageDialog.ShowConfirm(
                    $"本地文件夹不存在：\n{path}\n\n是否立即创建该文件夹？",
                    "文件夹不存在",
                    confirmText: "创建文件夹",
                    cancelText: "返回修改",
                    owner: this);
                if (!create) return;

                try
                {
                    Directory.CreateDirectory(path);
                }
                catch (Exception ex)
                {
                    MessageDialog.ShowWarning($"创建文件夹失败：\n{ex.Message}", "无法创建文件夹", owner: this);
                    return;
                }
            }

            // 核心校验：既未添加任何用户，也未开启匿名访问时，禁止创建/保存无意义的共享
            if (UserItems.Count == 0 && !allowGuest)
            {
                MessageDialog.ShowWarning(
                    "当前未添加任何访问用户，且未开启匿名访问。\n\n这样的共享目录没有任何人可以访问，请至少添加一个访问用户，或勾选“允许匿名访问”。",
                    "请配置访问方式",
                    owner: this);
                return;
            }

            // 收集当前目录用户
            var shareUsers = new List<ShareUserConfig>();
            var userPermissions = new List<UserPermissionConfig>();
            var allowedUsers = new List<string>();

            foreach (var item in UserItems)
            {
                shareUsers.Add(new ShareUserConfig
                {
                    Username = item.Username,
                    Password = item.Password,
                    AccessLevel = item.AccessLevel
                });
                userPermissions.Add(new UserPermissionConfig
                {
                    Username = item.Username,
                    AccessLevel = item.AccessLevel
                });
                allowedUsers.Add(item.Username);
            }

            ShareAccessLevel guestAccess = CmbGuestPermission.SelectedIndex == 1 ? ShareAccessLevel.ReadOnly : ShareAccessLevel.ReadWrite;
            bool enableSmb1 = ChkEnableSmb1.IsChecked == true;

            // 判断该共享目录整体是否为纯只读（没有任何读写途径时标记为只读，以便主界面准确展示只读/读写标签）
            bool hasAnyWriteAccess = (allowGuest && guestAccess == ShareAccessLevel.ReadWrite) ||
                                     shareUsers.Any(u => u.AccessLevel == ShareAccessLevel.ReadWrite);
            bool isEffectivelyReadOnly = !hasAnyWriteAccess;

            // 生成描述概括
            string guestDesc = allowGuest ? (guestAccess == ShareAccessLevel.ReadWrite ? "匿名读写" : "匿名只读") : "禁止匿名";
            string userDesc = shareUsers.Count > 0 ? $"{shareUsers.Count}个授权用户" : "无独立用户";
            string desc = $"{guestDesc} | {userDesc}";

            Result = new ShareConfig
            {
                ShareName = name,
                LocalPath = path,
                IsReadOnly = isEffectivelyReadOnly,
                AllowGuest = allowGuest,
                GuestAccess = guestAccess,
                Users = shareUsers,
                UserPermissions = userPermissions,
                AllowedUsers = allowedUsers,
                EnableSmb1 = enableSmb1,
                Description = desc
            };

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
