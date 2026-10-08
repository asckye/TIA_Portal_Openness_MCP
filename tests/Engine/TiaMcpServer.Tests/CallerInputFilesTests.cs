using System;
using System.IO;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcpServer.Tests
{
    public sealed class CallerInputFilesTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tia-cif-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        public CallerInputFilesTests() => Directory.CreateDirectory(root);
        public void Dispose() => Directory.Delete(root, true);

        private static string Arguments(string key, string path) => new JsonObject { [key] = path }.ToJsonString();

        // V20 finding 35: TIA Portal holds the open project file without read sharing.
        [Theory]
        [InlineData("ConnectProject", "projectPath")]
        [InlineData("OpenProject", "path")]
        [InlineData("OpenLocalSession", "localSessionPath")]
        public void Project_paths_are_checked_for_existence_without_opening_the_file(string tool, string key)
        {
            string project = Path.Combine(root, "Project1.ap20");
            File.WriteAllText(project, "fixture");
            using (new FileStream(project, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                McpServer.ValidateCallerInputFiles(tool, Arguments(key, project));
            var missing = Assert.Throws<AdapterPreconditionException>(() => McpServer.ValidateCallerInputFiles(tool, Arguments(key, Path.Combine(root, "Missing.ap20"))));
            Assert.True(missing.IsArgument);
            Assert.Equal(key, missing.ParamName);
        }

        [Fact]
        public void A_locked_import_file_is_a_precondition_refusal_before_the_operation()
        {
            string input = Path.Combine(root, "Block.xml");
            File.WriteAllText(input, "<Document />");
            using (new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Exception? error = Record.Exception(() => McpServer.ValidateCallerInputFiles("ImportPlcBlock", Arguments("importPath", input)));
                // Only Windows enforces the share mode for another reader.
                if (!OperatingSystem.IsWindows() && error == null) return;
                var refusal = Assert.IsType<AdapterPreconditionException>(error);
                Assert.False(refusal.IsArgument);
                Assert.Equal("importPath", refusal.ParamName);
                Assert.Equal(TiaOpenness.Shared.HostFailureKind.Precondition, TiaOpenness.Shared.HostFailurePolicy.Classify(refusal, true, false));
            }
        }
    }
}
