using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace TiaMcp.Updater
{
    internal sealed class UpdaterLog
    {
        internal string FilePath { get; }
        private UpdaterLog(string path) { FilePath = path; }

        internal static UpdaterLog Open(string dataDirectory)
        {
            try
            {
                var directory = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(dataDirectory), "logs", "updater"));
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string name = "updater-" + stamp + "-" + Guid.NewGuid().ToString("N") + ".log";
                string path = Path.Combine(directory.FullName, name);
                using (File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                try
                {
                    foreach (var old in directory.GetFiles("updater-*.log").Where(file => file.FullName != path)
                        .OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal).Skip(9))
                        old.Delete();
                }
                catch (Exception ex) when (IsLogError(ex))
                {
                    File.Delete(path);
                    throw;
                }
                return new UpdaterLog(path);
            }
            catch (Exception ex) when (IsLogError(ex)) { Report(ex); return null; }
        }

        internal void Write(string line)
        {
            try
            {
                // Open the existing log so a long-running worker cannot recreate a file pruned by a newer one.
                using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete))
                {
                    stream.Seek(0, SeekOrigin.End);
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.WriteLine(line);
                }
            }
            catch (Exception ex) when (IsLogError(ex)) { Report(ex); }
        }

        private static void Report(Exception error)
        {
            try { Console.Error.WriteLine(error.Message); }
            catch (IOException) /* swallow(logging-failure): redirected stderr may be on the same full volume as the log */ { }
        }

        private static bool IsLogError(Exception ex) => ex is IOException || ex is UnauthorizedAccessException
            || ex is SecurityException || ex is ArgumentException || ex is NotSupportedException;
    }
}
