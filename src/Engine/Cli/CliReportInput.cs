using System;
using System.IO;
using System.Text;
using System.Text.Json;
using TiaMcp.Logic.V4;

namespace TiaMcpServer.Cli
{
    internal static class CliReportInput
    {
        // The offline analyzer still accepts a report-body file. Keep that contract
        // internal, while user-supplied V4 reports are read through the shared codec.
        internal static T Read<T>(string path, Func<string, T> analyze)
        {
            if (!File.Exists(path)) return analyze(path);
            string json = File.ReadAllText(path, Encoding.UTF8);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schemaVersion", out _)) return analyze(path);
            var report = V4Json.Deserialize<Envelope>(json);
            if (!report.Ok || report.Meta.Outcome != Outcome.Succeeded || report.Meta.Completeness != Completeness.Complete)
                throw new InvalidDataException("The report is incomplete or unsuccessful; inspect its V4 outcome before analysis.");
            if (!report.Data.HasValue || report.Data.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The V4 report has no report body in data.");
            string temporary = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory,
                "tia-report-body-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(temporary, report.Data.Value.GetRawText(), new UTF8Encoding(false));
                return analyze(temporary);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
