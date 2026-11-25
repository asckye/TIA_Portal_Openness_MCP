using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Vci;
using Xunit;
using Siemens.Engineering.VersionControl;

namespace Siemens.Engineering.VersionControl
{
    internal sealed class Workspace { internal string Name = "Line"; }
}
namespace TiaMcp.Adapters.Native.Vci
{
    internal sealed partial class VersionControlAdapter
    {
        internal List<Workspace> Workspaces { get; } = new();
        internal Action<DirectoryInfo>? Creating;
        private VersionControlAdapter RequireVci() => this;
        private static List<Workspace> AllWorkspaces(VersionControlAdapter vci) => vci.Workspaces;
        private static T Keep<T>(T value) => value;
    }
}
namespace TiaOpenness.Openness
{
    internal static class VersionControlPrimitives
    {
        internal static string Name(Workspace ws) => ws.Name;
        internal static VersionControlAdapter Group(VersionControlAdapter owner) => owner;
        internal static VersionControlAdapter Workspaces(VersionControlAdapter owner) => owner;
        internal static Workspace Create(VersionControlAdapter owner, string name, DirectoryInfo dir)
        {
            owner.Creating!(dir);
            throw new InvalidOperationException("Create refused.");
        }
    }
}
namespace TiaMcp.Engine.Tests
{
    public sealed class WorkspaceRefusalTests
    {
        [Theory]
        [InlineData("unchanged")]
        [InlineData("registered")]
        [InlineData("created-file")]
        [InlineData("existing-file")]
        [InlineData("timeout")]
        public void Create_checks_both_workspace_registration_and_folder(string failure)
        {
            string path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try
            {
                var adapter = new VersionControlAdapter();
                if (failure == "existing-file") File.WriteAllText(Path.Combine(path, "FC1.scl"), "FUNCTION FC1 : Void");
                int calls = 0;
                adapter.Creating = dir => {
                    calls++;
                    if (failure == "registered") adapter.Workspaces.Add(new());
                    if (failure == "created-file") File.WriteAllText(Path.Combine(dir.FullName, "Motor.xml"), "<Document/>");
                    if (failure == "timeout") throw new TimeoutException();
                };
                var reply = VersionControlAdapter.WithWriteOutcome(new VersionControlRequest { Operation = "CreateVersionControlWorkspace", DryRun = false },
                    adapter.CreateVersionControlWorkspace("Line", path, false));
                Assert.Equal(1, calls);
                Assert.Equal(failure != "unchanged", reply.OutcomeUnknown);
            }
            finally { Directory.Delete(path, true); }
        }

        [Fact]
        public void Timed_out_export_cannot_be_settled_by_matching_files_and_mapping()
        {
            var root = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var attempts = new VciSyncAttempts();
                var target = VciSyncTarget.Capture("Motor", () => root);
                attempts.Synchronize(root, target, () => throw new TimeoutException(), () => "Unequal", value => value, false, out _);
                var evidence = attempts.Interrupted(new IOException());
                Assert.Equal(1, evidence["unconfirmed"]);
                Assert.Equal(true, evidence["writeOutcomeUnknown"]);
            }
            finally { Directory.Delete(root); }
        }

        [Fact]
        public void Lookup_after_settled_exports_preserves_the_partial_result()
        {
            string path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try
            {
                var attempts = new VciSyncAttempts();
                var target = VciSyncTarget.Capture("Motor", () => path);
                attempts.Synchronize(path, target, () => { }, () => "Equal", status => status, false, out _);
                attempts.Synchronize(path, target, () => throw new IOException("Export refused"), () => "Unequal", status => status, false, out _);
                var error = Assert.Throws<IOException>(() => attempts.FindNext<string>(() => throw new IOException("Next mapping lookup failed")));
                var meta = attempts.Interrupted(error);
                Assert.Equal(1, meta["synchronized"]);
                Assert.Equal(0, meta["unconfirmed"]);
                Assert.Equal(true, meta["mayHaveChanged"]);
                Assert.Equal(false, meta["writeOutcomeUnknown"]);
            }
            finally { Directory.Delete(path); }
        }
    }
}
