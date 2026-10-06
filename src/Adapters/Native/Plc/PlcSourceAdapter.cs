using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.ExternalSources;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed class PlcSourceAdapter : ISourceAdapter
    {
        private readonly Dictionary<string, object> native = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> declarations = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, PlcExternalSourceGroup> groups = new Dictionary<string, PlcExternalSourceGroup>(StringComparer.Ordinal);
        private readonly Dictionary<string, CandidateFile> inputs = new Dictionary<string, CandidateFile>(StringComparer.Ordinal);
        private readonly string scope = Guid.NewGuid().ToString("N");
        public Func<CandidateIdentity> Identity { private get; set; }
        public Func<PlcSoftware> Software { private get; set; }
        public Func<string> SoftwarePath { private get; set; }
        public Action RequireOffline { private get; set; }
        private readonly bool rootOnly;
        public PlcSourceAdapter(Func<CandidateIdentity> identity, Func<PlcSoftware> software, Func<string> path, Action offline, bool rootOnly = false)
        { Identity = identity; Software = software; SoftwarePath = path; RequireOffline = offline; this.rootOnly = rootOnly; }
        private string Id(object value)
        {
            foreach (var pair in native) if (object.Equals(pair.Value, value)) return pair.Key;
            if (native.Count >= 16384) CandidatePrimitives.Fail("precondition", "source-identity-budget");
            string id = scope + ":" + native.Count.ToString(System.Globalization.CultureInfo.InvariantCulture); native.Add(id, value); return id;
        }
        public SourceObservation Observe()
        {
            var identity = Identity(); var plc = Software(); var sources = new List<SourceRow>(); groups.Clear();
            void Group(PlcExternalSourceGroup group, string path, int depth)
            {
                if (depth > 32 || groups.Count >= 4096 || groups.Values.Any(g => object.Equals(g, group))) CandidatePrimitives.Fail("precondition", "source-group-inventory");
                groups.Add(path, group);
                foreach (var s in group.ExternalSources)
                {
                    if (!object.Equals(s.Parent, group)) CandidatePrimitives.Fail("precondition", "source-parent");
                    string id = Id(s); sources.Add(new SourceRow { Id = id, Name = s.Name, GroupPath = path, FilePath = inputs.TryGetValue(id, out var input) ? input.Path : "", ContentHash = input?.Sha256 ?? "", Declarations = declarations.TryGetValue(id, out var names) ? names : Array.Empty<string>() });
                }
                if (!rootOnly) foreach (var g in group.Groups) Group(g, path == "" ? Uri.EscapeDataString(g.Name) : path + "/" + Uri.EscapeDataString(g.Name), depth + 1);
            }
            Group(plc.ExternalSourceGroup, "", 0);
            var objects = new List<string>();
            void Blocks(PlcBlockGroup g, int depth)
            { if (depth > 32 || objects.Count > 4096) CandidatePrimitives.Fail("precondition", "block-inventory"); foreach (var b in g.Blocks) objects.Add("block:" + b.Name); foreach (var child in g.Groups) Blocks(child, depth + 1); }
            void Types(PlcTypeGroup g, int depth)
            { if (depth > 32 || objects.Count > 4096) CandidatePrimitives.Fail("precondition", "type-inventory"); foreach (var t in g.Types) objects.Add("type:" + t.Name); foreach (var child in g.Groups) Types(child, depth + 1); }
            Blocks(plc.BlockGroup, 0); Types(plc.TypeGroup, 0);
            var result = new SourceObservation { Identity = identity, PlcId = Id(plc), SoftwarePath = SoftwarePath(), Groups = groups.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(), GroupIds = groups.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Id(g.Value)).ToArray(), Sources = sources.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray(), Objects = objects.OrderBy(x => x, StringComparer.Ordinal).ToArray() };
            CandidateExecution.ValidateSourceObservation(result); return result;
        }
        public void BeforeAction(SourceItem item)
        {
            RequireOffline();
            if (item.Action != "import")
            {
                var observation = Observe(); var row = CandidateExecution.SourceTarget(observation, item);
                if (row == null) CandidatePrimitives.NotFound(item.SourceName);
                Exact(row!);
            }
        }
        public SourceRow Import(SourceItem item, string[] names)
        {
            var group = groups[item.GroupPath];
            var source = group.ExternalSources.CreateFromFile(item.SourceName, item.FilePath);
            if (source == null || source.Name != item.SourceName || !object.Equals(source.Parent, group)) throw new InvalidDataException("Source import identity differs.");
            string id = Id(source); declarations.Add(id, names); var bytes = File.ReadAllBytes(item.FilePath);
            inputs.Add(id, new CandidateFile { Path = item.FilePath, Exists = true, ByteLength = bytes.Length, Sha256 = CandidatePrimitives.ByteHash(bytes) });
            return new SourceRow { Id = id, Name = source.Name, GroupPath = item.GroupPath, FilePath = item.FilePath, ContentHash = inputs[id].Sha256!, Declarations = names };
        }
        private PlcExternalSource Exact(SourceRow row)
        {
            var group = groups[row.GroupPath];
            var source = group.ExternalSources.SingleOrDefault(s => s.Name == row.Name);
            if (source == null || !object.Equals(source, native[row.Id]) || !object.Equals(source.Parent, group)) CandidatePrimitives.NotFound(row.Name);
            return source!;
        }
        public string[]? Generate(SourceRow row)
        {
            var source = (PlcExternalSource)native[row.Id];
#if PLC_SOURCE_RESULTS
            return source.GenerateBlocksFromSource(GenerateBlockOption.None).Select(o => o is PlcType type ? "type:" + type.Name : o is PlcBlock block ? "block:" + block.Name : throw new InvalidDataException("Unexpected native generation object.")).ToArray();
#else
            source.GenerateBlocksFromSource(); return null;
#endif
        }
        public void Delete(SourceRow row) => ((PlcExternalSource)native[row.Id]).Delete();
    }
}
