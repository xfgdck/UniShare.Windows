using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using UniShare.Core;
using UniShare.Core.Models;
using UniShare.UI.Services;

namespace UniShare.UI.ViewModels
{
    public class ShareUserItem : ViewModelBase
    {
        private int m_accessLevelIndex; // 0: 读写, 1: 只读

        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;

        public int AccessLevelIndex
        {
            get => m_accessLevelIndex;
            set => SetProperty(ref m_accessLevelIndex, value);
        }

        public ShareAccessLevel AccessLevel => AccessLevelIndex == 1 ? ShareAccessLevel.ReadOnly : ShareAccessLevel.ReadWrite;

        public ICommand? RemoveCommand { get; init; }
    }

    /// <summary>
    /// 新建 / 编辑共享对话框：表单字段、保存前校验与保存结果
    /// </summary>
    public class ShareEditorViewModel : ViewModelBase
    {
        // Windows 共享名禁用字符（参照 LanmanServer NetShareAdd 的命名限制）
        private static readonly char[] s_invalidShareNameChars = "\"/\\[]:|<>+=;,*?".ToCharArray();
        private static readonly string[] s_reservedShareNames = { "IPC$", "ADMIN$", "PRINT$" };

        private readonly ShareConfig? m_editingShare;
        private readonly List<ShareConfig> m_existingShares;
        private readonly IDialogService m_dialogs;

        private string m_localPath = string.Empty;
        private string m_shareName = string.Empty;
        private bool m_isShareNameAuto = true;
        private string m_newUsername = string.Empty;
        private int m_newUserAccessIndex;
        private bool m_allowGuest;
        private int m_guestAccessIndex;
        private bool m_enableSmb1;

        public bool IsEditMode => m_editingShare != null;
        public string Title => IsEditMode ? $"编辑共享 - {m_editingShare!.ShareName}" : "新建共享";
        public string SaveButtonText => IsEditMode ? "保存修改" : "创建共享";

        public ObservableCollection<ShareUserItem> Users { get; } = new ObservableCollection<ShareUserItem>();
        public bool HasUsers => Users.Count > 0;

        /// <summary>
        /// 保存成功后生成的共享配置
        /// </summary>
        public ShareConfig? Result { get; private set; }

        /// <summary>
        /// 请求关闭对话框（true: 已保存，false: 取消）
        /// </summary>
        public event Action<bool>? CloseRequested;

        public string LocalPath
        {
            get => m_localPath;
            set
            {
                if (SetProperty(ref m_localPath, value))
                {
                    if (m_isShareNameAuto)
                    {
                        SetAutoShareName();
                    }
                }
            }
        }

        public string ShareName
        {
            get => m_shareName;
            set
            {
                if (SetProperty(ref m_shareName, value))
                {
                    // 用户手动修改名称后不再随路径自动生成；清空后恢复自动
                    m_isShareNameAuto = string.IsNullOrWhiteSpace(value);
                }
            }
        }

        public string NewUsername
        {
            get => m_newUsername;
            set => SetProperty(ref m_newUsername, value);
        }

        public int NewUserAccessIndex
        {
            get => m_newUserAccessIndex;
            set => SetProperty(ref m_newUserAccessIndex, value);
        }

        public bool AllowGuest
        {
            get => m_allowGuest;
            set => SetProperty(ref m_allowGuest, value);
        }

        public int GuestAccessIndex
        {
            get => m_guestAccessIndex;
            set => SetProperty(ref m_guestAccessIndex, value);
        }

        public bool EnableSmb1
        {
            get => m_enableSmb1;
            set => SetProperty(ref m_enableSmb1, value);
        }

        public ICommand BrowseCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }

        public ShareEditorViewModel(ShareConfig? editingShare, IEnumerable<ShareConfig> existingShares, IDialogService dialogs)
        {
            m_editingShare = editingShare;
            m_existingShares = existingShares.ToList();
            m_dialogs = dialogs;

            BrowseCommand = new RelayCommand(Browse);
            SaveCommand = new RelayCommand(Save);
            CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(false));

            Users.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasUsers));

            if (editingShare != null)
            {
                m_localPath = editingShare.LocalPath;
                m_shareName = editingShare.ShareName;
                m_isShareNameAuto = false;
                m_allowGuest = editingShare.AllowGuest;
                m_guestAccessIndex = editingShare.GuestAccess == ShareAccessLevel.ReadOnly ? 1 : 0;
                m_enableSmb1 = editingShare.EnableSmb1;

                // 载入该共享专属用户（兼容只有 UserPermissions 的旧版数据）
                if (editingShare.Users != null && editingShare.Users.Count > 0)
                {
                    foreach (var u in editingShare.Users)
                    {
                        Users.Add(CreateUserItem(u.Username, u.Password, u.AccessLevel));
                    }
                }
                else if (editingShare.UserPermissions != null)
                {
                    foreach (var p in editingShare.UserPermissions)
                    {
                        Users.Add(CreateUserItem(p.Username, string.Empty, p.AccessLevel));
                    }
                }
            }
        }

        private ShareUserItem CreateUserItem(string username, string password, ShareAccessLevel level)
        {
            ShareUserItem? item = null;
            item = new ShareUserItem
            {
                Username = username,
                Password = password,
                AccessLevelIndex = level == ShareAccessLevel.ReadOnly ? 1 : 0,
                RemoveCommand = new RelayCommand(() => RemoveUser(item!))
            };
            return item;
        }

        private void SetAutoShareName()
        {
            string name = string.Empty;
            try
            {
                if (!string.IsNullOrWhiteSpace(m_localPath))
                {
                    name = Path.GetFileName(m_localPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToLowerInvariant();
                }
            }
            catch { }

            if (m_shareName != name)
            {
                m_shareName = name;
                OnPropertyChanged(nameof(ShareName));
            }
        }

        private void Browse()
        {
            string? folder = m_dialogs.PickFolder("选择要共享的本地文件夹");
            if (folder != null)
            {
                LocalPath = folder;
            }
        }

        /// <summary>
        /// 添加授权用户；返回 true 表示已添加（由视图清空密码框）
        /// </summary>
        public bool TryAddUser(string password)
        {
            string username = NewUsername.Trim();

            if (string.IsNullOrWhiteSpace(username))
            {
                m_dialogs.ShowWarning("请输入要添加的用户名。", "用户名不能为空");
                return false;
            }

            if (string.Equals(username, "guest", StringComparison.OrdinalIgnoreCase))
            {
                m_dialogs.ShowWarning("用户名 \"guest\" 为系统保留标识，请直接开启下方的匿名访问，或使用其他用户名。", "保留用户名");
                return false;
            }

            if (Users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                m_dialogs.ShowInfo($"用户 [{username}] 已经在当前共享的用户列表中。", "重复添加提示");
                return false;
            }

            var conflict = ShareAccessEvaluator.FindPasswordConflict(m_existingShares, m_editingShare, username, password);
            if (conflict != null)
            {
                m_dialogs.ShowWarning(
                    $"用户名 [{username}] 已在共享 [{conflict.ShareName}] 中使用，但密码与此处输入的不同。\n\n设备登录发生在选择共享目录之前，同一个用户名在所有共享中只能使用同一个密码。请输入与该共享相同的密码，或改用其他用户名。",
                    "用户密码冲突");
                return false;
            }

            if (string.IsNullOrEmpty(password) && !m_dialogs.Confirm(
                    $"用户 [{username}] 未设置密码。\n\nWindows 电脑默认禁止使用空密码账户进行网络登录，可能无法连接；手机、电视等设备一般不受影响。\n\n确定要添加空密码用户吗？",
                    "空密码提示",
                    confirmText: "仍然添加",
                    cancelText: "返回修改"))
            {
                return false;
            }

            Users.Add(CreateUserItem(username, password, NewUserAccessIndex == 1 ? ShareAccessLevel.ReadOnly : ShareAccessLevel.ReadWrite));
            NewUsername = string.Empty;
            return true;
        }

        private void RemoveUser(ShareUserItem item)
        {
            bool confirmed = m_dialogs.Confirm(
                $"确定要从当前共享目录中移除用户 [{item.Username}] 吗？",
                "删除用户确认",
                confirmText: "确认删除",
                cancelText: "取消",
                isDanger: true);

            if (confirmed)
            {
                Users.Remove(item);
            }
        }

        private static bool IsDriveRoot(string fullPath)
        {
            string? root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrEmpty(root) &&
                   string.Equals(root.TrimEnd('\\', '/'), fullPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private void Save()
        {
            string path = LocalPath.Trim();
            string name = ShareName.Trim();

            if (string.IsNullOrWhiteSpace(path))
            {
                m_dialogs.ShowWarning("请选择或输入本地物理文件夹路径。", "路径不能为空");
                return;
            }

            try
            {
                if (!Path.IsPathFullyQualified(path))
                {
                    m_dialogs.ShowWarning("请输入完整的本地文件夹绝对路径（例如 D:\\Share）。", "路径格式无效");
                    return;
                }
                path = Path.GetFullPath(path);
            }
            catch
            {
                m_dialogs.ShowWarning("本地文件夹路径格式无效，请重新选择。", "路径格式无效");
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                m_dialogs.ShowWarning("请输入共享访问名称。", "共享名称不能为空");
                return;
            }

            if (name.Length > 80 || name.IndexOfAny(s_invalidShareNameChars) >= 0 || name.Any(char.IsControl))
            {
                m_dialogs.ShowWarning("共享名称不能超过 80 个字符，且不能包含以下字符：\" / \\ [ ] : | < > + = ; , * ?", "共享名称无效");
                return;
            }

            if (s_reservedShareNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                (name.Length == 2 && char.IsLetter(name[0]) && name[1] == '$'))
            {
                m_dialogs.ShowWarning($"[{name}] 是 Windows 系统保留的共享名称，请更换其他名称。", "共享名称无效");
                return;
            }

            // 校验是否与其他已有共享目录重名或重复路径
            string currentFullPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var existing in m_existingShares.Where(s => !ReferenceEquals(s, m_editingShare)))
            {
                if (string.Equals(existing.ShareName, name, StringComparison.OrdinalIgnoreCase))
                {
                    m_dialogs.ShowWarning($"已存在同名共享 [{name}]，请更换其他共享名称。", "共享名称冲突");
                    return;
                }

                try
                {
                    string existingFullPath = Path.GetFullPath(existing.LocalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(existingFullPath, currentFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        m_dialogs.ShowWarning($"该本地文件夹已在共享 [{existing.ShareName}] 中添加，无需重复创建。", "共享路径重复");
                        return;
                    }
                }
                catch { }
            }

            // 检查是否在“添加用户”输入框中填写了用户名但忘记点击“添加用户”按钮
            string pendingUsername = NewUsername.Trim();
            if (!string.IsNullOrWhiteSpace(pendingUsername))
            {
                m_dialogs.ShowWarning(
                    $"检测到您在“添加用户”输入框中填写了用户名 [{pendingUsername}]，但尚未点击右侧的【添加用户】按钮。\n\n请先点击【添加用户】按钮将其加入列表，或清空输入框后再保存。",
                    "存在未添加的用户输入");
                return;
            }

            // 同名用户跨共享密码一致性校验（兼容旧版配置中遗留的冲突数据）
            foreach (var item in Users)
            {
                var conflict = ShareAccessEvaluator.FindPasswordConflict(m_existingShares, m_editingShare, item.Username, item.Password);
                if (conflict != null)
                {
                    m_dialogs.ShowWarning(
                        $"用户 [{item.Username}] 在共享 [{conflict.ShareName}] 中的密码与此处不同。\n\n同一个用户名在所有共享中只能使用同一个密码，请删除该用户后用相同密码重新添加，或改用其他用户名。",
                        "用户密码冲突");
                    return;
                }
            }

            if (IsDriveRoot(path) && !m_dialogs.Confirm(
                    $"您选择的是整个磁盘分区 [{path}]。\n\n共享整个分区会让访问者看到该盘上的全部文件，建议只共享具体的文件夹。确定要继续吗？",
                    "共享整个磁盘",
                    confirmText: "继续共享",
                    cancelText: "返回修改",
                    isDanger: true))
            {
                return;
            }

            if (!Directory.Exists(path))
            {
                if (!m_dialogs.Confirm(
                        $"本地文件夹不存在：\n{path}\n\n是否立即创建该文件夹？",
                        "文件夹不存在",
                        confirmText: "创建文件夹",
                        cancelText: "返回修改"))
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(path);
                }
                catch (Exception ex)
                {
                    m_dialogs.ShowWarning($"创建文件夹失败：\n{ex.Message}", "无法创建文件夹");
                    return;
                }
            }

            // 核心校验：既未添加任何用户，也未开启匿名访问时，禁止创建/保存无意义的共享
            if (Users.Count == 0 && !AllowGuest)
            {
                m_dialogs.ShowWarning(
                    "当前未添加任何访问用户，且未开启匿名访问。\n\n这样的共享目录没有任何人可以访问，请至少添加一个访问用户，或勾选“允许匿名访问”。",
                    "请配置访问方式");
                return;
            }

            var shareUsers = Users.Select(u => new ShareUserConfig
            {
                Username = u.Username,
                Password = u.Password,
                AccessLevel = u.AccessLevel
            }).ToList();

            ShareAccessLevel guestAccess = GuestAccessIndex == 1 ? ShareAccessLevel.ReadOnly : ShareAccessLevel.ReadWrite;

            // 没有任何读写途径时标记为只读，以便列表准确展示只读 / 读写标签
            bool hasAnyWriteAccess = (AllowGuest && guestAccess == ShareAccessLevel.ReadWrite) ||
                                     shareUsers.Any(u => u.AccessLevel == ShareAccessLevel.ReadWrite);

            string guestDesc = AllowGuest ? (guestAccess == ShareAccessLevel.ReadWrite ? "匿名读写" : "匿名只读") : "禁止匿名";
            string userDesc = shareUsers.Count > 0 ? $"{shareUsers.Count}个授权用户" : "无独立用户";

            Result = new ShareConfig
            {
                ShareName = name,
                LocalPath = path,
                IsReadOnly = !hasAnyWriteAccess,
                AllowGuest = AllowGuest,
                GuestAccess = guestAccess,
                Users = shareUsers,
                UserPermissions = shareUsers.Select(u => new UserPermissionConfig { Username = u.Username, AccessLevel = u.AccessLevel }).ToList(),
                AllowedUsers = shareUsers.Select(u => u.Username).ToList(),
                EnableSmb1 = EnableSmb1,
                Description = $"{guestDesc} | {userDesc}"
            };

            CloseRequested?.Invoke(true);
        }
    }
}
