using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using UniShare.Core;
using UniShare.Core.Models;

namespace UniShare.Server
{
    internal class Program
    {
        private static SmbServerEngine? s_engine;
        private static bool s_stoppedLanmanServer = false;

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "UniShare (Windows) 独立文件共享服务 [PoC 原型验证]";

            PrintBanner();

            bool isAdministrator = CheckAdministrator();
            if (!isAdministrator)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("【提示】当前未以管理员权限运行。若需接管 TCP 445 端口或停止系统自带共享服务，请右键选择“以管理员身份运行”。\n");
                Console.ResetColor();
            }

            // 1. 加载或初始化配置文件 (config.json)
            string configPath = ConfigManager.GetDefaultConfigPath();
            Console.WriteLine($"正在加载配置文件: {configPath}");
            ServerConfig config;
            try
            {
                config = ConfigManager.Load(configPath);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[配置就绪] 已加载 {config.Shares.Count} 个共享目录，{config.Users.Count} 个虚拟用户。\n");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"加载配置文件异常: {ex.Message}，将使用默认配置。");
                Console.ResetColor();
                config = ConfigManager.CreateDefault();
            }

            int port = config.ListenPort;
            Console.WriteLine($"正在检查 TCP {port} 端口状态...");
            if (port == 445)
            {
                string diagnosis = PortHelper.GetPort445Diagnosis();
                Console.WriteLine($"诊断结果: {diagnosis}");

                if (PortHelper.IsPortInUse(445))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("\n[1] 尝试停止 Windows 自带 LanmanServer 服务以抢占 445 端口 (推荐，摄像头必须)");
                    Console.WriteLine("[2] 切换为自定义测试端口 20445 (仅供 PC/手机 客户端测试，摄像头不支持)");
                    Console.ResetColor();
                    Console.Write("请选择处理方式 (默认 1): ");
                    string? choice = Console.ReadLine()?.Trim();

                    if (choice == "2")
                    {
                        port = 20445;
                        config.ListenPort = port;
                        Console.WriteLine($"已临时切换为自定义端口: {port}");
                    }
                    else
                    {
                        Console.WriteLine("正在尝试停止 Windows SMB 协议栈以独占释放 445 端口...");
                        var (success, msg) = PortHelper.TryStopLanmanServer(s => Console.WriteLine($"  {s}"));
                        if (success)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"\n[成功] {msg}\n");
                            Console.ResetColor();
                            s_stoppedLanmanServer = true;
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"\n[失败] {msg}");
                            Console.WriteLine("接管 445 端口失败，请确认是否以管理员权限运行程序，或输入 2 改用测试端口。");
                            Console.ResetColor();
                            return;
                        }
                    }
                }
            }

            void StartServer()
            {
                s_engine = new SmbServerEngine(config);
                s_engine.LogReceived += OnLogReceived;
                s_engine.Start();
                PrintServiceInfo(config);
            }

            try
            {
                StartServer();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"服务启动失败: {ex.Message}");
                Console.ResetColor();
                RestoreSystemIfNeed();
                return;
            }

            Console.WriteLine("\n[快捷指令] 按 [Q] 停止服务并退出 | 按 [C] 切换模式并持久化 | 按 [R] 热重载 config.json");
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Q)
                {
                    break;
                }
                else if (key.Key == ConsoleKey.C)
                {
                    s_engine?.Stop();
                    config.Profile = config.Profile == ServerProfileType.CameraCompatibility
                        ? ServerProfileType.GeneralLanSharing
                        : ServerProfileType.CameraCompatibility;
                    ConfigManager.Save(config);
                    Console.WriteLine($"\n[模式切换] 正在以 {config.Profile} 模式重启服务 (已持久化存盘)...");
                    StartServer();
                }
                else if (key.Key == ConsoleKey.R)
                {
                    Console.WriteLine("\n[热重载] 正在重新读取 config.json 配置文件...");
                    try
                    {
                        var reloaded = ConfigManager.Load();
                        s_engine?.Stop();
                        config = reloaded;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("[热重载] 配置文件重载成功，正在重启服务...");
                        Console.ResetColor();
                        StartServer();
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[热重载失败] {ex.Message}，保持原配置运行。");
                        Console.ResetColor();
                    }
                }
            }

            s_engine?.Stop();
            RestoreSystemIfNeed();
            Console.WriteLine("程序已正常退出。");
        }

        private static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("====================================================================");
            Console.WriteLine("          UniShare (Windows) 统一文件共享服务 - 原型验证            ");
            Console.WriteLine("   - 零 Windows 依赖 / 纯用户态 / 监控摄像头极限兼容 / 4K流媒体    ");
            Console.WriteLine("====================================================================");
            Console.ResetColor();
        }

        private static void PrintServiceInfo(ServerConfig config)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n========================== UniShare 服务运行信息 ==========================");
            Console.WriteLine($"运行状态: 正在监听");
            Console.WriteLine($"服务模式: {config.Profile} ({(config.Profile == ServerProfileType.CameraCompatibility ? "监控摄像头极限兼容" : "局域网 4K 流媒体高速")})");
            Console.WriteLine($"监听端口: TCP {config.ListenPort}");

            Console.WriteLine("\n【挂载的共享目录】");
            foreach (var share in config.Shares)
            {
                string readMode = share.IsReadOnly ? "只读" : "读写";
                string guestMode = share.AllowGuest ? "允许匿名" : "需密码";
                string userScope = share.AllowedUsers.Count > 0 ? string.Join(", ", share.AllowedUsers) : "全部有效用户";
                Console.WriteLine($"  * [{share.ShareName}] -> {Path.GetFullPath(share.LocalPath)}");
                Console.WriteLine($"    - 权限: [{readMode}] | [{guestMode}] | 授权白名单: [{userScope}]");
                if (!string.IsNullOrWhiteSpace(share.Description))
                {
                    Console.WriteLine($"    - 描述: {share.Description}");
                }
            }

            Console.WriteLine("\n【已注册的虚拟用户】");
            foreach (var user in config.Users)
            {
                string userType = user.IsGuest ? "匿名账号" : "标准账号";
                string pwdDisplay = string.IsNullOrEmpty(user.Password) ? "(免密)" : "(密码已设置)";
                Console.WriteLine($"  * 用户: {user.Username} {pwdDisplay} [{userType}] {(string.IsNullOrEmpty(user.Description) ? "" : "- " + user.Description)}");
            }

            Console.WriteLine("\n【局域网访问地址 (各网卡 UNC 路径)】");
            var ips = GetLocalIPAddresses();
            if (ips.Count == 0)
            {
                ips.Add("127.0.0.1");
            }
            foreach (var ip in ips)
            {
                foreach (var share in config.Shares)
                {
                    Console.WriteLine($"  * \\\\{ip}\\{share.ShareName}");
                }
            }
            Console.WriteLine("==========================================================================\n");
            Console.ResetColor();
        }

        private static void OnLogReceived(object? sender, LogMessageEventArgs e)
        {
            ConsoleColor color = e.Severity switch
            {
                "Warning" => ConsoleColor.Yellow,
                "Error" => ConsoleColor.Red,
                _ => ConsoleColor.Gray
            };

            Console.ForegroundColor = color;
            Console.WriteLine($"[{e.Time:HH:mm:ss.fff}] [{e.Severity}] [{e.Source}] {e.Message}");
            Console.ResetColor();
        }

        private static bool CheckAdministrator()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static void RestoreSystemIfNeed()
        {
            if (s_stoppedLanmanServer)
            {
                Console.Write("\n是否恢复 Windows 自带的 LanmanServer 共享服务? (y/n, 默认 y): ");
                string? input = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(input) || input.Equals("y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("\n正在恢复 Windows 自带 SMB 服务与底层驱动...");
                    var (success, msg) = PortHelper.TryStartLanmanServer(s => Console.WriteLine($"  {s}"));
                    if (success)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[恢复成功] {msg}\n");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[恢复提示] {msg}\n");
                        Console.ResetColor();
                    }
                }
            }
        }

        private static System.Collections.Generic.List<string> GetLocalIPAddresses()
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        list.Add(ip.ToString());
                    }
                }
            }
            catch { }
            return list;
        }
    }
}
