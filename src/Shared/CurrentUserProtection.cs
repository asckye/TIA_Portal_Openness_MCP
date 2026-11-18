using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TiaOpenness.Shared
{
    internal static class CurrentUserProtection
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Blob { internal int Size; internal IntPtr Data; }
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags,
                out Blob output);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags,
                out Blob output);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        // DPAPI without a WindowsDesktop dependency in the cross-platform host. No machine-wide scope or optional entropy.
        internal static byte[] Transform(byte[] bytes, bool protect)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("HTTP access keys require Windows DPAPI for the current user.");
            var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Blob output = default;
            try
            {
                Marshal.Copy(bytes, 0, input.Data, bytes.Length);
                bool ok = protect ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new CryptographicException("The current Windows user could not access the protected HTTP key.");
                var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero)
                {
                    Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                    LocalFree(output.Data);
                }
            }
        }

    }
}
