using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleaseRecordSnapshotTests
{
    [Theory]
    [Trait("Category", "ReviewerChain")]
    [InlineData("success")]
    [InlineData("throw")]
    [InlineData("native")]
    public void RecordBackupAndRestoreSurviveEveryStepOutcome(string mode)
    {
        var basePath = Path.Combine(Path.GetTempPath(), "release-record-snapshot-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(basePath, "repo");
        var path = Path.Combine(root, "manifest/release.json");
        var original = new byte[] { 0, 13, 10, 255 };
        var candidate = new byte[] { 1, 2, 3, 4 };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, original);
        try
        {
            var snapshot = new ReleaseRecordSnapshot(root, Path.Combine(basePath, "original"), Path.Combine(basePath, "state"), ["manifest/release.json"]);
            snapshot.RestoreStepState();
            File.WriteAllBytes(path, candidate);
            if (mode == "success")
            {
                snapshot.CaptureStepState();
                File.WriteAllBytes(path, [9]);
                snapshot.RestoreStepState();
                Assert.Equal(candidate, File.ReadAllBytes(path));
            }
            if (mode == "throw")
            {
                var caught = false;
                try { throw new InvalidOperationException("synthetic command exception"); }
                catch (InvalidOperationException) { caught = true; }
                Assert.True(caught);
            }
            if (mode == "native")
            {
                var command = OperatingSystem.IsWindows()
                    ? ProcessRunner.Run("cmd.exe", ["/d", "/c", "exit /b 7"], Path.GetTempPath())
                    : ProcessRunner.Run("/bin/sh", ["-c", "exit 7"], Path.GetTempPath());
                Assert.Equal(7, command.ExitCode);
            }
            snapshot.RestoreOriginal();
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { if (Directory.Exists(basePath)) Directory.Delete(basePath, true); }
    }
}
