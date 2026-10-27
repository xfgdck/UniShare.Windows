using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using UniShare.UI.Helpers;
using UniShare.UI.Services;
using UniShare.UI.ViewModels;
using UniShare.UI.Views;
using Wpf.Ui.Controls;

namespace UniShare.UI
{
    public partial class MainWindow : FluentWindow
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private const int SW_RESTORE = 9;

        private static readonly int s_taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        private bool m_isExplicitExit = false;
        private bool m_isInitializedPort = false;

        public ShellViewModel ViewModel => (ShellViewModel)DataContext;

        private ServerHost Host => ViewModel.Host;

        public MainWindow(bool startMinimized = false)
        {
            InitializeComponent();
            DataContext = new ShellViewModel(new DialogService());

            // 预先确保 HWND 句柄创建，并将托盘图标直接与 Win32 消息循环绑定
            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.EnsureHandle();
            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(HwndMessageHook);

            if (source != null)
            {
                MyNotifyIcon.HookWindow = source;
                MyNotifyIcon.ParentHandle = hwnd;
            }

            if (SingleInstanceHelper.WindowMessageId != 0)
            {
                SingleInstanceHelper.ChangeWindowMessageFilter((uint)SingleInstanceHelper.WindowMessageId, SingleInstanceHelper.MSGFLT_ADD);
            }

            EnsureTrayIconRegistered();

            // 托盘左键点击恢复主窗口
#pragma warning disable CS8622
            MyNotifyIcon.LeftClick += (s, e) => RestoreAndActivate();
#pragma warning restore CS8622

            Loaded += async (s, e) =>
            {
                EnsureTrayIconRegistered();

                if (!string.IsNullOrEmpty(Host.StartupWarning))
                {
                    string warning = Host.StartupWarning;
                    Host.StartupWarning = null;
                    MessageDialog.ShowWarning(warning, "配置文件提示", owner: this);
                }

                if (!m_isInitializedPort)
                {
                    m_isInitializedPort = true;
                    await Host.InitializeStartupPortAsync();
                }
            };
        }

        /// <summary>
        /// 静默启动：不弹出任何窗口，直接驻留系统托盘并在后台初始化接管 445 端口与共享服务
        /// </summary>
        public void StartSilent()
        {
            ShowInTaskbar = false;
            Visibility = Visibility.Hidden;

            EnsureTrayIconRegistered();

            if (!m_isInitializedPort)
            {
                m_isInitializedPort = true;
                _ = Host.InitializeStartupPortAsync();
            }
        }

        private void EnsureTrayIconRegistered()
        {
            try
            {
                MyNotifyIcon.Register();
            }
            catch { }
        }

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Windows Explorer 重启或登录时桌面任务栏创建广播，重新向系统托盘注册图标
            if (msg == s_taskbarCreatedMessage && s_taskbarCreatedMessage != 0)
            {
                EnsureTrayIconRegistered();
            }
            // 收到其他新实例尝试重复启动时的广播唤醒消息
            else if (msg == SingleInstanceHelper.WindowMessageId && SingleInstanceHelper.WindowMessageId != 0)
            {
                RestoreAndActivate();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!m_isExplicitExit)
            {
                // 点击右上角关闭默认最小化到系统托盘，后台持续服务
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
            }
        }

        private void TrayOpen_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.NavigateToShares();
            RestoreAndActivate();
        }

        private void TrayExit_Click(object sender, RoutedEventArgs e)
        {
            // 服务启停 / 端口接管进行中时退出，可能在系统服务还原的同时仍有新引擎绑定 445 端口
            if (Host.IsBusy)
            {
                MessageDialog.ShowInfo($"{Host.BusyMessage}，请稍候完成后再退出程序。", "请稍候");
                return;
            }

            bool confirmed = MessageDialog.ShowConfirm(
                "确定要退出 UniShare 吗？\n退出后局域网设备将无法访问共享。",
                "退出确认",
                confirmText: "退出程序",
                cancelText: "取消",
                isDanger: true);

            if (confirmed)
            {
                m_isExplicitExit = true;
                try
                {
                    MyNotifyIcon.Unregister();
                }
                catch { }
                Host.ShutdownAndRestore();
                Application.Current.Shutdown();
            }
        }

        /// <summary>
        /// 将主窗口从后台/托盘/最小化状态平滑恢复并强力置于前台激活
        /// </summary>
        public void RestoreAndActivate()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RestoreAndActivate));
                return;
            }

            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;

            // 绕过 Windows 10/11 焦点抢占保护机制，将窗口置于最前台并聚焦
            IntPtr foreHwnd = GetForegroundWindow();
            uint foreThread = GetWindowThreadProcessId(foreHwnd, IntPtr.Zero);
            uint appThread = GetCurrentThreadId();

            if (foreThread != appThread && foreThread != 0)
            {
                AttachThreadInput(foreThread, appThread, true);
                BringWindowToTop(hwnd);
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                AttachThreadInput(foreThread, appThread, false);
            }
            else
            {
                BringWindowToTop(hwnd);
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }

            Activate();
            Focus();
        }
    }
}