using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace TiaMcp.Generation.Planning.Tests
{
    internal static class GoldenFiles
    {
        internal static void Verify((string RelativePath, string Actual)[] files, [CallerFilePath] string sourceFile = "")
        {
            bool regenerate = Environment.GetEnvironmentVariable("TIA_MCP_UPDATE_GOLDEN") == "1";
            foreach (var (relativePath, actual) in files)
            {
                var bytes = Encoding.UTF8.GetBytes(actual);
                if (regenerate)
                {
                    var path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "Golden", relativePath);
                    File.WriteAllBytes(path, bytes);
                }
                else
                    Assert.Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", relativePath)), bytes);
            }
            if (regenerate) Assert.Fail("Plan and SCL goldens were regenerated. Unset TIA_MCP_UPDATE_GOLDEN and rerun to verify.");
        }
    }
}
