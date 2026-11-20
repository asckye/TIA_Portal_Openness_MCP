using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Vci;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class VciSyncTests
{
    [Theory]
    [InlineData("unchanged", false)][InlineData("unrelated", false)][InlineData("empty", false)]
    [InlineData("relative", true)][InlineData("directory-read", true)][InlineData("post-read", true)]
    [InlineData("status-read", true)][InlineData("equal", true)][InlineData("selection-read", true)]
    [InlineData("length", true)][InlineData("timestamp", true)][InlineData("renamed", true)]
    [InlineData("added", true)][InlineData("removed", true)]
    public void Thrown_sync_requires_unchanged_files_and_readable_non_equal_status(string scenario, bool unknown)
    {
        var workspace = Directory.CreateTempSubdirectory();
        var root = workspace.CreateSubdirectory("Blocks");
        try
        {
            const string stem = "Line.Motor";
            string companion = Path.Combine(root.FullName, stem + ".en-US.xml");
            if (scenario != "empty")
            {
                File.WriteAllText(Path.Combine(root.FullName, stem + ".s7dcl"), "TYPE \"Motor\" : STRUCT Speed : Int; END_STRUCT; END_TYPE");
                File.WriteAllText(companion, "<Document/>");
            }
            bool called = false;
            string ReadDirectory()
            {
                if (scenario == "directory-read" || scenario == "post-read" && called) throw new IOException("Mapping path unavailable");
                return scenario == "relative" ? "." : root.FullName;
            }
            void Write()
            {
                called = true;
                switch (scenario)
                {
                    case "post-read": Directory.Move(root.FullName, Path.Combine(workspace.FullName, "Moved")); break;
                    case "unrelated": File.WriteAllText(Path.Combine(root.FullName, "Other.xml"), "<Document/>"); break;
                    case "length": File.AppendAllText(companion, " "); break;
                    case "timestamp": File.SetLastWriteTimeUtc(companion, File.GetLastWriteTimeUtc(companion).AddMinutes(1)); break;
                    case "renamed": File.Move(companion, Path.Combine(root.FullName, stem + ".de-DE.xml")); break;
                    case "added": File.WriteAllText(Path.Combine(root.FullName, stem + ".de-DE.xml"), "<Document/>"); break;
                    case "removed": File.Delete(companion); break;
                }
                throw new InvalidOperationException("VCI refused the object");
            }
            string Status(string name)
            {
                Assert.True(called);
                Assert.Equal(stem, name);
                if (scenario == "status-read") throw new IOException("Status unavailable");
                return scenario == "equal" ? "Equal" : "Unequal";
            }
            var attempts = new VciSyncAttempts();
            var target = VciSyncTarget.Capture(stem, ReadDirectory);
            Assert.Equal("VCI refused the object", attempts.Synchronize(workspace.FullName, target, Write, () => stem, Status,
                scenario == "selection-read", out var after));
            Assert.Equal(scenario == "equal" ? "Equal" : scenario == "status-read" ? null : "Unequal", after);
            var meta = new VciSyncSelection().Evidence(false, failed: 1);
            attempts.AddEvidence(meta);
            Assert.Equal(1, meta["exportsIssued"]);
            Assert.Equal(unknown ? 1 : 0, meta["unconfirmed"]);
            Assert.Equal("VCI refused the object", meta["nativeMessage"]);
            Assert.Equal(unknown, Worker(meta).OutcomeUnknown);
        }
        finally { workspace.Delete(true); }
    }

    private static VersionControlResult Worker(Dictionary<string, object?> meta)
        => VersionControlAdapter.WithWriteOutcome(new VersionControlRequest { Operation = "SynchronizeVersionControlWorkspace", DryRun = false },
            new VersionControlResult { Meta = meta });

    [Theory]
    [InlineData("fresh", false)]
    [InlineData("workspace-gone", true)]
    [InlineData("mapping-gone", true)]
    [InlineData("ambiguous", true)]
    [InlineData("status-unreadable", true)]
    public void Thrown_export_uses_the_saved_path_and_status_from_the_reacquired_mapping(string scenario, bool unknown)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            const string stem = "Line.Motor";
            string directory = VciWorkspaceLayout.LegacyMappingDirectory(root.FullName, @"\Blocks\Line.Motor.xml").FullName;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, stem + ".xml"), "<Document/>");
            bool disposed = false;
            int originalReads = 0, workspaceLookups = 0, statusReads = 0;
            string OriginalProperty(string value)
            {
                originalReads++;
                return disposed ? throw new ObjectDisposedException("original mapping") : value;
            }
            var target = VciSyncTarget.Capture(OriginalProperty(stem), () => OriginalProperty(directory));
            void Write() { disposed = true; throw new IOException("Original refusal"); }
            (string Name, string Directory) Reacquire()
            {
                workspaceLookups++;
                if (!disposed || scenario == "workspace-gone") throw new IOException("Workspace unavailable");
                var mappings = new List<(string Name, string Directory)> { (stem, root.FullName), ("Other", directory) };
                if (scenario != "mapping-gone") mappings.Add((stem, directory));
                if (scenario == "ambiguous") mappings.Add((stem, directory));
                return target.Find(mappings, m => m.Name, m => m.Directory);
            }
            string ReadStatus((string Name, string Directory) mapping)
            {
                statusReads++;
                if (mapping != (stem, directory) || scenario == "status-unreadable") throw new IOException("Status unavailable");
                return "Unequal";
            }
            var attempts = new VciSyncAttempts();
            Assert.Equal("Original refusal", attempts.Synchronize(root.FullName, target, Write, Reacquire, ReadStatus, false, out var after));
            Assert.Equal(2, originalReads);
            Assert.Equal(1, workspaceLookups);
            Assert.Equal(scenario is "fresh" or "status-unreadable" ? 1 : 0, statusReads);
            Assert.Equal(unknown ? null : "Unequal", after);
            var meta = new VciSyncSelection().Evidence(false, failed: 1);
            attempts.AddEvidence(meta);
            Assert.Equal(1, meta["exportsIssued"]);
            Assert.Equal(unknown ? 1 : 0, meta["unconfirmed"]);
            Assert.Equal(unknown, Worker(meta).OutcomeUnknown);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("sibling")]
    [InlineData("dot-dot")]
    [InlineData("root-unreadable")]
    public void Unchanged_files_outside_the_known_workspace_cannot_confirm_failure(string scenario)
    {
        var parent = Directory.CreateTempSubdirectory();
        try
        {
            string root = parent.CreateSubdirectory("Workspace").FullName;
            string outside = parent.CreateSubdirectory("Workspace2").FullName;
            File.WriteAllText(Path.Combine(outside, "Motor.xml"), "<Document/>");
            string directory = scenario == "dot-dot" ? Path.Combine(root, "..", "Workspace2") : outside;
            var target = VciSyncTarget.Capture("Motor", () => directory);
            var attempts = new VciSyncAttempts();
            attempts.Synchronize(scenario == "root-unreadable" ? "?" : root, target, () => throw new IOException("Refused"),
                () => "Unequal", status => status, false, out var after);
            Assert.Equal("Unequal", after);
            var meta = new VciSyncSelection().Evidence(false, failed: 1);
            attempts.AddEvidence(meta);
            Assert.Equal(1, meta["unconfirmed"]);
            Assert.True(Worker(meta).OutcomeUnknown);
        }
        finally { parent.Delete(true); }
    }

    [Fact]
    public void Multiple_exports_retain_the_first_failure_and_count_each_issue_once()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var attempts = new VciSyncAttempts();
            attempts.Synchronize(root.FullName, VciSyncTarget.Capture("Failed", () => root.FullName), () => throw new IOException("Original refusal"),
                () => "WorkspaceFileMissing", status => status, false, out _);
            attempts.Synchronize(root.FullName, VciSyncTarget.Capture("Uncertain", () => root.FullName), () => throw new IOException("Later refusal"),
                () => "Equal", status => status, false, out _);
            attempts.Synchronize(root.FullName, VciSyncTarget.Capture("Exported", () => root.FullName), () => { }, () => "",
                _ => throw new Exception("Unexpected status read"), false, out _);
            var meta = new VciSyncSelection().Evidence(false, synchronized: 1, failed: 2);
            attempts.AddEvidence(meta);
            Assert.Equal("Original refusal", meta["nativeMessage"]);
            Assert.Equal(3, meta["exportsIssued"]);
            Assert.Equal(1, meta["unconfirmed"]);
            Assert.True(Worker(meta).OutcomeUnknown);
        }
        finally { root.Delete(true); }
    }

    public static VersionControlResult InterruptedSync()
    {
        var attempts = new VciSyncAttempts();
        attempts.Synchronize("", VciSyncTarget.Capture("Motor", () => ""), () => { }, () => "Equal", status => status, false, out _);
        return Worker(attempts.Interrupted(new IOException("Workspace read failed after export")));
    }

    [Fact]
    public void Worker_preserves_an_interruption_after_issue_as_unknown()
    {
        var reply = InterruptedSync();
        Assert.True(reply.OutcomeUnknown);
        Assert.True(reply.MayHaveChanged);
        Assert.Equal(1, reply.Meta["exportsIssued"]);
    }

    public static VersionControlResult WorkspaceRefusal(bool empty)
    {
        var error = Assert.Throws<AdapterPreconditionException>(() => VersionControlAdapter.Select(
            empty ? Array.Empty<string>() : new[] { "First", "Second" }, "missing", name => name));
        return new VersionControlResult { Message = error.Message, Meta = VersionControlAdapter.FailureEvidence(error) };
    }

    [Fact]
    public void Workspace_selection_distinguishes_an_empty_project_from_a_missing_name()
    {
        Assert.Equal("no-workspace", WorkspaceRefusal(true).Meta["condition"]);
        Assert.Equal("not-found", WorkspaceRefusal(false).Meta["condition"]);
        var names = new[] { "First", "Second" };
        Assert.Equal("First", VersionControlAdapter.Select(names, "", _ => throw new Exception("Unexpected name read.")));
        Assert.Equal("Second", VersionControlAdapter.Select(names, " SECOND ", name => name));
        var afterIssue = VersionControlAdapter.FailureEvidence(AdapterPreconditionException.NotFound("Workspace disappeared."), issued: true);
        Assert.Equal("not-found", afterIssue["condition"]);
        Assert.Equal(true, afterIssue["mayHaveChanged"]);
        Assert.Null(afterIssue["v4Rejection"]);
    }

    public static VersionControlResult SkippedUnknown(bool dryRun)
    {
        var selection = new VciSyncSelection();
        for (int i = 0; i < 3; i++)
            Assert.False(selection.Include(() => throw new IOException("Status probe " + i), true, out _));
        return new VersionControlResult { Message = selection.Summary("Nothing selected.", true), Meta = selection.Evidence(dryRun) };
    }

    [Fact]
    public void Changed_only_distinguishes_equal_unknown_and_differing_objects()
    {
        var reply = SkippedUnknown(true);
        Assert.DoesNotContain("in sync", reply.Message);
        Assert.Contains("changedOnly=false", reply.Message);
        Assert.Equal(3, reply.Meta["skippedUnknown"]);
        Assert.Equal(false, reply.Meta["dataComplete"]);
        var selection = new VciSyncSelection();
        Assert.False(selection.Include(() => "Equal", false, out _));
        Assert.Contains("in sync", selection.Summary("", true));
        Assert.Equal(1, selection.SkippedEqual);
        Assert.True(selection.Include(() => "Unknown", false, out _));
        Assert.True(selection.Include(() => "Unequal", true, out _));
    }
}
