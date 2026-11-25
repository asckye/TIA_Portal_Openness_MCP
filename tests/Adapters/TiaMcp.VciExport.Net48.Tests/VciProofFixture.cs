#if !STUDIO_VCI_MODERN
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.VersionControl;
using TiaMcp.Adapters.Native.Vci;
using TiaMcp.Adapters.Contracts.Studio;
using TiaOpenness.Openness;

internal static class VciProofFixture
{
    internal static object UncheckableFiles(DirectoryInfo parent, bool readableRoot)
    {
        var root = parent.CreateSubdirectory(readableRoot ? "invalid-path" : "unreadable-root");
        var workspace = new Workspace(readableRoot ? root : null!);
        var project = new Project();
        project.Vci.WorkspaceGroup.Workspaces.Add(workspace);
        var adapter = new VersionControlAdapter(() => project);
        var events = new List<string>();
        foreach (string path in new[] { "Legacy.txt", @"MissingFolder\Table.xml", "Next.xml" })
        {
            var target = new PlcBlock { Write = (_, _) => events.Add("unexpected repair") };
            var mapped = new WorkspaceMapping(workspace, path, target);
            mapped.Status.State = CompareState.Unequal;
            mapped.Status.OnSynchronized = () => events.Add(path);
            workspace.Mappings.Add(mapped);
        }
        File.WriteAllText(Path.Combine(root.FullName, "Next.xml"), "<Document/>");
        var preview = adapter.SyncVersionControlWorkspace(dryRun: true);
        var reply = adapter.SyncVersionControlWorkspace(dryRun: false);
        foreach (var mapped in workspace.Mappings) mapped.Status.State = CompareState.Unknown;
        var unknown = adapter.SyncVersionControlWorkspace(dryRun: true);
        return new { preview, reply, unknown, events };
    }

    internal static object ProofAfterOtherOperations(DirectoryInfo parent)
    {
        string xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MCP_RC_FC.xml"));
        string changed = xml.Replace("<Name>MCP_RC_FC</Name>", "<Name>Changed</Name>");
        var cases = new Dictionary<string, object>();
        foreach (var key in new[] { "import", "disposed", "wrong-write", "studio-unknown", "cleanup", "stop", "inconsistent" })
        {
            var root = parent.CreateSubdirectory("proof-sequence-" + key);
            var ws = new Workspace(root);
            var project = new Project();
            project.Vci.WorkspaceGroup.Workspaces.Add(ws);
            string projectXml = xml;
            WorkspaceMapping Add(string name, Action<FileInfo, ExportOptions> export)
            {
                File.WriteAllText(Path.Combine(root.FullName, name + ".xml"), xml);
                var mapped = new WorkspaceMapping(ws, name + ".xml", new PlcBlock { Write = export });
                mapped.Status.State = CompareState.Equal;
                ws.Mappings.Add(mapped);
                return mapped;
            }

            FileStream? locked = null;
            DirectoryInfo? scratch = null;
            var first = Add("Motor", (file, _) =>
            {
                if (key == "disposed") throw new EngineeringObjectDisposedException("Disposed PLC block");
                if (key == "cleanup")
                {
                    locked = file.Open(FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    scratch = file.Directory;
                    throw new EngineeringUserAbortException("Original abort");
                }
                File.WriteAllText(file.FullName, key is "wrong-write" or "studio-unknown" or "inconsistent" ? changed : projectXml);
            });
            first.Status.AcceptEqual = true;
            if (key == "wrong-write")
                first.Status.OnSynchronized = () => File.WriteAllText(Path.Combine(root.FullName, "Motor.xml"), xml.Replace("MCP_RC_FC", "Other"));
            if (key == "studio-unknown")
                first.Status.OnSynchronized = () => throw new EngineeringUserAbortException("Unconfirmed write");
            if (key == "inconsistent")
                first.Status.OnSynchronized = () => throw new EngineeringTargetInvocationException("Motor is not consistent. password=fixture-password");
            var next = Add("Next", (file, _) => File.WriteAllText(file.FullName, xml));
            if (key is "import" or "stop" or "cleanup")
            {
                next.Status.State = CompareState.Unequal;
                next.Status.OnSynchronized = () => projectXml = changed;
            }
            if (key == "stop")
                ((PlcBlock)first.LinkedProjectObject).Write = (_, _) => throw new EngineeringUserAbortException("Aborted");
            try
            {
                if (key == "import")
                    cases[key] = new OpennessVersionControl(() => project).Sync("", SyncDirection.WorkspaceToProject, false, null);
                else if (key == "studio-unknown")
                {
                    try { cases[key] = new OpennessVersionControl(() => project).Sync("", SyncDirection.ProjectToWorkspace, false, null); }
                    catch (InvalidOperationException error) { cases[key] = error.Message; }
                }
                else
                    cases[key] = new VersionControlAdapter(() => project).SyncVersionControlWorkspace(dryRun: false);
            }
            finally
            {
                locked?.Dispose();
                scratch?.Delete(true);
            }
        }
        return cases;
    }

    internal static Dictionary<string, object> Run(DirectoryInfo parent)
    {
        var cases = new Dictionary<string, object>();
        string xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MCP_RC_FC.xml"));
        string edited = xml.Replace("<Name>MCP_RC_FC</Name>", "<Name>Edited</Name>");
        string defaults = xml.Replace("<Name>MCP_RC_FC</Name>", "<IsFailsafeCompliant>false</IsFailsafeCompliant><Name>MCP_RC_FC</Name>");
        foreach (string key in new[] { "none", "defaults", "stale", "refused", "missing", "export-refused", "no-write", "new-match",
            "external", "bad-xml", "oversize", "compare-aborted",
            "race", "mixed", "disposed", "abort", "connection", "nonrecoverable", "changed-throw", "pull", "push", "disconnected" })
        {
            var root = parent.CreateSubdirectory("proof-" + key);
            var workspace = new Workspace(root);
            var project = new Project();
            project.Vci.WorkspaceGroup.Workspaces.Add(workspace);
            var adapter = new VersionControlAdapter(() => project);
            var events = new List<string>();
            var temporary = new List<string>();
            var block = new PlcBlock { Name = "Motor" };
            string path = Path.Combine(root.FullName, "Motor.xml");
            if (key != "missing") File.WriteAllText(path, key == "defaults" ? defaults : key == "external" ? edited : xml);
            if (key == "bad-xml") File.WriteAllText(path, "<Document");
            if (key == "oversize") using (var large = File.OpenWrite(path)) large.SetLength(16 * 1024 * 1024 + 1);
            var mapping = new WorkspaceMapping(workspace, "Motor.xml", block);
            workspace.Mappings.Add(mapping);
            mapping.Status.State = CompareState.Equal;
            if (key == "disconnected") mapping.Status.RefreshError = new EngineeringObjectDisposedException(VciExportFixture.Disposed);
            string projectXml = key is "none" or "defaults" or "external" ? xml : edited;
            if (key == "none") projectXml = xml.Replace("V17", "V19").Replace("ID=\"0\"", "ID=\"42\"");
            block.Write = (file, options) =>
            {
                events.Add("export:" + options);
                if (file.FullName.StartsWith(root.FullName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Workspace export");
                temporary.Add(file.DirectoryName!);
                if (key == "compare-aborted") throw new EngineeringUserAbortException("Aborted before writing");
                if (key == "export-refused") throw new EngineeringTargetInvocationException("Export refused");
                File.WriteAllText(file.FullName, key == "defaults" && options == ExportOptions.WithDefaults ? defaults : projectXml);
            };
            mapping.Status.AcceptEqual = key is not ("refused" or "mixed" or "export-refused" or "bad-xml");
            mapping.Status.OnSynchronized = () =>
            {
                events.Add("sync:Motor");
                if (key == "disposed") throw new EngineeringObjectDisposedException("Object disposed");
                if (key == "abort") throw new EngineeringUserAbortException("Operation aborted");
                if (key == "connection") throw new EndOfStreamException("Connection lost");
                if (key == "nonrecoverable") throw new NonRecoverableException("Nonrecoverable");
                if (key == "changed-throw")
                {
                    var modified = File.GetLastWriteTimeUtc(path);
                    File.WriteAllText(path, xml.Replace("MCP_RC_FC", "MCP_RC_FB"));
                    File.SetLastWriteTimeUtc(path, modified);
                    throw new EngineeringTargetInvocationException("Refused after a same-length write");
                }
                if (key == "new-match") projectXml = xml;
                if (key is not ("no-write" or "new-match")) File.WriteAllText(path, projectXml);
            };
            if (key is "race" or "mixed")
            {
                var first = new WorkspaceMapping(workspace, "First.xml", new PlcBlock());
                first.Status.State = CompareState.Unequal;
                File.WriteAllText(Path.Combine(root.FullName, "First.xml"), xml);
                first.Status.OnSynchronized = () =>
                {
                    events.Add("sync:First");
                    if (key == "race") File.WriteAllText(path, defaults);
                };
                workspace.Mappings.Add(first);
            }
            if (key == "compare-aborted")
            {
                var next = new WorkspaceMapping(workspace, "Next.xml", block);
                next.Status.State = CompareState.Equal;
                workspace.Mappings.Add(next);
                File.WriteAllText(Path.Combine(root.FullName, "Next.xml"), xml);
            }
            var status = adapter.GetVersionControlStatus(changedOnly: true);
            var preview = adapter.SyncVersionControlWorkspace(dryRun: true);
            var previewEvents = events.ToArray();
            if (key is "pull" or "push")
            {
                var next = new WorkspaceMapping(workspace, "Next.xml", new PlcBlock { Write = (file, options) => File.WriteAllText(file.FullName, xml) });
                next.Status.State = CompareState.Equal;
                workspace.Mappings.Add(next);
                File.WriteAllText(Path.Combine(root.FullName, "Next.xml"), xml);
                var studio = new OpennessVersionControl(() => project);
                var result = studio.Sync("", key == "pull" ? SyncDirection.WorkspaceToProject : SyncDirection.ProjectToWorkspace, false, null);
                cases.Add(key, new { result, events, calls = mapping.Status.Calls, nextCalls = next.Status.Calls });
                continue;
            }
            var reply = adapter.SyncVersionControlWorkspace(dryRun: false, changedOnly: key != "mixed");
            cases.Add(key, new { reply, status, preview, previewEvents, events, calls = mapping.Status.Calls,
                cleaned = temporary.All(folder => !Directory.Exists(folder)), externalPreserved = key == "race" && File.ReadAllText(path) == defaults });
        }
        return cases;
    }
}
#endif
