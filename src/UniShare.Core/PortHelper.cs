using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Threading;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UniShare.Core
{
    public class PortHelper
    {
        public const int DefaultSmbPort = 445;
        public const int NetBiosPort = 139;

        /// <summary>
        /// 检查指定端口是否能被成功绑定（最严格真实的独占检测）
        /// </summary>
        public static bool CanBindPort(int port)
        {
            try
            {
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.LingerState = new LingerOption(true, 0);
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检查指定端口是否已被占用（结合系统监听列表与真实绑定探测）
        /// </summary>
        public static bool IsPortInUse(int port)
        {
            try
            {
                IPGlobalProperties ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
                IPEndPoint[] tcpListeners = ipGlobalProperties.GetActiveTcpListeners();
                foreach (IPEndPoint ep in tcpListeners)
                {
                    if (ep.Port == port)
                    {
                        return true;
                    }
                }
            }
            catch { }

            return !CanBindPort(port);
        }

        /// <summary>
        /// 获取服务的运行状态码：
        /// 1: STOPPED, 2: START_PENDING, 3: STOP_PENDING, 4: RUNNING, 
        /// 5: CONTINUE_PENDING, 6: PAUSE_PENDING, 7: PAUSED, -1: 未知或未安装
        /// </summary>
        public static int GetServiceStatusCode(string serviceName)
        {
            var (_, output) = RunScCommand($"query {serviceName}");
            var match = Regex.Match(output, @"STATE\s*:\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int state))
            {
                return state;
            }
            if (output.Contains("1060", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            {
                return 1; // STOPPED / 不存在
            }
            return -1;
        }

        /// <summary>
        /// 检查系统原生 SMB 驱动链 (LanmanServer, srv2, srvnet) 是否处于活动状态
        /// （非 STOPPED，包括 RUNNING、START_PENDING、STOP_PENDING 等）
        /// </summary>
        public static bool IsSystemSmbActive()
        {
            int sLanman = GetServiceStatusCode("LanmanServer");
            int sSrv2 = GetServiceStatusCode("srv2");
            int sSrvnet = GetServiceStatusCode("srvnet");

            // 状态码 > 1 表示处于运行中或过渡状态 (START_PENDING=2, STOP_PENDING=3, RUNNING=4 等)
            return (sLanman > 1) || (sSrv2 > 1) || (sSrvnet > 1);
        }

        /// <summary>
        /// 检查 Windows 原生 SMB 驱动链 (LanmanServer, srv2, srvnet) 是否正在运行
        /// </summary>
        public static bool IsSystemSmbRunning()
        {
            return IsSystemSmbActive();
        }

        /// <summary>
        /// 获取当前占用 445 端口的详细诊断说明
        /// </summary>
        public static string GetPort445Diagnosis()
        {
            if (CanBindPort(DefaultSmbPort))
            {
                return "端口 445 空闲，可供本程序直接绑定。";
            }

            var statusLanman = QueryServiceState("LanmanServer");
            var statusSrv2 = QueryServiceState("srv2");
            var statusSrvnet = QueryServiceState("srvnet");

            List<string> activeDrivers = new List<string>();
            if (statusLanman.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) || statusLanman.Contains("PENDING", StringComparison.OrdinalIgnoreCase))
                activeDrivers.Add("LanmanServer (文件与打印机共享)");
            if (statusSrv2.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) || statusSrv2.Contains("PENDING", StringComparison.OrdinalIgnoreCase))
                activeDrivers.Add("srv2 (SMB2内核驱动)");
            if (statusSrvnet.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) || statusSrvnet.Contains("PENDING", StringComparison.OrdinalIgnoreCase))
                activeDrivers.Add("srvnet (SMB网络核心驱动)");

            if (activeDrivers.Count > 0)
            {
                return $"端口 445 当前被 Windows 系统自带 SMB 协议栈占用: [{string.Join(", ", activeDrivers)}]。";
            }
            else
            {
                return "端口 445 已被占用（可能是第三方共享软件、安全工具或特定的端口排除规则）。";
            }
        }

        /// <summary>
        /// 安全终止指定系统服务或驱动，若其处于 START_PENDING 状态则先等待就绪后再发送停止指令
        /// </summary>
        private static bool StopSingleServiceOrDriver(string name, Action<string>? statusCallback, int maxWaitSeconds = 6)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);

            // 1. 若处于 START_PENDING，等待其完成启动或退出过渡状态
            while (DateTime.UtcNow < deadline)
            {
                int state = GetServiceStatusCode(name);
                if (state == 1) // 已是 STOPPED
                {
                    return true;
                }
                if (state != 2) // 不再是 START_PENDING
                {
                    break;
                }
                statusCallback?.Invoke($"[{name}] 正处于启动过渡态(START_PENDING)，等待其就绪以便安全终止...");
                Thread.Sleep(150);
            }

            // 2. 发送 stop 指令
            int currentState = GetServiceStatusCode(name);
            if (currentState == 1)
            {
                return true;
            }

            if (currentState != 3) // 如果不是正在停止中(STOP_PENDING)，发送停止命令
            {
                RunScCommand($"stop {name}");
            }

            // 3. 等待变为 STOPPED (1)
            while (DateTime.UtcNow < deadline)
            {
                int state = GetServiceStatusCode(name);
                if (state == 1)
                {
                    return true;
                }

                // 若之前因为依赖问题未停止成功，再次尝试发出 stop
                if (state == 4) // RUNNING
                {
                    RunScCommand($"stop {name}");
                }

                Thread.Sleep(100);
            }

            return GetServiceStatusCode(name) == 1;
        }

        /// <summary>
        /// 完整接管 445 端口：按顺序停用 LanmanServer -> srv2 -> srvnet，
        /// 自动处理短时间内快速退出重启导致的 START_PENDING / STOP_PENDING 过渡态
        /// </summary>
        public static (bool Success, string Message) TryStopLanmanServer(Action<string>? statusCallback = null)
        {
            try
            {
                statusCallback?.Invoke("正在解除 Windows 系统自带 SMB 驱动占用 (LanmanServer -> srv2 -> srvnet)...");

                // 1. 首先禁用 LanmanServer 自启，防止系统或网络事件反复自动拉起
                RunScCommand("config LanmanServer start= disabled");

                // 2. 按依赖关系自顶向下依次安全停止：
                // LanmanServer (应用层服务) -> srv2 (SMB2内核驱动) -> srvnet (网络核心驱动，绑定445)
                StopSingleServiceOrDriver("LanmanServer", statusCallback, maxWaitSeconds: 5);
                StopSingleServiceOrDriver("srv2", statusCallback, maxWaitSeconds: 4);
                StopSingleServiceOrDriver("srvnet", statusCallback, maxWaitSeconds: 4);

                // 3. 高频轮询 445 端口是否可成功绑定
                for (int i = 0; i < 40; i++)
                {
                    if (CanBindPort(DefaultSmbPort))
                    {
                        return (true, "成功接管 445 端口，Windows 系统原生 SMB 驱动已释放！");
                    }
                    Thread.Sleep(50);
                }

                // 兜底再次停止一次驱动并等待
                RunScCommand("stop srvnet");
                for (int i = 0; i < 20; i++)
                {
                    if (CanBindPort(DefaultSmbPort))
                    {
                        return (true, "成功接管 445 端口，Windows 系统原生 SMB 驱动已释放！");
                    }
                    Thread.Sleep(50);
                }

                return (false, "系统 SMB 驱动已发出停止指令，但端口 445 仍未能释放（请确保以管理员身份运行本程序）。");
            }
            catch (Exception ex)
            {
                return (false, $"接管 445 端口过程中发生异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 完整恢复 Windows 系统自带的 SMB 共享服务与底层驱动栈（异步恢复，不阻塞退出）
        /// </summary>
        public static (bool Success, string Message) TryStartLanmanServer(Action<string>? statusCallback = null)
        {
            try
            {
                statusCallback?.Invoke("正在恢复 Windows 系统自带 SMB 共享服务与底层网络驱动...");
                RunScCommand("config LanmanServer start= auto");
                RunScCommand("start srvnet");
                RunScCommand("start srv2");
                RunScCommand("start LanmanServer");
                return (true, "已发出恢复指令，Windows 系统自带共享驱动将在后台自动恢复运行。");
            }
            catch (Exception ex)
            {
                return (false, $"恢复 Windows 系统共享服务失败: {ex.Message}");
            }
        }

        private static (int ExitCode, string Output) RunScCommand(string arguments)
        {
            try
            {
                using Process p = new Process();
                p.StartInfo.FileName = "sc.exe";
                p.StartInfo.Arguments = arguments;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                return (p.ExitCode, output);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        private static string QueryServiceState(string serviceName)
        {
            var (_, output) = RunScCommand($"query {serviceName}");
            return output;
        }

        private static bool WaitForServiceState(string serviceName, string expectedState, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                string state = QueryServiceState(serviceName);
                if (state.Contains(expectedState, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                Thread.Sleep(100);
            }
            return false;
        }
    }

    /// <summary>
    /// 445 端口独占持有器：在 SMB 服务未启动或已停止期间，持续锁定 445 端口，防止 Windows 系统原生驱动重新夺取
    /// </summary>
    public class PortReservation : IDisposable
    {
        private Socket? m_socket;
        private readonly object m_lock = new object();

        public bool IsHolding
        {
            get
            {
                lock (m_lock)
                {
                    return m_socket != null;
                }
            }
        }

        public bool TryHold(int port = PortHelper.DefaultSmbPort)
        {
            lock (m_lock)
            {
                if (m_socket != null) return true;

                for (int attempt = 0; attempt < 8; attempt++)
                {
                    try
                    {
                        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        socket.LingerState = new LingerOption(true, 0);
                        socket.Bind(new IPEndPoint(IPAddress.Any, port));
                        socket.Listen(10);
                        m_socket = socket;
                        return true;
                    }
                    catch
                    {
                        m_socket = null;
                        if (attempt < 7)
                        {
                            Thread.Sleep(30);
                        }
                    }
                }

                return false;
            }
        }

        public void Release()
        {
            lock (m_lock)
            {
                if (m_socket != null)
                {
                    try
                    {
                        m_socket.LingerState = new LingerOption(true, 0);
                        m_socket.Close(0);
                        m_socket.Dispose();
                    }
                    catch { }
                    finally
                    {
                        m_socket = null;
                    }
                }
            }
        }

        public void Dispose()
        {
            Release();
        }
    }
}
