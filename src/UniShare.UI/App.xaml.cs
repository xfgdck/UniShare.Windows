using System.Windows;
using UniShare.UI.Helpers;

namespace UniShare.UI
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. 单实例互斥与唤醒检查：若已有运行中的实例，将其置顶到前台并立即退出当前进程
            if (!SingleInstanceHelper.AcquireOrNotifyExisting())
            {
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (s, args) =>
            {
                // 拦截剪贴板临时锁定等非严重 COM 异常，防止进程崩溃
                if (args.Exception is System.Runtime.InteropServices.COMException comEx && (uint)comEx.ErrorCode == 0x800401D0)
                {
                    args.Handled = true;
                    return;
                }
            };

            // 软件启动时自动请求管理员权限
            if (!AdminHelper.IsAdministrator())
            {
                SingleInstanceHelper.Cleanup();
                if (AdminHelper.RestartAsAdministrator())
                {
                    Shutdown();
                    return;
                }

                MessageBox.Show(
                    "UniShare 需要管理员权限以绑定标准 TCP 445 端口并接管共享服务驱动。\n请在 UAC 提权提示中选择“是”。",
                    "需要管理员权限",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Shutdown();
                return;
            }

            bool startMinimized = System.Linq.Enumerable.Any(e.Args, arg => string.Equals(arg, "--minimized", System.StringComparison.OrdinalIgnoreCase))
                || System.Linq.Enumerable.Any(System.Environment.GetCommandLineArgs(), arg => string.Equals(arg, "--minimized", System.StringComparison.OrdinalIgnoreCase));

            var mainWindow = new MainWindow(startMinimized);
            MainWindow = mainWindow;

            if (startMinimized)
            {
                mainWindow.StartSilent();
            }
            else
            {
                mainWindow.Show();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SingleInstanceHelper.Cleanup();

            if (MainWindow is MainWindow mw && mw.DataContext is ViewModels.MainViewModel vm)
            {
                vm.ShutdownAndRestore();
            }
            base.OnExit(e);
        }
    }
}
