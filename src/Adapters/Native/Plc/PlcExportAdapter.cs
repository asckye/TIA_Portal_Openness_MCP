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
    // Export candidate only; the host owns plans and file publication.
    public sealed class PlcExportAdapter : IPlcExportAdapter
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

        public PlcExportAdapter(string release, Func<CandidateIdentity> identity, Func<PlcSoftware> software, Action requireOffline, bool overwriteSupported)
        { this.release = release; Identity = identity; Software = software; RequireOffline = requireOffline; OverwriteSupported = overwriteSupported; }

        private string Id(object value)
        {
            foreach (var pair in ids) if (object.Equals(pair.Key, value)) return pair.Value;
            if (ids.Count >= 16384) throw new InvalidOperationException("Export native identity budget exhausted.");
            string id = "plc-export-" + scopeId + "-" + ids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ids.Add(new KeyValuePair<object, string>(value, id)); objects[id] = value; return id;
        }
        public CandidateIdentity ReadIdentity() => Identity();
        private static string Space(string kind) => kind == "UDT" ? "type" : kind == "TagTable" ? "tag" : "block";
        private static string Key(string space, string group) => space + ":" + group;
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
            else throw new NotSupportedException("Object is outside ordinary block/type/tag export scope.");
            if (!object.Equals(((IEngineeringObject)value).Parent, parent)) throw new InvalidDataException("Native object parent mismatch.");
            return new PlcImportObject { Id = Id(value), Name = name, Kind = kind, Number = number, GroupPath = path };
        }
        public IReadOnlyList<PlcImportObject> ReadInventory()
        {
            RefreshGroups(); var result = new List<PlcImportObject>();
            void Add(object item, object parent, string path)
            {
                if (result.Count >= 4096) throw new InvalidOperationException("Complete export inventory exceeds 4096 rows.");
                result.Add(Describe(item, parent, path));
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
        public bool SupportsOverwrite(string tool) => OverwriteSupported && !tool.EndsWith("Documents", StringComparison.Ordinal);
        public IReadOnlyList<PlcExportObject> ReadObjects(string tool, PlcExportRequest request)
        {
            if (!new[] { "ExportPlcBlock", "ExportPlcType", "ExportPlcTagTable", "ExportPlcBlocks", "ExportPlcTypes", "ExportPlcBlockDocuments", "ExportPlcBlocksDocuments" }.Contains(tool, StringComparer.Ordinal))
                CandidatePrimitives.Unsupported(release, "export-entry");
            bool batch = tool == "ExportPlcBlocks" || tool == "ExportPlcTypes" || tool == "ExportPlcBlocksDocuments";
            bool documents = tool.EndsWith("Documents", StringComparison.Ordinal);
            if (documents && release != "20" && release != "21") CandidatePrimitives.Unsupported(release, "documents");
            string space = tool.Contains("Type") ? "type" : tool == "ExportPlcTagTable" ? "tag" : "block";
            TiaMcp.Adapters.PlcExchangePolicy.ObjectPath(request.GroupPath, true);
            if (request.PreservePath && (documents || batch) && !OverwriteSupported) CandidatePrimitives.Unsupported(release, "preservePath");
            if (request.PreservePath && tool == "ExportPlcTagTable") CandidatePrimitives.Unsupported(release, "preservePath");
            var inventory = ReadInventory();
            if (!groups.ContainsKey(Key(space, request.GroupPath))) CandidatePrimitives.NotFound(request.GroupPath);
            var rx = request.RegexName == "" ? null : new System.Text.RegularExpressions.Regex(request.RegexName,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var rows = inventory.Where(o => !o.Kind.StartsWith("group-", StringComparison.Ordinal) && Space(o.Kind) == space);
            string ObjectPath(PlcImportObject o) => o.GroupPath == "" ? o.Name : o.GroupPath + "/" + o.Name;
            if (batch) rows = rows.Where(o => o.GroupPath == request.GroupPath || request.Recursive && (request.GroupPath == "" || o.GroupPath.StartsWith(request.GroupPath + "/", StringComparison.Ordinal)))
                .Where(o => rx == null || rx.IsMatch(o.Name));
            else rows = rows.Where(o => ObjectPath(o) == request.ObjectPath);
            var selected = rows.OrderBy(ObjectPath, StringComparer.Ordinal).ToArray();
            if (!batch && selected.Length != 1) CandidatePrimitives.NotFound(request.ObjectPath);
            if (!OverwriteSupported && space == "tag" && selected.Any(o => o.GroupPath != "")) CandidatePrimitives.Unsupported(release, "nested-tag-tables");
            var result = new List<PlcExportObject>();
            foreach (var row in selected)
            {
                var value = objects[row.Id]; string language = ""; bool consistent;
                if (value is PlcBlock block)
                {
                    if (block.IsKnowHowProtected || !new[] { "OB", "FC", "FB", "GlobalDB", "InstanceDB" }.Contains(row.Kind)) CandidatePrimitives.Unsupported(release, "ordinary-blocks");
                    language = block.ProgrammingLanguage.ToString(); consistent = block.IsConsistent;
                    if (documents && language != "LAD" && language != "DB") CandidatePrimitives.Unsupported(release, "document-language");
                    // V14 SP1 SCL stays interface-only; never claim complete backup semantics.
                }
                else if (value is PlcType type) { consistent = type.IsConsistent; if (row.Kind != "UDT") CandidatePrimitives.Unsupported(release, "ordinary-types"); }
                else if (value is PlcTagTable) consistent = true;
                else throw new NotSupportedException("Object is outside ordinary export scope.");
                var observation = new PlcExportObject { Id = row.Id, Path = ObjectPath(row), Name = row.Name, Kind = row.Kind, Language = language, Consistent = consistent };
                if (value is PlcTagTable table)
                {
                    // Hash the readable declaration scope without issuing an extra native export.
                    using var bytes = new MemoryStream(); using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
                    {
                        int count = 0;
                        void Row(string kind, string name, string type, string data)
                        {
                            if (++count > 4096) CandidatePrimitives.Invalid("tag-declaration-budget");
                            writer.Write(kind); writer.Write(name); writer.Write(type); writer.Write(data);
                        }
                        foreach (var tag in table.Tags.OrderBy(t => t.Name, StringComparer.Ordinal)) Row("tag", tag.Name, tag.DataTypeName, tag.LogicalAddress);
                        foreach (var constant in table.UserConstants.OrderBy(c => c.Name, StringComparer.Ordinal)) Row("user", constant.Name, constant.DataTypeName, constant.Value);
                        foreach (var constant in table.SystemConstants.OrderBy(c => c.Name, StringComparer.Ordinal)) Row("system", constant.Name, constant.DataTypeName, constant.Value);
                    }
                    observation.ContentHash = CandidatePrimitives.ByteHash(bytes.ToArray());
                    observation.ContentObservation = "readable-tag-declarations-and-constants";
                }
                result.Add(observation);
            }
            return result;
        }
        public void BeforeExport(PlcExportObject item)
        {
            RequireOffline();
            if (!objects.ContainsKey(item.Id)) CandidatePrimitives.NotFound(item.Path);
        }
        public void Export(PlcExportObject item, string stagingPath, bool documents)
        {
            var value = objects[item.Id];
            if (documents)
            {
#if PLC_DOCUMENT_EXPORT
                if (!(value is PlcBlock block)) CandidatePrimitives.Unsupported(release, "documents");
                var result = PlcDocumentPrimitives.Export((PlcBlock)value, new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(stagingPath)), item.Name);
                if (result == null || PlcDocumentPrimitives.State(result) != DocumentResultState.Success) throw new InvalidDataException("Native document export did not succeed.");
#else
                CandidatePrimitives.Unsupported(release, "documents");
#endif
            }
            else if (value is PlcBlock block) block.Export(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(stagingPath)), ExportOptions.None);
            else if (value is PlcType type) type.Export(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(stagingPath)), ExportOptions.None);
            else if (value is PlcTagTable table) table.Export(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(stagingPath)), ExportOptions.None);
            else throw new NotSupportedException("Object is outside ordinary export scope.");
        }
    }
}
