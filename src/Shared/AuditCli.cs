using System.IO;
using System.Text.Json;

namespace TiaOpenness.Shared
{
    internal static class AuditCli
    {
        internal static int Verify(string directory, TextWriter output)
        {
            var report = new AuditLog(directory).Verify();
            output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return report.Passed ? 0 : 3;
        }
    }
}
