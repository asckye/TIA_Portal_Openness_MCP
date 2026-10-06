using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleaseProductionTests
{
    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void ReleaseCommitStagesTheEnumeratedFilesAndLeavesTheFixtureClean()
    {
        using var fixture = new GitFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "docs"));
        File.WriteAllText(Path.Combine(fixture.Root, "docs", "release-note.md"), "release note\n");
        File.WriteAllText(Path.Combine(fixture.Root, "Version.props"), "<Version>4.0.0</Version>\n");
        var changed = ReleaseCommands.EnumerateReleaseChanges(fixture.Root, "git");
        Assert.Contains("docs/release-note.md", changed);
        Assert.Contains("Version.props", changed);

        var head = ReleaseCommands.CommitReleaseChanges(fixture.Root, "git", "Release 4.0.0: fixture release");

        Assert.Equal("Release 4.0.0: fixture release", fixture.GitText("log", "-1", "--format=%s"));
        Assert.Matches("^[0-9a-f]{40}$", head);
        Assert.Empty(fixture.GitText("status", "--porcelain").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("<Version>4.0.0</Version>", fixture.GitText("show", "HEAD:Version.props"));
    }

    [Fact]
    [Trait("Category", "ReleaseParity")]
    public void ReleaseCommitRejectsRuntimeBinariesAndClearsOnlyItsFixtureIndex()
    {
        using var fixture = new GitFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "runtime"));
        var binary = Path.Combine(fixture.Root, "runtime", "TiaMcp.Engine.V21.exe");
        File.WriteAllText(binary, "fixture binary");
        fixture.Run("add", "runtime/TiaMcp.Engine.V21.exe");
        fixture.Run("commit", "-q", "-m", "fixture binary baseline");
        File.AppendAllText(binary, "changed");

        var error = Assert.Throws<ReleaseException>(() => ReleaseCommands.CommitReleaseChanges(fixture.Root, "git", "Release 4.0.0: fixture"));

        Assert.Contains("binaries must not be committed", error.Message);
        Assert.Empty(fixture.GitText("diff", "--cached", "--name-only").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(" M runtime/TiaMcp.Engine.V21.exe", fixture.GitText("status", "--porcelain"));
    }

    private sealed class GitFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "release-git-fixture-" + Guid.NewGuid().ToString("N"));

        internal GitFixture()
        {
            Directory.CreateDirectory(Root);
            Run("init", "--initial-branch=master");
            Run("config", "user.name", "Release fixture");
            Run("config", "user.email", "release-fixture@example.invalid");
            File.WriteAllText(Path.Combine(Root, "README.md"), "fixture\n");
            File.WriteAllText(Path.Combine(Root, "Version.props"), "<Version>base</Version>\n");
            Run("add", "README.md", "Version.props");
            Run("commit", "-q", "-m", "fixture base");
        }

        internal string GitText(params string[] arguments) => ProcessRunner.Run("git", ["-C", Root, .. arguments], Root).StandardOutput.TrimEnd();

        internal void Run(params string[] arguments)
        {
            var result = ProcessRunner.Run("git", ["-C", Root, .. arguments], Root);
            ProcessRunner.RequireSuccess(result, "Fixture git " + string.Join(' ', arguments));
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            foreach (var directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                File.SetAttributes(directory, FileAttributes.Directory);
            File.SetAttributes(Root, FileAttributes.Directory);
            Directory.Delete(Root, true);
        }
    }
}
