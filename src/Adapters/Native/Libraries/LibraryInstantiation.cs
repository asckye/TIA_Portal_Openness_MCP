using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.Library.Types;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal static void RequireCommittedLibraryVersion(LibraryTypeVersionState state)
        {
            if (state != LibraryTypeVersionState.Committed)
                throw new NotSupportedException("Instantiation requires a released library type version.");
        }

        internal static string[] LibraryTargetFolderParts(string path) => PlcObjectPath.Parts(path, true, "targetGroupPath");

        internal static IEnumerable<(object Item, string Path)> LibraryScopeObjects(object root, string contents, string groups,
            string path = "", bool tags = false)
        {
            foreach (var item in PlcGroupOperations.Items(PlcGroupOperations.Get(root, contents)))
            {
                string itemPath = path + ObjectPathEncoding.EncodeSegment(PlcGroupOperations.Get(item, "Name").ToString()!);
                if (!tags) yield return (item, itemPath);
                else foreach (var tag in PlcGroupOperations.Items(PlcGroupOperations.Get(item, "Tags")))
                    yield return (tag, itemPath + "/" + ObjectPathEncoding.EncodeSegment(PlcGroupOperations.Get(tag, "Name").ToString()!));
            }
            foreach (var child in PlcGroupOperations.Items(PlcGroupOperations.Get(root, groups)))
                foreach (var row in LibraryScopeObjects(child, contents, groups,
                    path + ObjectPathEncoding.EncodeSegment(PlcGroupOperations.Get(child, "Name").ToString()!) + "/", tags)) yield return row;
        }

        internal static string InstantiateLibraryContentsCore(string kind, string software, string groupPath, string[] names, string copyMode, bool dryRun,
            Dictionary<string, object?> meta, Func<object, object> create, Func<bool, object> collection,
            Func<IEnumerable<(object Item, string Path)>> scope, Func<IDisposable> acquire, Func<IDisposable> undo)
        {
            string Name(object item) => PlcGroupOperations.Get(item, "Name").ToString()!;
            string targetPath = PlcObjectPath.Format(LibraryTargetFolderParts(groupPath));
            string Path(string name) => targetPath + (targetPath == "" ? "" : "/") + ObjectPathEncoding.EncodeSegment(name);
            string Apply(bool writing)
            {
                using var access = writing ? acquire() : null;
                var destination = collection(writing);
                var before = scope().ToArray();
                var beforePaths = new HashSet<string>(before.Select(row => row.Path), StringComparer.OrdinalIgnoreCase);
                var beforeNames = new HashSet<string>(before.Select(row => Name(row.Item)), StringComparer.OrdinalIgnoreCase);
                var targets = names.Select(name => new Dictionary<string, object?> {
                    ["name"] = name, ["path"] = Path(name),
                    ["exists"] = before.Any(row => string.Equals(Name(row.Item), name, StringComparison.OrdinalIgnoreCase)),
                    ["clashes"] = before.Where(row => string.Equals(Name(row.Item), name, StringComparison.OrdinalIgnoreCase))
                        .Select(row => (object?)new Dictionary<string, object?> {
                            ["path"] = row.Path, ["inTargetContainer"] = string.Equals(row.Path, Path(name), StringComparison.OrdinalIgnoreCase)
                        }).ToList()
                }).ToArray();
                meta["targetKind"] = kind; meta["targetSoftwarePath"] = software; meta["targetGroupPath"] = groupPath; meta["copyMode"] = copyMode;
                meta["targets"] = targets.Cast<object?>().ToList();
                bool clashes = targets.Any(row => (bool)row["exists"]!);
                if ((copyMode == "" || copyMode == "ThrowIfExists") && clashes)
                    throw new PlcSoftwareException("ALREADY_EXISTS", "A content name already exists in the target software/project; inspect targets.clashes.");
                if (copyMode == "Replace" && targets.Any(row => ((List<object?>)row["clashes"]!)
                    .Cast<Dictionary<string, object?>>().Any(clash => !(bool)clash["inTargetContainer"]!)))
                    throw new PlcSoftwareException("ALREADY_EXISTS",
                            "Replace cannot replace an object outside the destination container; inspect targets.clashes.");
                if (!writing) return "Library instantiation preview; source, destination and name-uniqueness scope resolved; no changes.";
                (object Item, string Path)[] created = Array.Empty<(object Item, string Path)>();
                bool observed = false;
                void RegisterCreated(int slot, string label)
                {
                    object? FindCreated()
                    {
                        if (!observed)
                            throw new NotSupportedException("Created paths could not be observed; inspect the target software/project before cleanup.");
                        return slot >= created.Length ? null : scope()
                            .Where(row => string.Equals(row.Path, created[slot].Path, StringComparison.OrdinalIgnoreCase))
                            .Select(row => row.Item).SingleOrDefault();
                    }
                    EngineeringWriteJournal.Creating(FindCreated, label);
                }
                for (int index = 0; index < names.Length; index++)
                {
                    RegisterCreated(index, Path(names[index]));
                    if (copyMode == "Replace" && beforePaths.Contains(Path(names[index])))
                        EngineeringWriteJournal.Unrestored("Restore replaced library instance " + Path(names[index]));
                }
                meta["mayHaveChanged"] = true;
                meta["instantiationAttempted"] = true;
                try { create(destination); meta["apiCallSuccess"] = true; }
                finally
                {
                    // Observe across the same uniqueness scope before releasing edit access, including a throwing CreateFrom.
                    created = scope().Where(row => !beforeNames.Contains(Name(row.Item))).ToArray();
                    observed = true;
                    meta["createdNames"] = created.Select(row => (object?)Name(row.Item)).ToList();
                    meta["createdPaths"] = created.Select(row => (object?)row.Path).ToList();
                    for (int index = names.Length; index < created.Length; index++) RegisterCreated(index, created[index].Path);
                }
                var expected = copyMode == "Rename" ? created.Select(row => Name(row.Item)).ToArray() : names;
                var readback = new List<object?>(); meta["createdObjects"] = readback;
                foreach (string name in expected)
                {
                    var found = PlcGroupOperations.Find(collection(false), name);
                    if (found != null) readback.Add(HardwareScalarEvidence.Read(found));
                }
                bool complete = expected.Length == names.Length && readback.Count == names.Length;
                meta["readbackComplete"] = complete; meta["operationSuccess"] = complete;
                if (!complete) meta["failed"] = Math.Max(1, names.Length - readback.Count);
                return complete ? "Library contents instantiated and read back; project not saved or compiled."
                    : "Library instantiation readback failed; rolling back new objects. Inspect createdPaths and rollback evidence; replacements require " +
                        "cleanup.";
            }
            return dryRun ? Apply(false) : EngineeringWriteJournal.Run(meta, () => Apply(true), undo);
        }
    }
}
