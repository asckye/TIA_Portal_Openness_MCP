using System;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaOpenness.Shared
{
    // Only wrap caller-input access before the native operation. Output publication
    // and native exceptions must retain their uncertain/partial outcome.
    internal static class NativeInputPolicy
    {
        internal static string NormalizeSeparators(string path) => NativePathSelection.NormalizeSeparators(path);
        internal static string FullPath(string path) => NativePathSelection.FullPath(path);

        internal static void RequireLocal(string path, string parameter)
        {
            if (string.IsNullOrEmpty(path)) return;
            string candidate = path.TrimStart();
            static bool DeviceOrNetwork(string value) =>
                value.Length >= 2 && value[0] is '/' or '\\' && value[1] is '/' or '\\'
                || value.Length >= 4 && value[0] is '/' or '\\' && value[1] == '?' && value[2] == '?' && value[3] is '/' or '\\';
            bool inheritsDrive = candidate.Length < 2 || candidate[1] != ':';
            if (DeviceOrNetwork(candidate) || Path.DirectorySeparatorChar == '\\' && inheritsDrive && DeviceOrNetwork(Environment.CurrentDirectory))
                throw new AdapterPreconditionException("Network and device input paths are refused. Use a local or mapped drive path.", parameter);
        }

        internal static T Read<T>(string parameter, Func<T> read, bool directory = false)
        {
            try { return read(); }
            catch (DirectoryNotFoundException error) when (directory)
            { throw new AdapterPreconditionException("The input directory does not exist.", parameter, true, error); }
            catch (Exception error) when (error is FileNotFoundException || error is DirectoryNotFoundException || error is UnauthorizedAccessException
                || error is PathTooLongException || error is NotSupportedException || error is ArgumentException && error is not AdapterPreconditionException)
            { throw new AdapterPreconditionException("The input file cannot be read. Verify its path and read permissions.", parameter, true, error); }
            // A sharing violation or another I/O failure still happens before the native operation.
            catch (IOException error)
            {
                int code = error.HResult & 0xffff;
                if (code == 123) throw new AdapterPreconditionException("Invalid file path. Check its separators and JSON escaping.", parameter, true, error);
                string reason = code == 32 || code == 33
                    ? "The input file cannot be read now; another program may hold it open. Close it or pass a copy, then retry."
                    : "The input file could not be read. Verify its path and read permissions.";
                throw new AdapterPreconditionException(reason, parameter, false, error);
            }
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
        {
            RequireLocal(path, parameter);
            if (path?.Any(char.IsControl) == true)
                throw new AdapterPreconditionException("File paths cannot contain control characters. Check backslash escaping in the JSON path.", parameter);
            return Read(parameter, () => new FileStream(FullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        internal static void RequireReadable(string path, string parameter, bool directory = false)
        {
            Read(parameter, () =>
            {
                RequireLocal(path, parameter);
                if (directory)
                {
                    using (var entries = Directory.EnumerateFileSystemEntries(FullPath(path)).GetEnumerator()) entries.MoveNext();
                }
                else using (var stream = OpenRead(path, parameter)) { }
                return true;
            }, directory);
        }
    }
}
