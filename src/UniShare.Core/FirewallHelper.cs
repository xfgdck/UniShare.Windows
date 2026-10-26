using System;
using System.Diagnostics;

namespace UniShare.Core
{
    /// <summary>
    /// Windows 防火墙入站规则管理。
    /// 系统自带的“文件和打印机共享”规则只放行 System 进程，用户态 SMB 服务需要为 UniShare.exe 单独放行；
    /// WS-Discovery 元数据端点由 http.sys (System 进程) 承载，需额外按端口放行。
    /// 规则对所有网络类型（域 / 专用 / 公用）生效：Windows 连接新网络时默认标记为“公用网络”，
    /// 若区分类型，家庭网络常被静默拦截，因此不做区分。
    /// </summary>
    public static class FirewallHelper
    {
        public const string ProgramRuleName = "UniShare";
        public const string WsdHttpRuleName = "UniShare WSD Metadata";
        public const string WsdHttpPorts = "5357,5358,5359,8765";

        /// <summary>
        /// 确保入站放行规则存在、指向当前程序路径（不存在或路径不一致时重建），并作用于所有网络类型
        /// </summary>
        public static (bool Success, string Message) EnsureRules(string exePath)
        {
            try
            {
                var (showCode, showOutput) = RunNetsh($"advfirewall firewall show rule name=\"{ProgramRuleName}\" verbose");
                bool programRuleOk = showCode == 0 && showOutput.Contains(exePath, StringComparison.OrdinalIgnoreCase);
                if (!programRuleOk)
                {
                    RunNetsh($"advfirewall firewall delete rule name=\"{ProgramRuleName}\"");
                    var (code, output) = RunNetsh($"advfirewall firewall add rule name=\"{ProgramRuleName}\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any");
                    if (code != 0)
                    {
                        return (false, $"添加防火墙程序放行规则失败: {output.Trim()}");
                    }
                }

                var (wsdCode, _) = RunNetsh($"advfirewall firewall show rule name=\"{WsdHttpRuleName}\"");
                if (wsdCode != 0)
                {
                    var (code, output) = RunNetsh($"advfirewall firewall add rule name=\"{WsdHttpRuleName}\" dir=in action=allow protocol=TCP localport={WsdHttpPorts} enable=yes profile=any");
                    if (code != 0)
                    {
                        return (false, $"添加 WS-Discovery 元数据端口放行规则失败: {output.Trim()}");
                    }
                }

                // 旧版本创建的规则仅作用于专用 / 域网络，统一更新为所有网络类型
                RunNetsh($"advfirewall firewall set rule name=\"{ProgramRuleName}\" new profile=any");
                RunNetsh($"advfirewall firewall set rule name=\"{WsdHttpRuleName}\" new profile=any");

                return (true, programRuleOk ? "防火墙入站放行规则已就绪（所有网络类型）。" : "已添加防火墙入站放行规则（所有网络类型）。");
            }
            catch (Exception ex)
            {
                return (false, $"配置防火墙规则异常: {ex.Message}");
            }
        }

        private static (int ExitCode, string Output) RunNetsh(string arguments)
        {
            using Process p = new Process();
            p.StartInfo.FileName = "netsh.exe";
            p.StartInfo.Arguments = arguments;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            var stderrTask = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000))
            {
                try { p.Kill(); } catch { }
                return (-1, output);
            }
            stderrTask.Wait(1000);
            return (p.ExitCode, output + stderrTask.Result);
        }
    }
}
