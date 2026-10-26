using System;
using System.Runtime.InteropServices;
using System.Text;

namespace UniShare.Core
{
    /// <summary>
    /// Windows DPAPI (CryptProtectData) 轻量封装，使用 LocalMachine 范围加密，
    /// 保证不同管理员账户运行本程序时均可解密，同时密文离开本机后无法还原。
    /// </summary>
    public static class DpapiHelper
    {
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
        private const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;
        private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("UniShare.Windows/config/v1");

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static string Protect(string plainText)
        {
            byte[] result = Transform(Encoding.UTF8.GetBytes(plainText), protect: true);
            return Convert.ToBase64String(result);
        }

        public static bool TryUnprotect(string protectedBase64, out string plainText)
        {
            plainText = string.Empty;
            try
            {
                byte[] result = Transform(Convert.FromBase64String(protectedBase64), protect: false);
                plainText = Encoding.UTF8.GetString(result);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] Transform(byte[] input, bool protect)
        {
            GCHandle inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            GCHandle entropyHandle = GCHandle.Alloc(s_entropy, GCHandleType.Pinned);
            DATA_BLOB output = new DATA_BLOB();
            try
            {
                DATA_BLOB inBlob = new DATA_BLOB { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
                DATA_BLOB entropyBlob = new DATA_BLOB { cbData = s_entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
                int flags = CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE;

                bool ok = protect
                    ? CryptProtectData(ref inBlob, "UniShare", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, flags, ref output)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
                if (!ok)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }

                byte[] result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                if (output.pbData != IntPtr.Zero)
                {
                    LocalFree(output.pbData);
                }
                inHandle.Free();
                entropyHandle.Free();
            }
        }
    }
}
