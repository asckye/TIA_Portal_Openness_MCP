using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TiaMcp.Adapters.Contracts
{
    public static class ProjectFileCanonicalizer
    {
        public static string Canonical(string path)
        {
            string normalized = ProjectFileIdentity.AbsoluteFile(path);
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return normalized;
            var tail = new Stack<string>();
            string existing = normalized;
            while (!File.Exists(existing) && !Directory.Exists(existing))
            {
                tail.Push(Path.GetFileName(existing));
                existing = Path.GetDirectoryName(existing) ?? throw new IOException("Project path has no existing filesystem root.");
            }
            using (var handle = CreateFile(existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var buffer = new StringBuilder(32768);
                uint count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (count == 0 || count >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
                string result = buffer.ToString();
                result = result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + result.Substring(8)
                    : result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result.Substring(4) : result;
                foreach (string part in tail) result = Path.Combine(result, part);
                return ProjectFileIdentity.AbsoluteFile(result);
            }
        }

        public static bool Same(string expected, string actual)
        {
            try { return string.Equals(Canonical(expected), Canonical(actual), StringComparison.OrdinalIgnoreCase); }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException || error is Win32Exception)
            { /* swallow(parse-fallback): an invalid or unreadable identity never matches the preset. */ return false; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    }
}
