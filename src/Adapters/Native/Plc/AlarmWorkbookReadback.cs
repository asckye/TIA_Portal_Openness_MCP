using System;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace TiaMcp.Adapters.Native.Plc
{
    internal static class AlarmWorkbookReadback
    {
        // Compare the parts of native exports, including strings, worksheet data and relationships.
        // Core document properties and ZIP timestamps are file metadata.
        internal static string[]? Parts(string file)
        {
            using var package = ZipFile.OpenRead(file);
            if (package.GetEntry("xl/workbook.xml") == null
                || !package.Entries.Any(entry => entry.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)
                    && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))) return null;
            using var digest = SHA256.Create();
            return package.Entries.Where(entry => entry.FullName != "docProps/core.xml").OrderBy(entry => entry.FullName, StringComparer.Ordinal)
                .Select(entry => {
                    using var content = entry.Open();
                    return entry.FullName + ":" + Convert.ToBase64String(digest.ComputeHash(content));
                }).ToArray();
        }
    }
}
