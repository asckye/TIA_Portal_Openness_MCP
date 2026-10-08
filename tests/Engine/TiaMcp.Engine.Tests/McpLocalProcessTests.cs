using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.Engine.Tests
{
    internal static class McpLocalProcessTests
    {
        internal static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

        internal static int Child(string mode)
        {
            if (mode == "sleep") { System.Threading.Thread.Sleep(10000); return 0; }
            Console.Write(new string('o', 100000));
            Console.Error.Write(new string('e', 100000));
            return 7;
        }

        private static async Task RunAsync(Action<bool, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "tia shared git 空格 " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var init = await EcosystemFiles.Run("git", new[] { "init", "--quiet" }, root, null, 30);
                check(init["success"]!.GetValue<bool>(), "shared runner initializes a real Git repository in a Unicode path");
                const string value = "空格 \"quoted\" C:\\trailing\\";
                var echo = await EcosystemFiles.Run("git", new[] { "-c", "tia.value=" + value, "config", "--get", "tia.value" }, root, null, 30);
                check(echo["success"]!.GetValue<bool>() && echo["stdout"]!.GetValue<string>().TrimEnd('\r','\n') == value,
                    "shared runner preserves Unicode, quotes and trailing backslashes as one argument");
                string name = "FB 轴.scl";
                File.WriteAllText(Path.Combine(root, name), "FUNCTION_BLOCK Test\nEND_FUNCTION_BLOCK\n", new UTF8Encoding(false));
                var stage = await EcosystemFiles.Run("git", new[] { "add", "--", name }, root, null, 30);
                check(stage["success"]!.GetValue<bool>(), "shared runner stages the selected PLC source");
                const string message = "PLC 轴 source import\n\nMulti-line commit input.";
                var commit = await EcosystemFiles.Run("git", new[] { "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                    "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path.Combine(root, "no-hooks"), "commit", "--quiet", "--file=-" }, root, message, 30);
                var history = await EcosystemFiles.Run("git", new[] { "log", "-1", "--format=%B" }, root, null, 30);
                check(commit["success"]!.GetValue<bool>() && history["stdout"]!.GetValue<string>().TrimEnd('\r','\n') == message,
                    "shared runner sends commit stdin and reads the resulting history");
                var fixture = typeof(McpLocalProcessTests).Assembly.Location;
                var output = await TiaOpenness.Shared.LocalProcess.Run("dotnet", new[] { fixture, "--local-process-fixture", "output" }, root, null, 30, 32);
                check(output.ExitCode == 7 && output.Stdout == new string('o', 32) && output.Stderr == new string('e', 32)
                    && output.OutputTruncated && !output.DataComplete, "shared runner drains both full pipes and reports truncated output");
                var timeout = await TiaOpenness.Shared.LocalProcess.Run("dotnet", new[] { fixture, "--local-process-fixture", "sleep" }, root, null, 1);
                check(timeout.TimedOut && !timeout.Success && !timeout.DataComplete, "shared runner terminates its timed-out child and reports incomplete execution");
                var fail = await EcosystemFiles.Run("git", new[] { "show", "refs/heads/does-not-exist" }, root, null, 30);
                check(!fail["success"]!.GetValue<bool>() && fail["exitCode"]!.GetValue<int>() != 0 && fail["stderr"]!.GetValue<string>().Length > 0,
                    "shared runner preserves a real command failure and its error text");
            }
            finally
            {
                string full = Path.GetFullPath(root);
                if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test cleanup root");
                foreach (var file in Directory.GetFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(full, true);
            }
        }
    }
}
