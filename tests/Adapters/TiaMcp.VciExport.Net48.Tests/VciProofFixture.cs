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
        foreach (var key in new[] { "import", "disposed", "wrong-write", "invalid-output", "changed-again", "final-proxy",
            "studio-unknown", "studio-final-abort", "studio-final-disposed", "missing-output", "cleanup", "stop", "inconsistent" })
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
            bool afterSync = false;
            var first = Add("Motor", (file, _) =>
            {
                if (key == "disposed") throw new EngineeringObjectDisposedException("Disposed PLC block");
                if (key == "cleanup")
                {
                    locked = file.Open(FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    scratch = file.Directory;
                    throw new EngineeringUserAbortException("Original abort");
                }
                if (afterSync && key == "changed-again") File.WriteAllText(Path.Combine(root.FullName, "Motor.xml"), xml);
                if (afterSync && key == "studio-final-abort") throw new EngineeringUserAbortException("Final comparison aborted");
                if (afterSync && key is "final-proxy" or "studio-final-disposed") throw new EngineeringObjectDisposedException("Final export proxy disposed");
                File.WriteAllText(file.FullName, key is "wrong-write" or "invalid-output" or "changed-again" or "final-proxy"
                    or "studio-unknown" or "studio-final-abort" or "studio-final-disposed" or "missing-output" or "inconsistent" ? changed : projectXml);
            });
            first.Status.AcceptEqual = true;
            if (key is "wrong-write" or "invalid-output" or "changed-again" or "final-proxy" or "studio-final-abort")
                first.Status.OnSynchronized = () =>
                {
                    afterSync = true;
                    File.WriteAllText(Path.Combine(root.FullName, "Motor.xml"), key == "invalid-output" ? "TIA file form" : xml.Replace("MCP_RC_FC", "Other"));
                };
            if (key == "missing-output")
                first.Status.OnSynchronized = () => File.Delete(Path.Combine(root.FullName, "Motor.xml"));
            if (key == "studio-unknown")
                first.Status.OnSynchronized = () => throw new EngineeringUserAbortException("Unconfirmed write");
            if (key == "inconsistent")
                first.Status.OnSynchronized = () => throw new EngineeringTargetInvocationException("Motor is not consistent. password=fixture-password");
            var next = Add("Next", (file, _) => File.WriteAllText(file.FullName, xml));
            if (key == "studio-final-disposed")
            {
                first.Status.OnSynchronized = () => afterSync = true;
                ((PlcBlock)next.LinkedProjectObject).Write = (file, _) => File.WriteAllText(file.FullName, changed);
                next.Status.AcceptEqual = true;
                next.Status.OnSynchronized = () => File.WriteAllText(Path.Combine(root.FullName, "Next.xml"), changed);
            }
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
                else if (key is "studio-final-abort" or "studio-final-disposed")
                    cases[key] = new OpennessVersionControl(() => project).Sync("", SyncDirection.ProjectToWorkspace, false, null);
                else if (key == "studio-unknown")
                {
                    var done = Add("Done", (file, _) => File.WriteAllText(file.FullName, xml));
                    done.Status.State = CompareState.Unequal;
                    var result = new OpennessVersionControl(() => project).Sync("", SyncDirection.ProjectToWorkspace, false, null);
                    cases[key] = result.Items.Single(row => row.Outcome == "unknown").Error;
                    cases["studio-rows"] = result;
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
        var empty = new Workspace(parent.CreateSubdirectory("pending-summary"));
        cases["studio-pending"] = new OpennessVersionControl(() => new Project()).SummarizePendingRow(empty);
        var folder = parent.CreateSubdirectory("repeated-comparison");
        string path = Path.Combine(folder.FullName, "Motor.xml");
        File.WriteAllText(path, xml);
        var block = new PlcBlock { IsConsistent = false, Write = (_, _) => throw new EngineeringTargetInvocationException("Export refused") };
        var mapped = new WorkspaceMapping(new Workspace(folder), "Motor.xml", block);
        var proof = new VciStoredEqual(folder.FullName, mapped, "Motor", "Refresh refused");
        VciStoredEqual.PrepareAll(new[] { proof }, false);
        bool initial = proof.Inconsistent;
        File.WriteAllText(path, "<Document");
        proof.CompareAfterImports(() => mapped, false);
        bool unreadable = proof.Inconsistent;
        File.WriteAllText(path, xml);
        block.Write = (output, _) => File.WriteAllText(output.FullName, "<Document");
        proof.CompareAfterImports(() => mapped, false);
        cases["repeated-comparison"] = new { initial, unreadable, final = proof.Inconsistent, proof.Proof.ExportSucceeded, proof.Message };
        cases["final-interrupted"] = FinalComparisonFailure(parent.CreateSubdirectory("final-interrupted"), xml, interrupted: true);
        cases["final-refused"] = FinalComparisonFailure(parent.CreateSubdirectory("final-refused"), xml, interrupted: false);
        return cases;
    }

    private static object FinalComparisonFailure(DirectoryInfo root, string xml, bool interrupted)
    {
        var ws = new Workspace(root);
        var project = new Project();
        project.Vci.WorkspaceGroup.Workspaces.Add(ws);
        File.WriteAllText(Path.Combine(root.FullName, "Motor.xml"), xml);
        bool afterSync = false;
        var block = new PlcBlock { IsConsistent = false, Write = (_, _) =>
        {
            if (afterSync && interrupted) throw new EndOfStreamException("Final comparison connection lost");
            throw new EngineeringTargetInvocationException("Comparison export refused");
        } };
        var mapped = new WorkspaceMapping(ws, "Motor.xml", block);
        mapped.Status.State = CompareState.Equal;
        mapped.Status.AcceptEqual = true;
        mapped.Status.OnSynchronized = () => afterSync = true;
        ws.Mappings.Add(mapped);

        var proof = new VciStoredEqual(root.FullName, mapped, "Motor", "Refresh refused");
        VciStoredEqual.PrepareAll(new[] { proof }, false);
        proof.Finish(() => mapped, SynchronizationMode.ProjectToWorkspace, false);
        afterSync = false;
        var reply = new VersionControlAdapter(() => project).SyncVersionControlWorkspace(dryRun: false);
        return new { proof.Inconsistent, proof.ComparisonInterrupted, reply };
    }

    internal static Dictionary<string, object> Run(DirectoryInfo parent)
    {
        var cases = new Dictionary<string, object>();
        string xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MCP_RC_FC.xml"));
        string edited = xml.Replace("<Name>MCP_RC_FC</Name>", "<Name>Edited</Name>");
        string defaults = xml.Replace("<Name>MCP_RC_FC</Name>", "<IsFailsafeCompliant>false</IsFailsafeCompliant><Name>MCP_RC_FC</Name>");
        foreach (string key in new[] { "none", "defaults", "stale", "refused", "missing", "export-refused", "no-write", "new-match",
            "external", "bad-xml", "oversize", "compare-aborted", "rc7-inconsistent",
            "inconsistent-no-write", "inconsistent-final-disposed", "inconsistent-final-different",
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
            bool inconsistentNoWrite = key.StartsWith("inconsistent-", StringComparison.Ordinal);
            block.IsConsistent = key != "rc7-inconsistent" && !inconsistentNoWrite;
            block.Write = (file, options) =>
            {
                events.Add("export:" + options);
                if (file.FullName.StartsWith(root.FullName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Workspace export");
                temporary.Add(file.DirectoryName!);
                if (key == "compare-aborted") throw new EngineeringUserAbortException("Aborted before writing");
                // V18 row 73 lost the comparison diagnostic. Row 74 recorded the export refusal for this FB.
                bool afterSync = events.Contains("sync:Motor");
                if (afterSync && key == "inconsistent-final-disposed") throw new EngineeringObjectDisposedException("Final export proxy disposed");
                if (key == "rc7-inconsistent" || inconsistentNoWrite && !(afterSync && key == "inconsistent-final-different"))
                    throw new EngineeringTargetInvocationException("Compile the inconsistent block before export. Inconsistent objects: MCP_RC_FB.");
                if (key == "export-refused") throw new EngineeringTargetInvocationException("Export refused");
                File.WriteAllText(file.FullName, key == "defaults" && options == ExportOptions.WithDefaults ? defaults : projectXml);
            };
            mapping.Status.AcceptEqual = key is not ("refused" or "mixed" or "export-refused" or "bad-xml" or "rc7-inconsistent");
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
                if (key is not ("no-write" or "new-match") && !inconsistentNoWrite) File.WriteAllText(path, projectXml);
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
            var applyEvents = events.ToArray();
            var applyCalls = mapping.Status.Calls.ToArray();
            TiaMcp.Adapters.Contracts.VersionControlResult? mixedRecovery = null;
            if (key == "rc7-inconsistent")
            {
                var other = new WorkspaceMapping(workspace, "Other.xml", new PlcBlock { Name = "Other",
                    Write = (file, _) => File.WriteAllText(file.FullName, edited) });
                other.Status.State = CompareState.Equal;
                workspace.Mappings.Add(other);
                File.WriteAllText(Path.Combine(root.FullName, "Other.xml"), xml);
                mixedRecovery = adapter.SyncVersionControlWorkspace(dryRun: false);
            }
            cases.Add(key, new { reply, status, preview, previewEvents, events = applyEvents, calls = applyCalls, mixedRecovery,
                block.ConsistencyReads, cleaned = temporary.All(folder => !Directory.Exists(folder)),
                externalPreserved = key == "race" && File.ReadAllText(path) == defaults });
        }
        return cases;
    }
}

namespace TiaOpenness.Openness
{
    internal sealed partial class OpennessVersionControl
    {
        internal SyncResult SummarizePendingRow(Workspace workspace)
        {
            var result = new SyncResult { Direction = SyncDirection.ProjectToWorkspace };
            AddNotAttempted(result, "Next");
            SyncMappings(workspace, SynchronizationMode.ProjectToWorkspace, result, null, false);
            return result;
        }
    }
}
#endif
