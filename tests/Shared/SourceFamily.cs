using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaMcp.Tests.Shared;

internal static class SourceFamily
{
    internal static string Read(string path)
    {
        // Read the base first so a family can never hide a missing original file.
        static string ReadSource(string file)
        {
            var bytes = File.ReadAllBytes(file);
            int offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(bytes.AsSpan(offset))
                .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }
        string source = ReadSource(path);
        string full = Path.GetFullPath(path);
        string prefix = Path.GetFileNameWithoutExtension(full) + ".";
        var parts = Directory.EnumerateFiles(Path.GetDirectoryName(full)!, "*.cs")
            .Where(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal) && p != full)
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal);
        return string.Join("\n", new[] { source }.Concat(parts.Select(ReadSource)));
    }
}
