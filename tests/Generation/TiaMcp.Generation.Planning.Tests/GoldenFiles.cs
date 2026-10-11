using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace TiaMcp.Generation.Planning.Tests
{
    internal static class GoldenFiles
    {
        internal static void Verify(string relativePath, string actual, [CallerFilePath] string sourceFile = "")
        {
            var bytes = Encoding.UTF8.GetBytes(actual);
            if (Environment.GetEnvironmentVariable("TIA_MCP_UPDATE_GOLDEN") == "1")
            {
                var path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "Golden", relativePath);
                File.WriteAllBytes(path, bytes);
                return;
            }
            Assert.Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", relativePath)), bytes);
        }
    }
}
