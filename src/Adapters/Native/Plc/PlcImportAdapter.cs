using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Adapters.Native.Plc
{
    // New candidate only. Identity/offline checks and native-thread ownership remain with the host.
    public sealed class PlcImportAdapter : IPlcImportAdapter
    {
        private readonly List<KeyValuePair<object, string>> ids = new List<KeyValuePair<object, string>>();
        private readonly Dictionary<string, object> objects = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> groups = new Dictionary<string, object>(StringComparer.Ordinal);
        public Func<CandidateIdentity> Identity { private get; set; }
        public Func<PlcSoftware> Software { private get; set; }
        public Action RequireOffline { private get; set; }
        public bool OverwriteSupported { private get; set; }
        private readonly string release;
        private readonly string scopeId = Guid.NewGuid().ToString("N");

        public PlcImportAdapter(string release, Func<CandidateIdentity> identity, Func<PlcSoftware> software, Action requireOffline, bool overwriteSupported)
        { this.release = release; Identity = identity; Software = software; RequireOffline = requireOffline; OverwriteSupported = overwriteSupported; }

        private string Id(object value)
        {
            foreach (var pair in ids) if (object.Equals(pair.Key, value)) return pair.Value;
            if (ids.Count >= 16384) throw new InvalidOperationException("Import native identity budget exhausted.");
            string id = "plc-import-" + scopeId + "-" + ids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ids.Add(new KeyValuePair<object, string>(value, id)); objects[id] = value; return id;
        }
        public CandidateIdentity ReadIdentity() => Identity();
        public IReadOnlyList<PlcImportInput> ReadInputs(string key, string tool, PlcImportRequest request, IDictionary<string, Stream> locks)
        { if (key != release) CandidatePrimitives.Unsupported(key, "adapter-release"); return TiaOpenness.Shared.NativeInputPolicy.Read(tool == "ImportPlcProgramFromDirectory" ? "sourceDir" : tool.EndsWith("FromDirectory", StringComparison.Ordinal) ? "dir" : "importPath", () => CandidateImportFiles.Read(key, tool, request, locks)); }
        private static string Space(string kind) => kind == "UDT" ? "type" : kind == "TagTable" ? "tag" : "block";
        private static string Key(string space, string group) => space + ":" + group;
        public string TargetGroupIdentity(PlcImportObject target)
        {
            RefreshGroups();
            return groups.TryGetValue(Key(Space(target.Kind), target.GroupPath), out var group) ? Id(group) : "";
        }
        public bool SupportsOverwrite(PlcImportInput input) => OverwriteSupported;
        public void BeforeImport(PlcImportInput input)
        {
            RequireOffline();
            if (TargetGroupIdentity(input.Target) == "") CandidatePrimitives.NotFound(input.Target.GroupPath);
        }
        private void RefreshGroups()
        {
            groups.Clear(); var software = Software(); var seen = new List<object>();
            void Add(object value, string space, string path, int depth)
            {
                if (depth > 32 || groups.Count >= 4096 || seen.Any(o => object.Equals(o, value))) throw new InvalidOperationException("PLC group graph is incomplete or oversized.");
                seen.Add(value); groups.Add(Key(space, path), value);
            }
            void Blocks(PlcBlockGroup group, string path, int depth)
            {
                Add(group, "block", path, depth);
                foreach (var child in group.Groups) { if (!object.Equals(child.Parent, group)) throw new InvalidDataException("Block group parent mismatch."); Blocks(child, path == "" ? child.Name : path + "/" + child.Name, depth + 1); }
            }
            void Types(PlcTypeGroup group, string path, int depth)
            {
                Add(group, "type", path, depth);
                foreach (var child in group.Groups) { if (!object.Equals(child.Parent, group)) throw new InvalidDataException("Type group parent mismatch."); Types(child, path == "" ? child.Name : path + "/" + child.Name, depth + 1); }
            }
            void Tags(PlcTagTableGroup group, string path, int depth)
            {
                Add(group, "tag", path, depth);
                foreach (var child in group.Groups) { if (!object.Equals(child.Parent, group)) throw new InvalidDataException("Tag group parent mismatch."); Tags(child, path == "" ? child.Name : path + "/" + child.Name, depth + 1); }
            }
            Blocks(software.BlockGroup, "", 0); Types(software.TypeGroup, "", 0); Tags(software.TagTableGroup, "", 0);
        }
        private PlcImportObject Describe(object value, object parent, string path)
        {
            string name, kind; int? number = null;
            if (value is PlcBlock block)
            {
                name = block.Name; kind = block.GetType().Name;
                var raw = ((IEngineeringObject)block).GetAttribute("Number");
                if (raw == null || !int.TryParse(raw.ToString(), out int parsed) || parsed < 0) throw new InvalidDataException("Native block number is unavailable.");
                number = parsed;
            }
            else if (value is PlcType type) { name = type.Name; kind = type.GetType().Name == "PlcStruct" ? "UDT" : type.GetType().Name; }
            else if (value is PlcTagTable table) { name = table.Name; kind = "TagTable"; }
            else throw new NotSupportedException("Object is outside ordinary block/type/tag import scope.");
            if (!object.Equals(((IEngineeringObject)value).Parent, parent)) throw new InvalidDataException("Native object parent mismatch.");
            return new PlcImportObject { Id = Id(value), Name = name, Kind = kind, Number = number, GroupPath = path };
        }
        public IReadOnlyList<PlcImportObject> ReadInventory() => Inventory(true);
        public IReadOnlyList<PlcImportObject> ReadBatchInventory() => Inventory(false);
        public IReadOnlyList<PlcImportObject> ReadRecoveryTargets(string kind, string groupPath, IEnumerable<string> names)
        {
            // Single imports resolve only their destination group and named objects.
            // Unrelated groups and the complete batch-inventory budget do not apply.
            var software = Software();
            object group;
            if (kind == "UDT")
            {
                PlcTypeGroup current = software.TypeGroup;
                foreach (var segment in groupPath.Split('/').Where(s => s.Length > 0)) current = current.Groups.Find(Uri.UnescapeDataString(segment)) ?? throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Destination type group is unavailable.", "groupPath");
                group = current;
            }
            else if (kind == "TagTable")
            {
                PlcTagTableGroup current = software.TagTableGroup;
                foreach (var segment in groupPath.Split('/').Where(s => s.Length > 0)) current = current.Groups.Find(Uri.UnescapeDataString(segment)) ?? throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Destination tag group is unavailable.", "groupPath");
                group = current;
            }
            else
            {
                PlcBlockGroup current = software.BlockGroup;
                foreach (var segment in groupPath.Split('/').Where(s => s.Length > 0)) current = current.Groups.Find(Uri.UnescapeDataString(segment)) ?? throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Destination block group is unavailable.", "groupPath");
                group = current;
            }
            var result = new List<PlcImportObject>();
            foreach (var name in names.Distinct(StringComparer.Ordinal))
            {
                object? item = group is PlcTypeGroup types ? types.Types.Find(name) : group is PlcTagTableGroup tags ? tags.TagTables.Find(name) : ((PlcBlockGroup)group).Blocks.Find(name);
                if (item != null) result.Add(Describe(item, group, groupPath));
            }
            return result;
        }
        private IReadOnlyList<PlcImportObject> Inventory(bool content)
        {
            RefreshGroups(); var result = new List<PlcImportObject>();
            void Add(object item, object parent, string path)
            {
                if (result.Count >= 4096) throw new InvalidOperationException("Complete import inventory exceeds 4096 rows.");
                var row = Describe(item, parent, path); if (content) row.ContentHash = XmlReadback(item); result.Add(row);
            }
            foreach (var pair in groups.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string space = pair.Key.Substring(0, pair.Key.IndexOf(':')), path = pair.Key.Substring(pair.Key.IndexOf(':') + 1);
                result.Add(new PlcImportObject { Id = Id(pair.Value), Kind = "group-" + space, Name = path == "" ? "$root" : path, GroupPath = path });
                if (pair.Value is PlcBlockGroup blocks) foreach (var block in blocks.Blocks) Add(block, blocks, path);
                else if (pair.Value is PlcTypeGroup types) foreach (var type in types.Types) Add(type, types, path);
                else if (pair.Value is PlcTagTableGroup tags) foreach (var table in tags.TagTables) Add(table, tags, path);
            }
            return result;
        }
        public PlcImportObject Import(PlcImportInput input, bool overwrite)
        {
            var group = groups[Key(Space(input.Target.Kind), input.Target.GroupPath)]; object[] imported;
            var option = overwrite ? ImportOptions.Override : ImportOptions.None;
            if (input.Documents)
            {
#if PLC_DOCUMENT_EXPORT
                var result = ((PlcBlockGroup)group).Blocks.ImportFromDocuments(new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.GetDirectoryName(input.Path)!)), Path.GetFileNameWithoutExtension(input.Path), overwrite ? ImportDocumentOptions.Override : ImportDocumentOptions.None);
                var state = result == null ? (object?)null : result.State;
                if (!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(state))
                    throw new TiaMcp.Adapters.Contracts.NativeResultException(TiaMcp.Adapters.Contracts.NativeResultStates.Documents, state);
                imported = result.ImportedPlcBlocks.Take(2).Cast<object>().ToArray();
#else
                throw new NotSupportedException("Document import is not available on this exact release.");
#endif
            }
            else if (input.Target.Kind == "UDT") imported = ((PlcTypeGroup)group).Types.Import(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(input.Path)), option).Take(2).Cast<object>().ToArray();
            else if (input.Target.Kind == "TagTable") imported = ((PlcTagTableGroup)group).TagTables.Import(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(input.Path)), option).Take(2).Cast<object>().ToArray();
            else imported = ((PlcBlockGroup)group).Blocks.Import(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(input.Path)), option).Take(2).Cast<object>().ToArray();
            if (imported.Length != 1) throw new InvalidDataException("Native import must return exactly one reviewed object.");
            return Describe(imported[0], group, input.Target.GroupPath);
        }
        public string RecoveryBlocker(PlcImportObject item)
        {
            var value = objects[item.Id];
            return value is PlcBlock block ? TiaOpenness.Shared.NativeExportPolicy.ExportBlocker(block.IsConsistent, block.IsKnowHowProtected)
                : value is PlcType type ? TiaOpenness.Shared.NativeExportPolicy.ExportBlocker(type.IsConsistent) : "";
        }
        public void ExportRecovery(PlcImportObject item, FileInfo file)
        {
            TiaMcp.Adapters.PlcExportPublication.Publish(file, output =>
            {
                var value = objects[item.Id];
                if (value is PlcBlock block) block.Export(output, ExportOptions.None);
                else if (value is PlcType type) type.Export(output, ExportOptions.None);
                else ((PlcTagTable)value).Export(output, ExportOptions.None);
            });
        }
        private static string AuditDirectory()
        {
            string path = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "plc-import-readback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }
        private static string XmlReadback(object item)
        {
            string path = Path.Combine(AuditDirectory(), "readback.xml"); var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path));
            if (item is PlcBlock block) block.Export(file, ExportOptions.None);
            else if (item is PlcType type) type.Export(file, ExportOptions.None);
            else ((PlcTagTable)item).Export(file, ExportOptions.None);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return CandidateImportFiles.XmlHash(CandidatePrimitives.Read(stream));
        }
        public string ReadContent(PlcImportInput input, PlcImportObject imported)
        {
            var item = objects[imported.Id];
            if (!input.Documents) return XmlReadback(item);
#if PLC_DOCUMENT_EXPORT
            string path = AuditDirectory(), name = imported.Name;
            var result = ((PlcBlock)item).ExportAsDocuments(new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)), name);
            var state = result == null ? (object?)null : result.State;
                if (!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(state))
                    throw new TiaMcp.Adapters.Contracts.NativeResultException(TiaMcp.Adapters.Contracts.NativeResultStates.Documents, state);
            byte[] Read(string file) { using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); return CandidatePrimitives.Read(stream); }
            var code = Read(Path.Combine(path, name + ".s7dcl"));
            string resource = Path.Combine(path, name + ".s7res");
            return CandidateImportFiles.DocumentHash(code, File.Exists(resource) ? Read(resource) : null);
#else
            throw new NotSupportedException("Document readback is unavailable.");
#endif
        }
    }
}
