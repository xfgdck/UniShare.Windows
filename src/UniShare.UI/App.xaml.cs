using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using UniShare.Core;
using UniShare.UI.Helpers;
using UniShare.UI.Views;

namespace UniShare.UI
{
    public partial class App : Application
    {
        public const string RestoreSystemSmbArg = "--restore-system-smb";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 0. 卸载程序调用的还原模式：按记录的原始状态还原系统共享服务后直接退出，不显示任何界面
            if (e.Args.Any(arg => string.Equals(arg, RestoreSystemSmbArg, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    ViewModels.MainViewModel.RestoreSystemSmbFromConfig();
                }
                catch (Exception ex)
                {
                    CrashLogger.Write("还原系统共享服务失败", ex);
                }
                Shutdown();
                return;
            }

            // 1. 单实例互斥与唤醒检查：若已有运行中的实例，将其置顶到前台并立即退出当前进程
            if (!SingleInstanceHelper.AcquireOrNotifyExisting())
            {
                Shutdown();
                return;
            }

            RegisterExceptionHandlers();

            // 软件启动时自动请求管理员权限
            if (!AdminHelper.IsAdministrator())
            {
                SingleInstanceHelper.Cleanup();
                if (AdminHelper.RestartAsAdministrator())
                {
                    Shutdown();
                    return;
                }

                MessageDialog.ShowWarning(
                    "UniShare 需要管理员权限以绑定标准 TCP 445 端口并接管共享服务驱动。\n请在 UAC 提权提示中选择“是”。",
                    "需要管理员权限");

                Shutdown();
                return;
            }

            bool startMinimized = e.Args.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase))
                || Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

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

        private void RegisterExceptionHandlers()
        {
            DispatcherUnhandledException += (s, args) =>
            {
                // 拦截剪贴板临时锁定等非严重 COM 异常，防止进程崩溃
                if (args.Exception is System.Runtime.InteropServices.COMException comEx && (uint)comEx.ErrorCode == 0x800401D0)
                {
                    args.Handled = true;
                    return;
                }

                // 界面线程异常：记录日志并提示，保持后台共享服务继续运行
                CrashLogger.Write("界面线程未处理异常", args.Exception);
                args.Handled = true;
                try
                {
                    MessageDialog.ShowError(
                        $"程序发生异常，已记录到日志：\n{CrashLogger.GetLogPath()}\n\n{args.Exception.Message}",
                        "程序异常");
                }
                catch { }
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                CrashLogger.Write("后台任务未观察到的异常", args.Exception);
                args.SetObserved();
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                CrashLogger.Write("致命未处理异常（进程即将退出）", args.ExceptionObject as Exception);
                // 进程即将终止：尽力还原系统共享服务，避免 Windows 原生共享一直处于禁用状态
                try
                {
                    if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
                    {
                        vm.ShutdownAndRestore();
                    }
                }
                catch { }
            };
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
