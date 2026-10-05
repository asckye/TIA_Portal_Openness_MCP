using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaOpenness.Shared
{
    internal sealed class JournalRetention
    {
        internal const int DefaultFileSizeMb = 50;
        internal const int DefaultCopies = 16;
        internal int FileSizeMb { get; }
        internal int Copies { get; }
        internal static string SettingsPath => Path.Combine(DataLocations.Current.ConfigDirectory, "journal-retention.settings");

        internal JournalRetention(int fileSizeMb = DefaultFileSizeMb, int copies = DefaultCopies)
        {
            if (fileSizeMb < 1 || fileSizeMb > 1024) throw new ArgumentOutOfRangeException(nameof(fileSizeMb));
            if (copies < 1 || copies > 256) throw new ArgumentOutOfRangeException(nameof(copies));
            FileSizeMb = fileSizeMb; Copies = copies;
        }

        internal static JournalRetention Load(string path)
        {
            try
            {
                using (JournalFileLock.Acquire(path + ".lock"))
                {
                    if (!File.Exists(path)) return new JournalRetention();
                    int size = DefaultFileSizeMb, copies = DefaultCopies;
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var pair = line.Split('=');
                        if (pair.Length != 2 || !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out int value)) continue;
                        if (pair[0] == "fileSizeMb" && value >= 1 && value <= 1024) size = value;
                        if (pair[0] == "copies" && value >= 1 && value <= 256) copies = value;
                    }
                    return new JournalRetention(size, copies);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Trace.TraceWarning("Journal retention settings unavailable: " + ex.GetType().Name);
                return new JournalRetention();
            }
        }

        internal void Save(string path)
        {
            using (JournalFileLock.Acquire(path + ".lock"))
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write("fileSizeMb=" + FileSizeMb.ToString(CultureInfo.InvariantCulture) + "\ncopies=" + Copies.ToString(CultureInfo.InvariantCulture) + "\n");
                writer.Flush(); stream.Flush(true);
            }
        }

        // Copies includes the active file. This policy applies only to invocation journals.
        internal void Rotate(string path, int nextBytes)
        {
            string previous = path + ".previous";
            if (File.Exists(previous))
            { if (File.Exists(path + ".1")) File.Delete(previous); else File.Move(previous, path + ".1"); }
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*"))
                if (file.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase) && int.TryParse(file.Substring(path.Length + 1), out int copy) && copy >= Copies) File.Delete(file);
            if (!File.Exists(path) || new FileInfo(path).Length == 0 || new FileInfo(path).Length + nextBytes <= (long)FileSizeMb * 1024 * 1024) return;
            for (int copy = Copies - 1; copy >= 1; copy--)
            {
                string source = copy == 1 ? path : path + "." + (copy - 1).ToString(CultureInfo.InvariantCulture);
                string target = path + "." + copy.ToString(CultureInfo.InvariantCulture);
                if (File.Exists(target)) File.Delete(target);
                if (File.Exists(source)) File.Move(source, target);
            }
            if (Copies == 1) File.Delete(path);
        }

        internal static JournalCoverage Coverage(string directory)
        {
            DateTimeOffset? first = null, last = null;
            if (!Directory.Exists(directory)) return new JournalCoverage(first, last);
            foreach (string file in Directory.GetFiles(directory, "calls-*.jsonl*").Where(IsJournal))
            {
                try
                {
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                        {
                            string? row;
                            while ((row = reader.ReadLine()) != null)
                                if (Timestamp(row, out var time)) { first = !first.HasValue || time < first ? time : first; break; }
                        }
                        string tail = LastCompleteLine(stream);
                        if (Timestamp(tail, out var end)) last = !last.HasValue || end > last ? end : last;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { Trace.TraceInformation("Journal changed while reading coverage: " + ex.GetType().Name); }
            }
            return new JournalCoverage(first, last);
        }

        private static bool IsJournal(string path)
        {
            string suffix = Path.GetFileName(path).Split(new[] { ".jsonl" }, StringSplitOptions.None).Last();
            return suffix.Length == 0 || suffix == ".previous" || suffix.StartsWith(".", StringComparison.Ordinal) && int.TryParse(suffix.Substring(1), out _);
        }
        private static bool Timestamp(string row, out DateTimeOffset time)
        {
            const string key = "\"utc\":\"";
            int start = row.IndexOf(key, StringComparison.Ordinal);
            int end = start < 0 ? -1 : row.IndexOf('"', start + key.Length);
            time = default;
            return end >= 0 && row.EndsWith("}", StringComparison.Ordinal) && DateTimeOffset.TryParseExact(row.Substring(start + key.Length, end - start - key.Length),
                "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
        }

        internal static string LastCompleteLine(FileStream stream)
        {
            // Ignore an interrupted final line. Seek backwards in chunks, avoiding a full-file scan.
            long position = stream.Length;
            bool foundEnd = false;
            var bytes = new System.Collections.Generic.List<byte>();
            var buffer = new byte[4096];
            while (position > 0)
            {
                int count = (int)Math.Min(buffer.Length, position); position -= count;
                stream.Position = position;
                int read = 0;
                while (read < count) { int chunk = stream.Read(buffer, read, count - read); if (chunk == 0) break; read += chunk; }
                for (int i = read - 1; i >= 0; i--)
                {
                    if (!foundEnd) { if (buffer[i] == '\n') foundEnd = true; continue; }
                    if (buffer[i] == '\n') { bytes.Reverse(); return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r'); }
                    bytes.Add(buffer[i]);
                }
            }
            bytes.Reverse(); return foundEnd ? Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r') : "";
        }
    }

    internal sealed class JournalCoverage
    {
        internal DateTimeOffset? Start { get; }
        internal DateTimeOffset? End { get; }
        internal JournalCoverage(DateTimeOffset? start, DateTimeOffset? end) { Start = start; End = end; }
    }
}
