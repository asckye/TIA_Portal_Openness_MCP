using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TiaOpenness.Core.Environment
{
    // The Openness firewall identifies an application by path, modification date and SHA-256 hash. An administrator (or a
    // "Yes to all" answer) writes HKLM\SOFTWARE\Siemens\Automation\Openness\AllowList\<application>\Entry with the values
    // Path, DateModified (UTC, yyyy/MM/dd HH:mm:ss.fff, invariant culture) and FileHash (Base64 SHA-256 of the file), as the
    // V21 Openness firewall topic shows. This check only reads the registry (API audit 2026-10-19, F6).
    public sealed class AllowListEntry
    {
        public string Path { get; set; }
        public string DateModified { get; set; }
        public string FileHash { get; set; }
    }

    public static class OpennessAllowList
    {
        public const string RegistryKey = @"SOFTWARE\Siemens\Automation\Openness\AllowList";

        public static AllowListEntry Expected(string fullPath, DateTime lastWriteUtc, byte[] sha256)
        {
            if (lastWriteUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("The modification time must be UTC.", nameof(lastWriteUtc));
            return new AllowListEntry
            {
                Path = fullPath,
                DateModified = lastWriteUtc.ToString("yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                FileHash = Convert.ToBase64String(sha256),
            };
        }

        // "match", "missing", or the first differing value: "path", "date", "hash".
        public static string Compare(AllowListEntry expected, AllowListEntry actual)
        {
            if (actual == null) return "missing";
            if (!string.Equals(expected.Path, actual.Path, StringComparison.OrdinalIgnoreCase)) return "path";
            if (!string.Equals(expected.DateModified, actual.DateModified, StringComparison.Ordinal)) return "date";
            if (!string.Equals(expected.FileHash, actual.FileHash, StringComparison.Ordinal)) return "hash";
            return "match";
        }

        public static string AdministratorSteps(IEnumerable<KeyValuePair<string, AllowListEntry>> entries)
        {
            var text = new StringBuilder();
            text.AppendLine("An administrator can allow the workers permanently: answer the TIA Portal Openness dialog once with 'Yes to all', or import this .reg file with administrator rights (regedit). Any rebuilt or updated worker needs new values.");
            text.AppendLine("Windows Registry Editor Version 5.00");
            foreach (var entry in entries)
            {
                text.AppendLine();
                text.AppendLine("[HKEY_LOCAL_MACHINE\\" + RegistryKey + "\\" + entry.Key + "\\Entry]");
                text.AppendLine("\"Path\"=\"" + entry.Value.Path.Replace("\\", "\\\\") + "\"");
                text.AppendLine("\"DateModified\"=\"" + entry.Value.DateModified + "\"");
                text.AppendLine("\"FileHash\"=\"" + entry.Value.FileHash + "\"");
            }
            return text.ToString().TrimEnd();
        }
    }
}
