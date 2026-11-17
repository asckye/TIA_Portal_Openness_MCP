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

        internal static IEnumerable<(object Item, string Name, string Path)> LibraryScopeObjects(object root, string contents, string groups,
            string path = "", bool tags = false)
        {
            foreach (var item in PlcGroupOperations.Items(PlcGroupOperations.Get(root, contents)))
            {
                string name = PlcGroupOperations.Get(item, "Name").ToString()!;
                string itemPath = path + ObjectPathEncoding.EncodeSegment(name);
                if (!tags) yield return (item, name, itemPath);
                else foreach (string symbols in new[] { "Tags", "UserConstants" })
                    foreach (var symbol in PlcGroupOperations.Items(PlcGroupOperations.Get(item, symbols)))
                    {
                        string symbolName = PlcGroupOperations.Get(symbol, "Name").ToString()!;
                        yield return (symbol, symbolName, itemPath + "/" + ObjectPathEncoding.EncodeSegment(symbolName));
                    }
            }
            foreach (var child in PlcGroupOperations.Items(PlcGroupOperations.Get(root, groups)))
                foreach (var row in LibraryScopeObjects(child, contents, groups,
                    path + ObjectPathEncoding.EncodeSegment(PlcGroupOperations.Get(child, "Name").ToString()!) + "/", tags)) yield return row;
        }

        private static Dictionary<string, (object Item, string Name, string Path)[]> LibraryNameSnapshot(
            IEnumerable<(object Item, string Name, string Path)> rows)
            => rows.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        internal static string InstantiateLibraryContentsCore(string kind, string software, string groupPath, string[] names, string copyMode, bool dryRun,
            Dictionary<string, object?> meta, Func<object, object> create, Func<bool, object> collection,
            Func<IEnumerable<(object Item, string Name, string Path)>> scope, Func<IDisposable> acquire, Func<IDisposable> undo)
        {
            string targetPath = PlcObjectPath.Format(LibraryTargetFolderParts(groupPath));
            string Path(string name) => targetPath + (targetPath == "" ? "" : "/") + ObjectPathEncoding.EncodeSegment(name);
            string Apply(bool writing)
            {
                using var access = writing ? acquire() : null;
                var destination = collection(writing);
                var before = LibraryNameSnapshot(scope());
                var targets = names.Select(name => {
                    before.TryGetValue(name, out var matches);
                    return new Dictionary<string, object?> {
                        ["name"] = name, ["path"] = Path(name), ["exists"] = matches != null,
                        ["clashes"] = (matches ?? Array.Empty<(object Item, string Name, string Path)>())
                            .Select(row => (object?)new Dictionary<string, object?> {
                                ["path"] = row.Path, ["inTargetContainer"] = string.Equals(row.Path, Path(name), StringComparison.OrdinalIgnoreCase)
                            }).ToList()
                    };
                }).ToArray();
                meta["targetKind"] = kind; meta["targetSoftwarePath"] = software; meta["targetGroupPath"] = groupPath; meta["copyMode"] = copyMode;
                meta["targets"] = targets.Cast<object?>().ToList();
                meta["scope"] = kind == "blockGroup"
                    ? "Destination parent group names only; blocks inside the copied group are not checked against PLC-wide names."
                    : "Names in the selected PLC software/project/Classic HMI scope.";
                bool clashes = targets.Any(row => (bool)row["exists"]!);
                if ((copyMode == "" || copyMode == "ThrowIfExists") && clashes)
                    throw new PlcSoftwareException("ALREADY_EXISTS", "A content name already exists in the target software/project; inspect targets.clashes.");
                if (copyMode == "Replace" && targets.Any(row => ((List<object?>)row["clashes"]!)
                    .Cast<Dictionary<string, object?>>().Any(clash => !(bool)clash["inTargetContainer"]!)))
                    throw new PlcSoftwareException("ALREADY_EXISTS",
                            "Replace cannot replace an object outside the destination container; inspect targets.clashes.");
                if (!writing) return "Library instantiation preview; source, destination and name-uniqueness scope resolved; no changes.";
                bool observed = false;
                // A throwing/unreadable CreateFrom must retain an explicit recovery task even when its results cannot be enumerated.
                EngineeringWriteJournal.Record("Observe library instantiation results", () => {
                    if (!observed) throw new NotSupportedException("Created paths could not be observed; inspect the target software/project before cleanup.");
                });
                foreach (string name in names)
                    if (copyMode == "Replace" && before.ContainsKey(name))
                        EngineeringWriteJournal.Unrestored("Restore replaced library instance " + Path(name));
                meta["mayHaveChanged"] = true; meta["instantiationAttempted"] = true;
                Dictionary<string, (object Item, string Name, string Path)[]> after = new(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, (object Item, string Name, string Path)[]> created = new(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, (object Item, string Name, string Path)[]>? rollback = null;
                try { create(destination); meta["apiCallSuccess"] = true; }
                finally
                {
                    after = LibraryNameSnapshot(scope());
                    created = after.Where(pair => !before.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase);
                    observed = true;
                    meta["createdNames"] = created.Keys.Cast<object?>().ToList();
                    meta["createdPaths"] = created.Values.SelectMany(rows => rows).Select(row => (object?)row.Path).ToList();
                    foreach (var row in created.Values.SelectMany(rows => rows))
                    {
                        bool finding = false;
                        object? FindCreated()
                        {
                            // Cache each fresh rollback navigation as a name-indexed snapshot, including the absence read after Delete.
                            if (rollback == null || finding) rollback = LibraryNameSnapshot(scope());
                            finding = !finding;
                            return rollback.TryGetValue(row.Name, out var matches)
                                ? matches.Where(match => string.Equals(match.Path, row.Path, StringComparison.OrdinalIgnoreCase))
                                    .Select(match => match.Item).SingleOrDefault() : null;
                        }
                        EngineeringWriteJournal.Creating(FindCreated, row.Path);
                    }
                }
                var expected = copyMode == "Rename" ? created.Keys.ToArray() : names;
                var readback = new List<object?>(); meta["createdObjects"] = readback;
                foreach (string name in expected)
                    if (after.TryGetValue(name, out var matches))
                    {
                        var found = matches.Where(row => string.Equals(row.Path, Path(name), StringComparison.OrdinalIgnoreCase)).SingleOrDefault();
                        if (found.Item != null) readback.Add(HardwareScalarEvidence.Read(found.Item));
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
