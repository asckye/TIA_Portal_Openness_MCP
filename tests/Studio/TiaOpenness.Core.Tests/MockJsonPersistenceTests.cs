using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Mock;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class MockJsonPersistenceTests
{
    [Fact]
    public void Mock_workspace_and_restore_state_read_old_sidecars_and_round_trip_with_STJ()
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "Line.ap21");
        string? sidecar = null;
        try
        {
            using (var session = new MockTiaSession())
            {
                session.Connect(false, false, null);
                session.OpenProject(project);
                var vc = session.VersionControl;
                vc.CreateWorkspace("LineGit", root);
                vc.MapProject("LineGit", "PLC_1", false, null);
                vc.Sync("LineGit", SyncDirection.ProjectToWorkspace, false, null);
                foreach (var file in Directory.GetFiles(root, "*.s7dcl", SearchOption.AllDirectories))
                    File.AppendAllText(file, "\n// restored 轴\n");
                var restored = vc.Sync("LineGit", SyncDirection.WorkspaceToProject, false, null);
                Assert.True(restored.Synchronized > 0);
                var type = vc.GetType();
                sidecar = (string)type.GetField("_statePath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vc)!;
                var state = type.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vc)!;
                var oldJson = JsonConvert.SerializeObject(state, Formatting.Indented);
                Assert.Equal(BridgeJsonGoldenTests.Parse(oldJson).ToString(), BridgeJsonGoldenTests.Parse(File.ReadAllText(sidecar)).ToString());
                // Reopen a file actually written with the original mock's default settings.
                File.WriteAllText(sidecar, oldJson);
            }
            for (int pass = 0; pass < 2; pass++)
            {
                using var reopened = new MockTiaSession();
                reopened.Connect(false, false, null);
                reopened.OpenProject(project);
                var vc = reopened.VersionControl;
                Assert.Equal("LineGit", Assert.Single(vc.ListWorkspaces()).Name);
                var state = vc.GetType().GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vc)!;
                var restored = (IDictionary)state.GetType().GetField("Restored")!.GetValue(state)!;
                Assert.NotEmpty(restored);
                foreach (string key in restored.Keys) Assert.True(restored.Contains(key.ToLowerInvariant()));
                var workspaces = (IList)state.GetType().GetField("Workspaces")!.GetValue(state)!;
                var mappings = (IDictionary)workspaces[0]!.GetType().GetField("Mappings")!.GetValue(workspaces[0])!;
                Assert.NotEmpty(mappings);
                foreach (string key in mappings.Keys) Assert.True(mappings.Contains(key.ToLowerInvariant()));
                // The second reopen consumes STJ output, including populated dictionaries.
                vc.Sync("LineGit", SyncDirection.ProjectToWorkspace, false, null);
            }
        }
        finally
        {
            if (sidecar != null) File.Delete(sidecar);
            Directory.Delete(root, recursive: true);
        }
    }
}
