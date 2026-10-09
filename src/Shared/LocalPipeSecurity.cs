using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TiaOpenness.Shared
{
    internal static class LocalPipeSecurity
    {
        internal static string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("No current user SID.");
        internal static string SecurityDescriptor(string sid) => "O:" + new SecurityIdentifier(sid).Value + "D:P(A;;GA;;;" + sid + ")";

        internal static NamedPipeServerStream CreateServer(string name, string sid, bool first, int maxInstances)
        {
            if (maxInstances < 1 || maxInstances > 255) throw new ArgumentOutOfRangeException(nameof(maxInstances));
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(SecurityDescriptor(sid), 1, out var descriptor, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = descriptor };
                // Duplex + overlapped, byte mode, reject remote clients. Protected DACL grants only this SID.
                var handle = CreateNamedPipe(@"\\.\pipe\" + name, 3u | 0x40000000u | (first ? 0x00080000u : 0u),
                    8u, (uint)maxInstances, 65536, 65536, 0, ref attributes);
                if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            }
            finally { LocalFree(descriptor); }
        }
        internal static bool PeerIsCurrentUser(NamedPipeServerStream pipe, string sid)
        {
            string? peer = null;
            pipe.RunAsClient(() => peer = WindowsIdentity.GetCurrent().User?.Value);
            return peer == sid;
        }
        internal static bool ServerIsCurrentUser(NamedPipeClientStream pipe, string sid)
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) return false;
            using (var process = OpenProcess(0x1000, false, pid))
            {
                if (process.IsInvalid || !OpenProcessToken(process.DangerousGetHandle(), 8, out var token)) return false;
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User?.Value == sid;
            }
        }
        // Control callers require both checks. Approval keeps its original SID-only behavior.
        internal static bool PeerMatches(NamedPipeServerStream pipe, string sid, string expectedImagePath)
            => PeerIsCurrentUser(pipe, sid) && GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)
                && ImageMatches(pid, expectedImagePath);
        internal static bool ServerMatches(NamedPipeClientStream pipe, string sid, string expectedImagePath)
            => ServerIsCurrentUser(pipe, sid) && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)
                && ImageMatches(pid, expectedImagePath);
        private static bool ImageMatches(uint pid, string expectedImagePath)
        {
            if (string.IsNullOrWhiteSpace(expectedImagePath) || !Path.IsPathRooted(expectedImagePath)) return false;
            using (var process = OpenProcess(0x1000, false, pid))
            {
                if (process.IsInvalid) return false;
                var image = new StringBuilder(32768);
                uint size = (uint)image.Capacity;
                return QueryFullProcessImageName(process, 0, image, ref size)
                    && string.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(expectedImagePath), StringComparison.OrdinalIgnoreCase);
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
        { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafePipeHandle CreateNamedPipe(string name, uint mode, uint pipeMode, uint instances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    }

}
