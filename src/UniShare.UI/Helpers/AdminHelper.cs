using System;
using System.Diagnostics;
using System.Security.Principal;

namespace UniShare.UI.Helpers
{
    public static class AdminHelper
    {
        /// <summary>
        /// 检查当前进程是否具备管理员（Administrator）权限
        /// </summary>
        public static bool IsAdministrator()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 以管理员身份重新启动当前程序
        /// </summary>
        public static bool RestartAsAdministrator()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    return false;
                }

                string[] args = Environment.GetCommandLineArgs();
                string arguments = args.Length > 1 ? string.Join(" ", System.Linq.Enumerable.Skip(args, 1)) : string.Empty;

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas" // 请求 UAC 提权
                };

                Process.Start(startInfo);
                System.Windows.Application.Current.Shutdown();
                return true;
            }
            catch (Exception)
            {
                // 用户在 UAC 弹窗中点击了“否”或发生其他异常
                return false;
            }
        }
    }
}
