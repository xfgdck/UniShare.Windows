using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace UniShare.UI.Helpers
{
    /// <summary>
    /// 单实例运行检测与唤醒控制辅助类
    /// 确保软件同一时间仅运行一个实例，重复启动时自动将已存在的实例置于前台激活
    /// </summary>
    public static class SingleInstanceHelper
    {
        private const string MutexName = "UniShare_SingleInstance_Mutex";
        private const string EventName = "UniShare_SingleInstance_Activate_Event";
        public const string MessageName = "UniShare_ActivateWindow_Message";

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

        public const uint MSGFLT_ADD = 1;
        private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;

        private static Mutex? s_mutex;
        private static EventWaitHandle? s_event;
        private static Thread? s_waitThread;
        private static volatile bool s_isDisposed;

        public static int WindowMessageId { get; private set; }

        /// <summary>
        /// 尝试获取单实例互斥体。
        /// 若返回 true，说明本进程是唯一的首个主实例；
        /// 若返回 false，说明已有实例在运行，本方法已自动通知原实例置于前台。
        /// </summary>
        public static bool AcquireOrNotifyExisting()
        {
            WindowMessageId = RegisterWindowMessage(MessageName);

            bool createdNew;
            try
            {
                s_mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (AbandonedMutexException)
            {
                // 前一个实例非正常退出时释放了所有权，本实例自动接管
                createdNew = true;
            }

            if (!createdNew)
            {
                // 已有实例在运行：将权限转交并通知其恢复前台
                NotifyExistingInstance();
                return false;
            }

            // 本实例是首个实例：启动后台监听线程
            try
            {
                s_event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                s_waitThread = new Thread(ListenForActivation)
                {
                    IsBackground = true,
                    Name = "SingleInstanceActivationListener"
                };
                s_waitThread.Start();
            }
            catch { }

            return true;
        }

        private static void NotifyExistingInstance()
        {
            try
            {
                // 1. 查找已运行的同名进程并允许其设置前台窗口
                var current = Process.GetCurrentProcess();
                var existingProcesses = Process.GetProcessesByName(current.ProcessName)
                    .Where(p => p.Id != current.Id);

                foreach (var p in existingProcesses)
                {
                    try
                    {
                        AllowSetForegroundWindow((uint)p.Id);
                    }
                    catch { }
                }

                // 2. 触发命名事件激活已有实例
                if (EventWaitHandle.TryOpenExisting(EventName, out var waitHandle))
                {
                    waitHandle.Set();
                    waitHandle.Dispose();
                }

                // 3. 同时广播 Win32 消息双重保障
                if (WindowMessageId != 0)
                {
                    PostMessage(HWND_BROADCAST, (uint)WindowMessageId, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }
        }

        private static void ListenForActivation()
        {
            while (!s_isDisposed && s_event != null)
            {
                try
                {
                    if (s_event.WaitOne())
                    {
                        if (s_isDisposed) break;

                        Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        {
                            if (Application.Current?.MainWindow is MainWindow mw)
                            {
                                mw.RestoreAndActivate();
                            }
                        }));
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 应用程序退出时清理互斥体与事件句柄
        /// </summary>
        public static void Cleanup()
        {
            s_isDisposed = true;
            try
            {
                s_event?.Set();
                s_event?.Dispose();
                s_event = null;
            }
            catch { }

            try
            {
                s_mutex?.ReleaseMutex();
                s_mutex?.Dispose();
                s_mutex = null;
            }
            catch { }
        }
    }
}
