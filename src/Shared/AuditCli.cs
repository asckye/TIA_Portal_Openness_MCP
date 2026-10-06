using System.IO;
using System.Linq;
using System.Text.Json;

namespace TiaOpenness.Shared
{
    internal static class AuditCli
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        internal static int Verify(string directory, TextWriter output)
        {
            var report = new AuditLog(directory).Verify();
            output.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return report.Passed ? 0 : 3;
        }
        internal static int Verify(string[] directories, TextWriter output)
        {
            var reports = directories.Select(path => new AuditLog(path).Verify()).ToArray();
            output.WriteLine(JsonSerializer.Serialize(reports, JsonOptions));
            return reports.All(report => report.Passed) ? 0 : 3;
        }
    }
}
