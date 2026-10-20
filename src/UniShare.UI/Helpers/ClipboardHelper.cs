using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace UniShare.UI.Helpers
{
    /// <summary>
    /// 具备多级重试与原生 Win32 容错机制的剪贴板工具类。
    /// 解决 WPF 原生 Clipboard.SetText 在 Windows 10/11 下因 OLE Flush 竞争
    /// 或第三方软件（虚拟机共享剪贴板、剪贴板历史记录、监控工具等）占用时抛出
    /// CLIPBRD_E_CANT_OPEN (0x800401D0) 导致程序崩溃的问题。
    /// </summary>
    public static class ClipboardHelper
    {
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        /// <summary>
        /// 安全地将文本写入系统剪贴板。
        /// </summary>
        /// <param name="text">要复制的文本内容</param>
        /// <param name="retryCount">遭遇锁定时最大重试次数</param>
        /// <param name="retryDelayMs">每次重试间隔毫秒数</param>
        /// <returns>是否成功复制</returns>
        public static bool SetText(string text, int retryCount = 10, int retryDelayMs = 50)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            // 1. 优先使用底层 Win32 API 写入 Unicode 文本（绕过 WPF OLE Flush 导致的竞争锁）
            for (int i = 0; i < retryCount; i++)
            {
                if (TrySetTextNative(text))
                {
                    return true;
                }

                if (i < retryCount - 1)
                {
                    Thread.Sleep(retryDelayMs);
                }
            }

            // 2. 若 Native API 因特殊环境限制未成功，降级尝试 WPF Clipboard.SetDataObject (copy = false，避免 OleFlushClipboard 阻塞)
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, false);
                    return true;
                }
                catch (COMException)
                {
                    Thread.Sleep(retryDelayMs);
                }
                catch
                {
                    break;
                }
            }

            // 3. 最终尝试标准 Clipboard.SetText
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetTextNative(string text)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                return false;
            }

            IntPtr hGlobal = IntPtr.Zero;
            try
            {
                if (!EmptyClipboard())
                {
                    return false;
                }

                int bytesNeeded = (text.Length + 1) * 2;
                hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytesNeeded);
                if (hGlobal == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr target = GlobalLock(hGlobal);
                if (target == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    Marshal.WriteInt16(target, text.Length * 2, 0); // Unicode 结尾空字符 '\0'
                }
                finally
                {
                    GlobalUnlock(hGlobal);
                }

                // 当 SetClipboardData 成功时，系统将接管该全局内存所有权，不得手动 GlobalFree
                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    return false;
                }

                hGlobal = IntPtr.Zero; // 成功移交所有权
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hGlobal != IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                }
                CloseClipboard();
            }
        }
    }
}
