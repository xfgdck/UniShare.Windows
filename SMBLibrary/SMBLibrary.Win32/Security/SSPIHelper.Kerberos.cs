/* Copyright (C) 2026 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using System.Runtime.InteropServices;

namespace SMBLibrary.Win32.Security
{
    public partial class SSPIHelper
    {
        public static SecHandle AcquireKerberosCredentialsHandle(string serverPrincipalName)
        {
            return AcquireKerberosCredentialsHandle(serverPrincipalName, null);
        }

        public static SecHandle AcquireKerberosCredentialsHandle(string serverPrincipalName, string domainName, string userName, string password)
        {
            SEC_WINNT_AUTH_IDENTITY auth = GetWinNTAuthIdentity(domainName, userName, password);
            return AcquireKerberosCredentialsHandle(serverPrincipalName, auth);
        }

        private static SecHandle AcquireKerberosCredentialsHandle(string serverPrincipalName, SEC_WINNT_AUTH_IDENTITY? auth)
        {
            SecHandle credential;
            SECURITY_INTEGER expiry;

            IntPtr pAuthData;
            if (auth.HasValue)
            {
                pAuthData = Marshal.AllocHGlobal(Marshal.SizeOf(auth.Value));
                Marshal.StructureToPtr(auth.Value, pAuthData, false);
            }
            else
            {
                pAuthData = IntPtr.Zero;
            }

            uint result = AcquireCredentialsHandle(serverPrincipalName, "Kerberos", SECPKG_CRED_BOTH, IntPtr.Zero, pAuthData, IntPtr.Zero, IntPtr.Zero, out credential, out expiry);
            if (pAuthData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pAuthData);
            }

            if (result != SEC_E_OK)
            {
                throw new Exception("AcquireCredentialsHandle failed, Error code 0x" + result.ToString("X8"));
            }

            return credential;
        }
    }
}
