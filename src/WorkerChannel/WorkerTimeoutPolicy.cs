using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TiaMcp.WorkerChannel
{
    public sealed class WorkerTimeoutPolicy
    {
        // Data, rather than separate timeout decisions in the two host pipelines.
        public static readonly IReadOnlyDictionary<string, TimeSpan> Defaults = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
        {
            ["default"] = TimeSpan.FromSeconds(120), ["firstAttach"] = TimeSpan.FromMinutes(5),
            ["compile"] = TimeSpan.FromMinutes(30), ["transfer"] = TimeSpan.FromMinutes(30),
            ["importExport"] = TimeSpan.FromMinutes(30), ["archive"] = TimeSpan.FromMinutes(30),
            ["libraryUpdate"] = TimeSpan.FromMinutes(30)
        };
        private readonly Dictionary<string, TimeSpan> budgets;
        public WorkerTimeoutPolicy(int defaultSeconds = 120, string? configJson = null, string? environmentJson = null)
        {
            if (defaultSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(defaultSeconds));
            budgets = Defaults.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            budgets["default"] = TimeSpan.FromSeconds(defaultSeconds);
            Apply(configJson); Apply(environmentJson);
        }
        private void Apply(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            using var document = JsonDocument.Parse(json!);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(entry.Name) || !budgets.ContainsKey(entry.Name) || !entry.Value.TryGetDouble(out var seconds)
                    || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0 || seconds > 86400)
                    throw new ArgumentException("Worker timeout overrides require known family names and seconds in (0, 86400].");
                budgets[entry.Name] = TimeSpan.FromSeconds(seconds);
            }
        }
        public IReadOnlyDictionary<string, TimeSpan> Budgets => budgets;
        public TimeSpan For(string operation, bool firstAttach = false)
            => budgets[firstAttach ? "firstAttach" : Family(operation)];
        public static string Family(string operation)
        {
            if (operation.IndexOf("Compile", StringComparison.OrdinalIgnoreCase) >= 0) return "compile";
            if (operation.IndexOf("Download", StringComparison.OrdinalIgnoreCase) >= 0 || operation.IndexOf("Upload", StringComparison.OrdinalIgnoreCase) >= 0) return "transfer";
            if (operation.IndexOf("Import", StringComparison.OrdinalIgnoreCase) >= 0 || operation.IndexOf("Export", StringComparison.OrdinalIgnoreCase) >= 0) return "importExport";
            if (operation.IndexOf("Archive", StringComparison.OrdinalIgnoreCase) >= 0 || operation.IndexOf("Retrieve", StringComparison.OrdinalIgnoreCase) >= 0) return "archive";
            if (operation.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0 && operation.IndexOf("Librar", StringComparison.OrdinalIgnoreCase) >= 0) return "libraryUpdate";
            return "default";
        }
    }
}
