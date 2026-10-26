using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace UniShare.UI.Helpers
{
    /// <summary>
    /// Windows 开机自启动管理工具类。
    /// 由于 UniShare 需要管理员权限 (requireAdministrator) 接管 TCP 445 端口，
    /// 普通注册表 Run 键会被 Windows UAC 静默拦截，因此采用 Windows 计划任务 (HighestAvailable) 实现免 UAC 弹窗开机自启。
    /// </summary>
    public static class AutoStartHelper
    {
        private const string TaskName = "UniShare_AutoStart";

        /// <summary>
        /// 查询当前是否已在系统中注册开机自启动计划任务
        /// </summary>
        public static bool IsAutoStartEnabled()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Query /TN \"{TaskName}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(3000);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 启用或禁用开机自启动
        /// </summary>
        /// <param name="enable">是否启用开机自启</param>
        /// <param name="startMinimized">开机自启时是否静默最小化到系统托盘</param>
        /// <returns>(是否成功, 提示信息)</returns>
        public static (bool Success, string Message) SetAutoStart(bool enable, bool startMinimized = true)
        {
            if (!enable)
            {
                return DisableAutoStart();
            }

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return (false, "无法获取当前程序可执行文件路径。");
            }

            string workDir = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            string arguments = startMinimized ? "--minimized" : string.Empty;

            // 优先使用 XML 方式创建计划任务，以解除“电池供电不启动”和“72小时超时停止”的默认限制
            string tempXmlPath = Path.Combine(Path.GetTempPath(), $"UniShare_AutoStart_{Guid.NewGuid():N}.xml");
            try
            {
                string xmlContent = BuildTaskXml(exePath, arguments, workDir);
                File.WriteAllText(tempXmlPath, xmlContent, Encoding.Unicode);

                var (ok, output) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tempXmlPath}\" /F");
                if (ok)
                {
                    return (true, "已成功开启开机自启动。");
                }

                // 若 XML 导入在特殊精简系统下失败，回退为标准命令行参数创建
                string trValue = string.IsNullOrEmpty(arguments)
                    ? $"\\\"{exePath}\\\""
                    : $"\\\"{exePath}\\\" {arguments}";

                // /RU + /IT：限定为当前用户登录时以交互令牌运行（无需保存密码），避免任意用户登录都触发
                string currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                var (fallbackOk, fallbackOut) = RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"{trValue}\" /SC ONLOGON /RU \"{currentUser}\" /IT /RL HIGHEST /F");
                if (fallbackOk)
                {
                    return (true, "已成功开启开机自启动。");
                }

                return (false, $"设置开机自启动失败: {fallbackOut}");
            }
            catch (Exception ex)
            {
                return (false, $"设置开机自启动异常: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempXmlPath))
                    {
                        File.Delete(tempXmlPath);
                    }
                }
                catch { }
            }
        }

        private static (bool Success, string Message) DisableAutoStart()
        {
            try
            {
                if (!IsAutoStartEnabled())
                {
                    return (true, "开机自启动已关闭。");
                }

                var (ok, output) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                if (ok || !IsAutoStartEnabled())
                {
                    return (true, "已关闭开机自启动。");
                }

                return (false, $"关闭开机自启动失败: {output}");
            }
            catch (Exception ex)
            {
                return (false, $"关闭开机自启动异常: {ex.Message}");
            }
        }

        private static (bool Success, string Output) RunSchtasks(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return (false, "无法启动 schtasks.exe");
            }

            string stdOut = proc.StandardOutput.ReadToEnd();
            string stdErr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            string combined = string.IsNullOrWhiteSpace(stdErr) ? stdOut.Trim() : $"{stdOut} {stdErr}".Trim();
            return (proc.ExitCode == 0, combined);
        }

        private static string BuildTaskXml(string exePath, string arguments, string workingDirectory)
        {
            string escapedExe = SecurityElement.Escape(exePath) ?? exePath;
            string escapedArgs = SecurityElement.Escape(arguments) ?? string.Empty;
            string escapedWorkDir = SecurityElement.Escape(workingDirectory) ?? workingDirectory;
            // 限定为当前用户：未指定 UserId 的登录触发器会在任意用户登录时触发
            string userId = SecurityElement.Escape(System.Security.Principal.WindowsIdentity.GetCurrent().Name) ?? string.Empty;

            string argsNode = string.IsNullOrWhiteSpace(escapedArgs)
                ? string.Empty
                : $"      <Arguments>{escapedArgs}</Arguments>\r\n";

            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>UniShare 文件共享服务开机自动启动任务</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{userId}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{userId}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{escapedExe}</Command>
{argsNode}      <WorkingDirectory>{escapedWorkDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        }
    }
}
