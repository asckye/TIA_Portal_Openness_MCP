using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TiaOpenness.Shared
{
    // The persistent file is never deleted: all linked assemblies/processes lock the same inode.
    internal static class JournalFileLock
    {
        internal static FileStream Acquire(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(30)) /* swallow(logging-failure): retry a contended cross-process journal lock until the bounded timeout */ { Thread.Sleep(10); }
            }
        }
    }
}
