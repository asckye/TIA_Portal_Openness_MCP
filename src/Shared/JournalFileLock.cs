using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Threading;

namespace TiaOpenness.Shared
{
    // The persistent file is never deleted: all linked assemblies/processes lock the same inode.
    internal static class JournalFileLock
    {
        internal static FileStream Acquire(string path, bool readOnly = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            path = Path.GetFullPath(path);
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return AcquirePortable(path, readOnly);
            var stream = new FileStream(path, FileMode.OpenOrCreate, readOnly ? FileAccess.Read : FileAccess.ReadWrite,
                FileShare.ReadWrite, 1, FileOptions.Asynchronous);
            using var ready = new ManualResetEvent(false);
            // Suppress IOCP delivery: this OVERLAPPED belongs to the native lock,
            // not to FileStream's managed completion callback.
            var overlapped = new NativeOverlapped { EventHandle = new IntPtr(ready.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1L) };
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
            Marshal.StructureToPtr(overlapped, memory, false);
            try
            {
                if (!LockFileEx(stream.SafeFileHandle, readOnly ? 0u : 2u, 0, 1, 0, memory))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 997) throw new IOException("Journal lock failed.", new Win32Exception(error));
                    if (!ready.WaitOne(TimeSpan.FromSeconds(30)))
                    {
                        CancelIoEx(stream.SafeFileHandle, memory);
                        GetOverlappedResult(stream.SafeFileHandle, memory, out _, true);
                        throw new IOException("Journal lock timed out.");
                    }
                    if (!GetOverlappedResult(stream.SafeFileHandle, memory, out _, false))
                        throw new IOException("Journal lock failed.", new Win32Exception(Marshal.GetLastWin32Error()));
                }
                return stream; // Closing the handle releases its shared/exclusive byte-range lock.
            }
            catch { stream.Dispose(); throw; }
            finally { Marshal.FreeHGlobal(memory); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool LockFileEx(SafeFileHandle file, uint flags, uint reserved, uint low, uint high, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

        private static FileStream AcquirePortable(string path, bool readOnly)
        {
            string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", "");
            var mutex = new Mutex(false, "TiaMcpJournal-" + key);
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) /* swallow(cleanup): a terminated writer released ownership; the file content is checked by its reader */ { acquired = true; }
            if (!acquired) { mutex.Dispose(); throw new IOException("Journal lock timed out."); }
            try { return new PortableLockStream(path, readOnly, mutex); }
            catch { mutex.ReleaseMutex(); mutex.Dispose(); throw; }
        }
        private sealed class PortableLockStream : FileStream
        {
            private Mutex? mutex;
            internal PortableLockStream(string path, bool readOnly, Mutex mutex)
                : base(path, FileMode.OpenOrCreate, readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.ReadWrite) { this.mutex = mutex; }
            protected override void Dispose(bool disposing)
            {
                try { base.Dispose(disposing); }
                finally { if (disposing && mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); mutex = null; } }
            }
        }
    }
}
