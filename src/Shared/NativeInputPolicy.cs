using System;
using System.IO;
using TiaMcp.Adapters.Contracts;

namespace TiaOpenness.Shared
{
    // Only wrap caller-input access before the native operation. Output publication
    // and native exceptions must retain their uncertain/partial outcome.
    internal static class NativeInputPolicy
    {
        internal static string NormalizeSeparators(string path) => NativePathSelection.NormalizeSeparators(path);
        internal static string FullPath(string path) => NativePathSelection.FullPath(path);

        internal static T Read<T>(string parameter, Func<T> read)
        {
            try { return read(); }
            catch (Exception error) when (error is FileNotFoundException || error is DirectoryNotFoundException || error is UnauthorizedAccessException
                || error is PathTooLongException || error is NotSupportedException || error is ArgumentException && error is not AdapterPreconditionException)
            { throw new AdapterPreconditionException("The input file cannot be read. Verify its path and read permissions.", parameter, true, error); }
            // A sharing violation or another I/O failure still happens before the native operation.
            catch (IOException error)
            { throw new AdapterPreconditionException("The input file cannot be read now; another program may hold it open. Close it or pass a copy, then retry.", parameter, false, error); }
        }

        // TIA Portal holds an open project file; a project path is checked for existence only.
        internal static void RequireExists(string path, string parameter)
            => Read(parameter, () =>
            {
                string full = FullPath(path);
                if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException(null, full);
                return true;
            });

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
