using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Text.Json;
using UniShare.Core.Models;

namespace UniShare.Core
{
    /// <summary>
    /// UniShare 服务配置管理器，负责 config.json 的持久化读写与默认配置生成。
    /// 配置统一存放于 %ProgramData%\UniShare（目录 ACL 仅允许 SYSTEM 与 Administrators 访问），
    /// 密码使用 DPAPI (LocalMachine) 加密后落盘。
    /// </summary>
    public static class ConfigManager
    {
        public const string DefaultConfigFileName = "config.json";

        /// <summary>
        /// 最近一次加载时是否从旧版本（安装目录）迁移了配置，用于识别升级场景
        /// </summary>
        public static bool MigratedFromLegacy { get; private set; }

        /// <summary>
        /// 最近一次加载产生的非致命警告（如密码无法解密），无警告时为 null
        /// </summary>
        public static string? LastLoadWarning { get; private set; }

        private static readonly object s_saveLock = new object();

        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// 获取程序数据目录 (%ProgramData%\UniShare)
        /// </summary>
        public static string GetDataDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniShare");
        }

        /// <summary>
        /// 获取程序默认配置文件完整物理路径
        /// </summary>
        public static string GetDefaultConfigPath()
        {
            return Path.Combine(GetDataDirectory(), DefaultConfigFileName);
        }

        /// <summary>
        /// 旧版本存放于程序安装目录的配置文件路径（用于自动迁移）
        /// </summary>
        public static string GetLegacyConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultConfigFileName);
        }

        /// <summary>
        /// 从指定路径或默认路径加载配置；若文件不存在则自动生成默认配置（默认路径下会先尝试迁移旧版配置）。
        /// 配置文件损坏时抛出 InvalidOperationException。
        /// </summary>
        public static ServerConfig Load(string? configPath = null)
        {
            string path = configPath ?? GetDefaultConfigPath();
            if (IsDefaultLocation(path))
            {
                MigrateLegacyConfig(path);
            }

            if (!File.Exists(path))
            {
                ServerConfig defaultConfig = CreateDefault();
                Save(defaultConfig, path);
                return defaultConfig;
            }

            ServerConfig? config;
            try
            {
                string json = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<ServerConfig>(json, s_jsonOptions);
                if (config == null)
                {
                    throw new InvalidDataException("配置文件内容为空或格式无效。");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"读取配置文件 [{path}] 失败: {ex.Message}", ex);
            }

            Normalize(config);
            bool hasPlaintext = DecryptPasswords(config, out List<string> undecryptableUsers);
            LastLoadWarning = undecryptableUsers.Count == 0
                ? null
                : $"以下用户的密码无法解密（配置文件可能复制自其他电脑），已置为空，请在共享设置中删除后重新添加：\n{string.Join("、", undecryptableUsers)}";
            if (hasPlaintext)
            {
                // 存在旧版明文密码，立即以加密形式重新保存
                try { Save(config, path); } catch { }
            }
            return config;
        }

        /// <summary>
        /// 加载配置；文件损坏时自动备份损坏文件并以默认配置继续运行，通过 recoveryMessage 返回提示信息
        /// </summary>
        public static ServerConfig LoadOrRecover(string? configPath, out string? recoveryMessage)
        {
            recoveryMessage = null;
            string path = configPath ?? GetDefaultConfigPath();
            try
            {
                ServerConfig config = Load(configPath);
                recoveryMessage = LastLoadWarning;
                return config;
            }
            catch (Exception ex)
            {
                string backupPath = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                try
                {
                    if (File.Exists(path))
                    {
                        File.Move(path, backupPath, overwrite: true);
                    }
                }
                catch
                {
                    backupPath = string.Empty;
                }

                ServerConfig defaultConfig = CreateDefault();
                try { Save(defaultConfig, path); } catch { }

                recoveryMessage = string.IsNullOrEmpty(backupPath)
                    ? $"配置文件读取失败，已使用默认配置启动。\n原因：{ex.Message}"
                    : $"配置文件读取失败，已使用默认配置启动，原配置已备份至：\n{backupPath}\n原因：{ex.Message}";
                return defaultConfig;
            }
        }

        /// <summary>
        /// 将配置持久化保存到 JSON 文件（密码加密后写入，先写临时文件再原子替换，避免写入中断导致配置损坏）
        /// </summary>
        public static void Save(ServerConfig config, string? configPath = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            string path = configPath ?? GetDefaultConfigPath();

            lock (s_saveLock)
            {
                string dir = Path.GetDirectoryName(path) ?? string.Empty;
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                if (IsDefaultLocation(path))
                {
                    EnsureSecureDirectory(dir);
                }

                ServerConfig snapshot = Clone(config);
                EncryptPasswords(snapshot);

                string json = JsonSerializer.Serialize(snapshot, s_jsonOptions);
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json, System.Text.Encoding.UTF8);
                File.Move(tempPath, path, overwrite: true);
            }
        }

        /// <summary>
        /// 深拷贝配置（用于把配置快照交给后台服务引擎，避免与 UI 线程的修改并发冲突）
        /// </summary>
        public static ServerConfig Clone(ServerConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            string json = JsonSerializer.Serialize(config, s_jsonOptions);
            return JsonSerializer.Deserialize<ServerConfig>(json, s_jsonOptions) ?? CreateDefault();
        }

        /// <summary>
        /// 生成开箱即用的默认配置
        /// </summary>
        public static ServerConfig CreateDefault()
        {
            return new ServerConfig
            {
                ListenPort = 445,
                Profile = ServerProfileType.CameraCompatibility,
                EnableSmb1 = false,
                EnableVerboseLog = true,
                AutoRestoreSystemServices = true,
                IsServiceEnabled = true,
                EnableNetworkDiscovery = true,
                Shares = new List<ShareConfig>(),
                Users = new List<VirtualUser>()
            };
        }

        private static bool IsDefaultLocation(string path)
        {
            try
            {
                return string.Equals(Path.GetFullPath(path), Path.GetFullPath(GetDefaultConfigPath()), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void Normalize(ServerConfig config)
        {
            config.Shares ??= new List<ShareConfig>();
            config.Users ??= new List<VirtualUser>();
            foreach (var share in config.Shares)
            {
                share.Users ??= new List<ShareUserConfig>();
                share.UserPermissions ??= new List<UserPermissionConfig>();
                share.AllowedUsers ??= new List<string>();
            }
        }

        private static void MigrateLegacyConfig(string newPath)
        {
            try
            {
                string legacyPath = GetLegacyConfigPath();
                if (File.Exists(newPath) || !File.Exists(legacyPath) ||
                    string.Equals(Path.GetFullPath(legacyPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string dir = Path.GetDirectoryName(newPath) ?? string.Empty;
                Directory.CreateDirectory(dir);
                EnsureSecureDirectory(dir);
                File.Copy(legacyPath, newPath);
                MigratedFromLegacy = true;
                // 删除安装目录下含明文密码的旧配置
                File.Delete(legacyPath);
            }
            catch { }
        }

        /// <summary>
        /// 收紧数据目录 ACL：仅 SYSTEM 与 Administrators 完全控制，禁止普通用户读取配置（需管理员权限，失败时静默忽略）
        /// </summary>
        private static void EnsureSecureDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;

            // 普通用户可在 %ProgramData% 下预先创建同名目录或联接点（目录抢占），此时拒绝写入敏感配置
            var info = new DirectoryInfo(dir);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"配置目录 [{dir}] 是符号链接或联接点，出于安全考虑拒绝写入。");
            }

            try
            {
                var security = new DirectorySecurity();
                // 所有者改为 Administrators，避免抢先创建目录的普通用户凭所有者身份重新修改 ACL
                security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(security);
            }
            catch { }
        }

        private static void EncryptPasswords(ServerConfig config)
        {
            foreach (var share in config.Shares)
            {
                if (share.Users == null) continue;
                foreach (var u in share.Users)
                {
                    u.PasswordProtected = string.IsNullOrEmpty(u.Password) ? null : DpapiHelper.Protect(u.Password);
                    u.Password = null!;
                }
            }
            if (config.Users != null)
            {
                foreach (var u in config.Users)
                {
                    u.PasswordProtected = string.IsNullOrEmpty(u.Password) ? null : DpapiHelper.Protect(u.Password);
                    u.Password = null!;
                }
            }
        }

        /// <summary>
        /// 解密磁盘上的密码；返回 true 表示发现了旧版明文密码，需要重新加密保存
        /// </summary>
        private static bool DecryptPasswords(ServerConfig config, out List<string> undecryptableUsers)
        {
            bool hasPlaintext = false;
            var failed = new List<string>();

            string Resolve(string userName, string? plain, string? protectedValue)
            {
                if (!string.IsNullOrEmpty(protectedValue))
                {
                    if (DpapiHelper.TryUnprotect(protectedValue, out string value))
                    {
                        return value;
                    }
                    if (!failed.Contains(userName, StringComparer.OrdinalIgnoreCase))
                    {
                        failed.Add(userName);
                    }
                    return string.Empty;
                }
                if (!string.IsNullOrEmpty(plain))
                {
                    hasPlaintext = true;
                }
                return plain ?? string.Empty;
            }

            foreach (var share in config.Shares)
            {
                foreach (var u in share.Users)
                {
                    u.Password = Resolve(u.Username, u.Password, u.PasswordProtected);
                    u.PasswordProtected = null;
                }
            }
            foreach (var u in config.Users)
            {
                u.Password = Resolve(u.Username, u.Password, u.PasswordProtected);
                u.PasswordProtected = null;
            }

            undecryptableUsers = failed;
            return hasPlaintext;
        }
    }
}
