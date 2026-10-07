using System;
using System.IO;
using TiaMcp.Adapters.Contracts;

namespace TiaOpenness.Shared
{
    // Only wrap caller-input access before the native operation. Output publication
    // and native exceptions must retain their uncertain/partial outcome.
    internal static class NativeInputPolicy
    {
        internal static string NormalizeSeparators(string path)
            => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        internal static string FullPath(string path) => Path.GetFullPath(NormalizeSeparators(path));

        internal static T Read<T>(string parameter, Func<T> read)
        {
            try { return read(); }
            catch (Exception error) when (error is FileNotFoundException || error is DirectoryNotFoundException || error is UnauthorizedAccessException)
            { throw new AdapterPreconditionException("The input file cannot be read. Verify its path and read permissions.", parameter, true, error); }
        }

        internal static FileStream OpenRead(string path, string parameter)
            => Read(parameter, () => new FileStream(FullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read));

        internal static void RequireReadable(string path, string parameter, bool directory = false)
        {
            Read(parameter, () =>
            {
                string full = FullPath(path);
                if (directory)
                {
                    using (var entries = Directory.EnumerateFileSystemEntries(full).GetEnumerator()) entries.MoveNext();
                }
                else using (var stream = OpenRead(full, parameter)) { }
                return true;
            });
        }
    }
}
