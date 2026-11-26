#if STUDIO_VCI_MODERN
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.VersionControl;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Native.Vci;
#if TIA_ADAPTER_INTERNAL_VERSIONING
using TiaMcp.Adapters.Contracts.Studio;
#else
using TiaOpenness.Contracts.Models;
#endif

namespace TiaOpenness.Openness;

internal sealed partial class OpennessVersionControl
{
    internal static SyncItem? RestoreSdMapping(MappedObject mapped, string rootPath, string label, bool dryRun, out bool connectionLost)
    {
        var item = new SyncItem { Name = label, Outcome = "refused" };
        PlcSdBlockSnapshot? plan = null;
        bool dispatched = false;
        connectionLost = false;
        try
        {
            var target = mapped.EngineeringObject ?? throw new InvalidOperationException("Cannot establish the exact mapped PLC block or other target.");
            if (target is PlcType or PlcTypeGroup or PlcTagTable or PlcTagTableGroup || IsHmiObject(target)) return null;
            if (VciExportPolicy.IsKnownNonSd(VersionControlPrimitives.Format(mapped))) return null;
            if (target is not PlcBlock block || block.Parent is not PlcBlockGroup group)
                throw new InvalidOperationException("Cannot establish the exact mapped PLC block and group for SIMATIC SD restore.");

            string name = block.Name;
            var file = DeclarationFile(mapped, rootPath);
            var files = new SdDeclarationFiles();
            // The VCI export stem can be an alias. Admission binds the declaration to the project block name.
            plan = new PlcSdBlockSnapshot(files.Read(file), name, () => block, true, () => ExistsInAnotherGroup(group, name),
                NativeRefusalProbe.UncertainConnection);
            item.NotCompared = ((string[])plan.Evidence["notCompared"]!).ToList();
            plan.RequireAdmission();
            if (dryRun) { item.Outcome = "would sync WorkspaceToProject"; return item; }

            plan.Backup();
            files.RequireUnchanged(file);
            dispatched = true;
            VersionControlPrimitives.Synchronize(mapped, SynchronizationMode.WorkspaceToProject);
            VerifyRestore(mapped, plan, () => group.Blocks.Find(name), item);
            if (item.Error != null) item.Error += RecoveryNote(plan);
        }
        catch (Exception error)
        {
            connectionLost = NativeRefusalProbe.UncertainConnection(error) && !IsDisposedMapping(mapped, error);
            item.Outcome = dispatched ? "unknown" : "refused";
            item.Error = (dispatched ? "Restore may have changed the project. " : "") + error.Message + RecoveryNote(plan);
        }
        return item;
    }

    private static bool IsHmiObject(object target)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var name = type.FullName;
            if (name != null && (name.StartsWith("Siemens.Engineering.Hmi.", StringComparison.Ordinal)
                || name.StartsWith("Siemens.Engineering.HmiUnified.", StringComparison.Ordinal))) return true;
        }
        return false;
    }

    private static bool IsDisposedMapping(MappedObject mapped, Exception error) => NativeRefusalProbe.DisposedTarget(mapped, error);

    internal static CompareState ReadMappedState(MappedObject mapped, out string? error)
    {
        error = null;
        try { return VersionControlPrimitives.State(VersionControlPrimitives.ReadStatus(mapped)); }
        catch (Exception disposed) when (IsDisposedMapping(mapped, disposed))
        {
            error = NativeFailureText.BoundedDiagnostic(disposed);
            return CompareState.Unknown;
        }
    }

    private static void VerifyRestore(MappedObject mapped, PlcSdBlockSnapshot plan, Func<PlcBlock?> find, SyncItem item)
    {
        try
        {
            bool preserved = plan.Verify(find);
            item.NotCompared = ((string[])plan.Evidence["notCompared"]!).ToList();
            item.Outcome = preserved ? "synchronized" : "failed";
            if (!preserved)
            {
                var differences = (List<Dictionary<string, object?>>)plan.Evidence["propertyDifferences"]!;
                item.Error = SdBlockOverridePolicy.Failure + ": " + string.Join(", ", differences.Select(DescribeDifference));
            }
        }
        catch (Exception error) when (!NativeRefusalProbe.UncertainConnection(error))
        {
            item.Outcome = "unknown";
            item.Error = "Cannot verify restored block properties: " + error.Message;
        }

        // Read the final VCI state after Verify has restored any unrequested number change.
        string diagnostic;
        try
        {
            var state = VersionControlPrimitives.State(VersionControlPrimitives.ReadStatus(mapped));
            if (state == CompareState.Equal) return;
            // VCI can only determine the outcome if the block properties were successfully verified.
            if (item.Outcome == "synchronized")
                item.Outcome = state is CompareState.Unequal or CompareState.WorkspaceFileMissing ? "failed" : "unknown";
            diagnostic = $"Restore not-verified: mapped object state is {state} after synchronization.";
        }
        catch (Exception error)
        {
            if (item.Outcome != "failed") item.Outcome = "unknown";
            diagnostic = "Restore not-verified: cannot read mapping status after synchronization: " + error.Message;
        }
        item.Error = item.Error == null ? diagnostic : item.Error + " " + diagnostic;
    }

    private static string DescribeDifference(Dictionary<string, object?> difference)
    {
        var expected = difference.TryGetValue("expected", out var requested) ? requested : difference["before"];
        string message = $"{difference["name"]} (expected {expected ?? "<no value>"}; read {difference["after"] ?? "<no value>"})";
        return difference.TryGetValue("restoreError", out var error) ? message + ": " + error : message;
    }

    private static string RecoveryNote(PlcSdBlockSnapshot? plan)
    {
        if (plan == null) return "";
        if (plan.Evidence.TryGetValue("recoveryDirectory", out var directory) && directory is string path) return " Recovery: " + path;
        return plan.Evidence.TryGetValue("backupWarning", out var warning) ? " " + warning : "";
    }

    private static FileInfo DeclarationFile(MappedObject mapped, string rootPath)
    {
        string stem = VersionControlPrimitives.FileName(mapped);
        if (string.IsNullOrWhiteSpace(stem) || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Cannot establish the mapped SD declaration filename.");
        var directory = VersionControlPrimitives.Directory(mapped) ?? throw new InvalidOperationException("Mapped SD directory unavailable.");
        if (!Path.IsPathRooted(rootPath)) throw new InvalidOperationException("Mapped workspace root unavailable.");
        string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = directory.ToString();
        // ExportObject accepts a single leading separator for a workspace-relative directory.
        if (Path.GetPathRoot(path) is "\\" or "/") path = path.Substring(1);
        var file = new FileInfo(Path.Combine(root, path, stem + ".s7dcl"));
        if (!file.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mapped SD file is outside the workspace.");
        return file;
    }

    private static bool ExistsInAnotherGroup(PlcBlockGroup target, string name)
    {
        var root = target;
        while (root.Parent is PlcBlockGroup parent) root = parent;
        var pending = new Stack<PlcBlockGroup>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var group = pending.Pop();
            if (!Equals(group, target) && group.Blocks.Find(name) != null) return true;
            foreach (var child in group.Groups) pending.Push(child);
        }
        return false;
    }
}
#endif
