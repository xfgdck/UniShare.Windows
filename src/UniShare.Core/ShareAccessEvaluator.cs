using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniShare.Core.Models;

namespace UniShare.Core
{
    /// <summary>
    /// 共享目录访问权限判定与虚拟用户凭据工具（纯逻辑，不依赖 SMB 运行时，便于单元测试）
    /// </summary>
    public static class ShareAccessEvaluator
    {
        /// <summary>
        /// 剥离 DOMAIN\user 与 user@domain 形式，得到裸用户名
        /// </summary>
        public static string GetBareUserName(string? userName)
        {
            string bareName = userName ?? string.Empty;
            if (bareName.Contains('\\'))
            {
                bareName = bareName.Substring(bareName.LastIndexOf('\\') + 1);
            }
            else if (bareName.Contains('@'))
            {
                bareName = bareName.Substring(0, bareName.IndexOf('@'));
            }
            return bareName;
        }

        /// <summary>
        /// 是否为匿名 / 来宾身份（SMBLibrary 对 guest 回退会话统一使用 "Guest" 作为用户名）
        /// </summary>
        public static bool IsGuestIdentity(string? userName)
        {
            return string.IsNullOrEmpty(userName) ||
                   string.Equals(userName, "guest", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(GetBareUserName(userName), "guest", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsWriteRequest(FileAccess requestedAccess)
        {
            return (requestedAccess & FileAccess.Write) != 0;
        }

        /// <summary>
        /// 是否配置了任意访问用户（含旧版数据格式）
        /// </summary>
        public static bool HasConfiguredUsers(ShareConfig share)
        {
            return (share.Users != null && share.Users.Count > 0) ||
                   (share.UserPermissions != null && share.UserPermissions.Count > 0) ||
                   (share.AllowedUsers != null && share.AllowedUsers.Count > 0);
        }

        /// <summary>
        /// 判定指定用户对共享的读 / 写请求是否放行
        /// </summary>
        /// <param name="denyReason">拒绝时的中文原因说明（放行时为空）</param>
        public static bool Evaluate(ShareConfig share, string? userName, FileAccess requestedAccess, out string denyReason)
        {
            denyReason = string.Empty;
            string name = userName ?? string.Empty;
            string bareName = GetBareUserName(name);
            bool isWrite = IsWriteRequest(requestedAccess);

            if (IsGuestIdentity(name))
            {
                return EvaluateGuest(share, isWrite, "匿名用户", out denyReason);
            }

            bool Matches(string candidate) =>
                string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, bareName, StringComparison.OrdinalIgnoreCase);

            if (share.Users != null && share.Users.Count > 0)
            {
                var shareUser = share.Users.FirstOrDefault(u => Matches(u.Username));
                if (shareUser != null)
                {
                    return EvaluateLevel(shareUser.AccessLevel, isWrite, $"用户 [{name}] 在共享 [{share.ShareName}] 上的权限为只读", out denyReason);
                }
            }

            if (share.UserPermissions != null && share.UserPermissions.Count > 0)
            {
                var userPerm = share.UserPermissions.FirstOrDefault(p => Matches(p.Username));
                if (userPerm != null)
                {
                    return EvaluateLevel(userPerm.AccessLevel, isWrite, $"用户 [{name}] 在共享 [{share.ShareName}] 上的权限为只读", out denyReason);
                }
            }

            if (share.AllowedUsers != null && share.AllowedUsers.Any(Matches))
            {
                return EvaluateLevel(share.IsReadOnly ? ShareAccessLevel.ReadOnly : ShareAccessLevel.ReadWrite, isWrite,
                    $"共享 [{share.ShareName}] 设置为只读 (用户: {name})", out denyReason);
            }

            if (share.AllowGuest)
            {
                // 已认证但不属于本共享的用户，按本共享的匿名权限处理
                return EvaluateGuest(share, isWrite, $"未授权用户 [{name}] 按匿名身份", out denyReason);
            }

            denyReason = $"用户 [{name}] 未被授权访问共享 [{share.ShareName}]";
            return false;
        }

        private static bool EvaluateGuest(ShareConfig share, bool isWrite, string who, out string denyReason)
        {
            if (!share.AllowGuest)
            {
                denyReason = $"{who}访问被拒绝：共享 [{share.ShareName}] 未开放匿名访问";
                return false;
            }
            return EvaluateLevel(share.GuestAccess, isWrite, $"{who}写入被拒绝：共享 [{share.ShareName}] 的匿名权限为只读", out denyReason);
        }

        private static bool EvaluateLevel(ShareAccessLevel level, bool isWrite, string readOnlyReason, out string denyReason)
        {
            if (isWrite && level == ShareAccessLevel.ReadOnly)
            {
                denyReason = readOnlyReason;
                return false;
            }
            denyReason = string.Empty;
            return true;
        }

        /// <summary>
        /// SMB/NTLM 认证发生在连接具体共享之前，因此同一用户名在所有共享中只能对应一个密码。
        /// 查找与给定用户名同名但密码不同的其他共享（未找到返回 null）。
        /// </summary>
        public static ShareConfig? FindPasswordConflict(IEnumerable<ShareConfig> shares, ShareConfig? excludeShare, string userName, string password)
        {
            foreach (var share in shares)
            {
                if (ReferenceEquals(share, excludeShare) || share.Users == null) continue;
                foreach (var u in share.Users)
                {
                    if (string.Equals(u.Username, userName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(u.Password ?? string.Empty, password ?? string.Empty, StringComparison.Ordinal))
                    {
                        return share;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 构建全局用户名 → 密码映射。存在同名不同密码冲突时保留首个并通过 conflicts 返回冲突用户名。
        /// </summary>
        public static Dictionary<string, string> BuildUserPasswordMap(ServerConfig config, out List<string> conflicts)
        {
            var userMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var conflictSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 记录首次出现的用户名写法，冲突提示统一使用该写法
            var canonicalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void AddUser(string? username, string? password)
            {
                if (string.IsNullOrWhiteSpace(username)) return;
                string pwd = password ?? string.Empty;
                if (userMap.TryGetValue(username, out string? existing))
                {
                    if (!string.Equals(existing, pwd, StringComparison.Ordinal))
                    {
                        conflictSet.Add(canonicalNames[username]);
                    }
                    return;
                }
                userMap[username] = pwd;
                canonicalNames[username] = username;
            }

            foreach (var share in config.Shares)
            {
                if (share.Users == null) continue;
                foreach (var u in share.Users)
                {
                    AddUser(u.Username, u.Password);
                }
            }

            if (config.Users != null)
            {
                foreach (var u in config.Users)
                {
                    // 旧版全局用户列表仅作补充，不参与冲突判定
                    if (!string.IsNullOrWhiteSpace(u.Username) && !userMap.ContainsKey(u.Username))
                    {
                        userMap[u.Username] = u.Password ?? string.Empty;
                    }
                }
            }

            conflicts = conflictSet.ToList();
            return userMap;
        }

        /// <summary>
        /// 根据各共享的 SMB1 开关计算是否需要全局开启 SMB 1.0
        /// </summary>
        public static bool IsSmb1Required(ServerConfig config)
        {
            return config.Shares.Count > 0 ? config.Shares.Any(s => s.EnableSmb1) : config.EnableSmb1;
        }
    }
}
