using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Contracts;

namespace Siemens.Engineering
{
    public enum ExportOptions { None, WithDefaults, WithReadOnly }
    public enum ImportOptions { None, Override }
}
namespace Siemens.Engineering.SW.Blocks
{
    public class NamedList<T> : List<T> where T : class
    {
        public T? Find(string name) => this.SingleOrDefault(item => string.Equals(
            item.GetType().GetProperty("Name")!.GetValue(item), name));
    }
    public class PlcBlockGroup
    {
        public string Name { get; set; } = "";
        public object? Parent { get; set; }
        public Action<string>? OnCall { get; set; }
        public Action<PlcBlock>? AfterImport { get; set; }
        public Action<FileInfo>? AfterBlockExport { get; set; }
        public GroupList Groups { get; }
        public BlockList Blocks { get; }
        public PlcBlockGroup() { Groups = new(this); Blocks = new(this); }
        public void Delete()
        {
            OnCall?.Invoke("delete group " + Name);
            ((PlcBlockGroup)Parent!).Groups.Remove(this);
        }
    }
    public sealed class GroupList(PlcBlockGroup parent) : NamedList<PlcBlockGroup>
    {
        public PlcBlockGroup Create(string name)
        {
            parent.OnCall?.Invoke("create " + name);
            var group = new PlcBlockUserGroup { Name = name, Parent = parent, OnCall = parent.OnCall, AfterImport = parent.AfterImport };
            Add(group);
            return group;
        }
    }
    public sealed class BlockList(PlcBlockGroup parent) : NamedList<PlcBlock> { public PlcBlockGroup Parent => parent; }
    public sealed class OB : PlcBlock { }
    public sealed class PlcBlockUserGroup : PlcBlockGroup { }
    public class PlcBlock : CrossReference.ServiceProvider
    {
        public string Name { get; set; } = "";
        public object? Parent { get; set; }
        public bool IsConsistent { get; set; } = true;
        public string Language { get; set; } = "LAD";
        public int Exports { get; set; }
        public List<Siemens.Engineering.ExportOptions> ExportOptionsUsed { get; } = new();
        public bool IsKnowHowProtected => false;
        public void Delete() => ((PlcBlockGroup)Parent!).Blocks.Remove(this);
    }
}
namespace Siemens.Engineering.SW.Types
{
    public class PlcTypeGroup
    {
        public string Name { get; set; } = "";
        public NamedList<PlcTypeGroup> Groups { get; } = new();
        public PlcTypeComposition Types { get; } = new();
    }
    public sealed class PlcType : CrossReference.ServiceProvider
    {
        public string Name { get; set; } = "";
        public string Declaration { get; set; } = "";
        public Exception? ExportFailure { get; set; }
        public void Export(FileInfo file, Siemens.Engineering.ExportOptions options)
        {
            if (ExportFailure != null) throw ExportFailure;
            File.WriteAllText(file.FullName, Declaration);
        }
    }
}
namespace TiaMcp.Adapters.Native.Plc
{
    public partial interface IPlcOrganisationSession
    {
        public string ReleaseKey { get; }
        public void VerifyBinding(string tool);
        public PlcTypeGroup? GetPlcTypeGroupByPath(string path, string groupPath);
        public PlcSoftware? ResolvePlc(string path, bool writing);
        public Siemens.Engineering.HW.Features.SoftwareContainer? GetSoftwareContainer(string path);
        public void GetBlocksRecursive(PlcBlockGroup root, List<PlcBlock> result);
        public string PlcSoftwarePath(PlcSoftware? software);
        public HardwareAddressingReply RunHmiStepTool(string tool, Func<Dictionary<string, object?>, string> operation);
        public PlcBlock? GetBlock(string path, string blockPath);
        public PlcBlock GetBlockForRead(string path, string blockPath);
        public bool IsProjectNull();
        public string GetPlcBlockGroupPath(PlcBlockGroup group);
        public void RecordExportPath(string? path);
        public string GetBlockPath(PlcBlock block);
        public List<PlcBlock>? GetBlocks(string path, string regex = "");
        public char[] RegexChars { get; }
        public string AvailablePlcPathsSuffix();
        public PlcBlockGroup? GetPlcBlockGroupByPath(string path, string groupPath);
        public List<PlcCrossReference>? GetCrossReferences(string path, string block, string kind, string filter, out string? reason, out bool queried);
        public string SerializeEvidence(Dictionary<string, object?> evidence);
        public string BindingIdentity();
        public IDisposable AcquireHmiEditAccess();
        public object ExactPlcForEngineering(string path, bool write);
    }
    public sealed partial class PlcOrganisationAdapter
    {
        public HardwareAddressingReply ManagePlcUserGroup(string softwarePath, string family, string groupPath, string action,
            string newName = "", bool dryRun = true) => throw new NotSupportedException("Group editing is outside this path fixture.");
        private static List<object?> ToCrossReferenceArray(List<PlcCrossReference> references) => references.Cast<object?>().ToList();
    }
    internal static class PlcBlockPrimitives
    {
        internal static PlcBlockGroup BlockGroup(object plc) => PlcOrganisationPrimitives.BlockGroup(plc);
        internal static PlcTypeGroup TypeGroup(object plc) => ((PlcSoftware)plc).TypeGroup;
        internal static PlcTypeComposition Types(PlcTypeGroup group) => group.Types;
        internal static NamedList<PlcBlock> Blocks(PlcBlockGroup group) => group.Blocks;
        internal static PlcBlock? Find(NamedList<PlcBlock> blocks, string name) => blocks.Find(name);
        internal static bool IsConsistent(PlcBlock block) => block.IsConsistent;
        internal static List<PlcBlock> Import(IEnumerable<PlcBlock> blocks, FileInfo file, Siemens.Engineering.ImportOptions options)
            => PlcOrganisationPrimitives.Import(blocks, file, options);
        internal static object? Compiler(PlcBlock block) => null;
        internal static object Compile(object compiler) => throw new NotSupportedException();
        internal static string State(object result) => throw new NotSupportedException();
        internal static int ErrorCount(object result) => throw new NotSupportedException();
        internal static int WarningCount(object result) => throw new NotSupportedException();
        internal static void Export(PlcBlock block, FileInfo file, Siemens.Engineering.ExportOptions options)
        {
            block.Exports++;
            block.ExportOptionsUsed.Add(options);
            File.WriteAllText(file.FullName, "<Document><SW.Blocks.FC><AttributeList><Name>" +
                System.Security.SecurityElement.Escape(block.Name) + "</Name></AttributeList></SW.Blocks.FC></Document>");
            (block.Parent as PlcBlockGroup)?.AfterBlockExport?.Invoke(file);
        }
    }
}
namespace TiaOpenness.Shared
{
    internal static class NativeInputPolicy
    {
        internal static T Read<T>(string parameter, Func<T> read) => read();
        internal static string FullPath(string path) => Path.GetFullPath(path);
    }
    internal static class NativeExportPolicy
    {
        internal static void RequireSoftwarePath(string path, string exact, bool allowAlias)
        { if (path != exact) throw new ArgumentException("Exact software path required."); }
        internal static void RequireConsistent(string kind, string[] inconsistent, string parameter)
        {
            if (inconsistent.Length != 0) throw new InvalidOperationException("Inconsistent " + kind + ": " + parameter);
        }
    }
}
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public sealed class Software : PlcSoftware { }
        public PlcOrganisationAdapter Artifacts => new PlcOrganisationAdapter(new OrganisationSession(this));
        public Software Plc { get; } = new();
        public PlcFoundationEngine(string release) => ReleaseKey = release;
        public string ReleaseKey { get; }
        private (Software Value, string Path) ReadSelection(string path) => (Plc, path);
        public PlcBlock? ExactBlock(string path) => new OrganisationSession(this).GetBlock("PLC", path);
        public PlcType? ExactType(string path) => new OrganisationSession(this).GetType("PLC", path);
        public string GroupPath(PlcBlockGroup group) => new OrganisationSession(this).GetPlcBlockGroupPath(group);
        public string BlockPath(PlcBlock block) => new OrganisationSession(this).GetBlockPath(block);
        public PlcBlockGroup? BlockGroup(string path) => new OrganisationSession(this).GetPlcBlockGroupByPath("PLC", path);
        public PlcTypeGroup? TypeGroup(string path) => new OrganisationSession(this).GetPlcTypeGroupByPath("PLC", path);
        public string TreePath(string group, string name) => PlcSoftwareReadPolicy.ChildPath(group, name);
        public PlcBlock? DescribePlcBlockLogic(string path, string file)
            => new PlcOrganisationAdapter(new OrganisationSession(this)).ExportBlock("PLC", path, file);
        public Dictionary<string, object?> DeletePlcBlock(string path, bool dryRun = false, bool crossReferences = false)
            => Artifacts.DeletePlcBlock("PLC", path, dryRun, crossReferences);
        public HardwareAddressingReply MovePlcBlockToGroup(string path, string group)
            => new PlcOrganisationAdapter(new OrganisationSession(this)).MoveBlockToGroup("PLC", path, group);
        public Func<string, Func<Dictionary<string, object?>, string>, HardwareAddressingReply>? Step { get; set; }
        public HardwareAddressingReply VerifiedImport(PlcSoftwareRequest request, PlcVerifiedCandidate candidate, Func<PlcVerifiedExecution, string> run)
            => new PlcOrganisationAdapter(new OrganisationSession(this)).ImportPlcBlockVerified(request, _ => candidate, run);
        public HardwareAddressingReply ApplyMove(string path, string group, string recovery, string hash)
            => new PlcOrganisationAdapter(new OrganisationSession(this)).MoveBlockToGroup("PLC", path, group, true, recovery, false, hash);
        private sealed partial class OrganisationSession : IPlcOrganisationSession
        {
            private readonly PlcFoundationEngine engine;
            public OrganisationSession(PlcFoundationEngine engine) => this.engine = engine;
            public string ReleaseKey => engine.ReleaseKey;
            public void VerifyBinding(string tool) { }
            public HardwareAddressingReply RunHmiStepTool(string tool, Func<Dictionary<string, object?>, string> operation)
            {
                if (engine.Step != null) return engine.Step(tool, operation);
                var meta = new Dictionary<string, object?>();
                return new HardwareAddressingReply { Message = operation(meta), Meta = meta };
            }
            public PlcSoftware? ResolvePlc(string path, bool writing) => path == "PLC" ? engine.Plc : null;
            public string PlcSoftwarePath(PlcSoftware? software) => software == engine.Plc ? "PLC" : "";
            public Siemens.Engineering.HW.Features.SoftwareContainer? GetSoftwareContainer(string path)
                => new Siemens.Engineering.HW.Features.SoftwareContainer { Software = ResolvePlc(path, false) };
            public void GetBlocksRecursive(PlcBlockGroup root, List<PlcBlock> result)
            {
                result.AddRange(root.Blocks);
                foreach (var group in root.Groups) GetBlocksRecursive(group, result);
            }
            public bool IsProjectNull() => false;
            public void RecordExportPath(string? path) { }
            public char[] RegexChars => new[] { '.', '*', '+', '?', '^', '$', '[', ']', '(', ')', '{', '}', '|', '\\' };
            public List<PlcBlock>? GetBlocks(string path, string regex = "") => engine.Plc.BlockGroup.Groups.SelectMany(group => group.Blocks).ToList();
            public string AvailablePlcPathsSuffix() => "";
            public List<PlcCrossReference>? GetCrossReferences(string path, string block, string kind, string filter, out string? reason, out bool queried)
            => new PlcCrossReferences(this).GetCrossReferences(path, block, kind, filter, out reason, out queried, "", "unit");
            public string SerializeEvidence(Dictionary<string, object?> evidence) => System.Text.Json.JsonSerializer.Serialize(evidence);
            public string BindingIdentity() => engine.ReleaseKey + ":fixture";
            public IDisposable AcquireHmiEditAccess() => new MemoryStream();
            public object ExactPlcForEngineering(string path, bool write) => engine.Plc;
        }
    }
}

namespace TiaMcp.Adapters.Native.Plc
{
    internal static partial class PlcOrganisationPrimitives
    {
        internal static bool AutoNumber(PlcBlock block) => true;
        internal static int Number(PlcBlock block) => 1;
        internal static string Name(object value) => value.GetType().GetProperty("Name")!.GetValue(value)!.ToString()!;
        internal static int Count(object values) => ((System.Collections.IEnumerable)values).Cast<object>().Count();
        internal static object Groups(object value) => PlcGroupOperations.Get(value, "Groups");
        internal static object Types(object value) => PlcGroupOperations.Get(value, "Types");
        internal static PlcTagTableComposition TagTables(PlcTagTableGroup group) => group.TagTables;
        internal static object TagTables(object value) => PlcGroupOperations.Get(value, "TagTables");
        internal static object ExternalSources(object value) => PlcGroupOperations.Get(value, "ExternalSources");
        internal static object Parent(PlcBlock block) => block.Parent!;
        internal static string Language(PlcBlock block) => block.Language;
        internal static void Delete(PlcBlock block)
        {
            ((PlcBlockGroup)block.Parent!).OnCall?.Invoke("delete " + block.Name);
            block.Delete();
        }
        internal static IEnumerable<PlcBlock> Blocks(PlcBlockGroup group) => group.Blocks;
        internal static void Export(PlcBlock block, FileInfo file, Siemens.Engineering.ExportOptions options)
        {
            ((PlcBlockGroup)block.Parent!).OnCall?.Invoke("export " + block.Name);
            PlcBlockPrimitives.Export(block, file, options);
            var document = System.Xml.Linq.XDocument.Load(file.FullName);
            document.Descendants("AttributeList").Single().Add(new System.Xml.Linq.XElement("Number", Number(block)),
                new System.Xml.Linq.XElement("ProgrammingLanguage", Language(block)));
            document.Save(file.FullName);
        }
        internal static List<PlcBlock> Import(IEnumerable<PlcBlock> blocks, FileInfo file, Siemens.Engineering.ImportOptions options)
        {
            var target = (BlockList)blocks;
            target.Parent.OnCall?.Invoke("import " + target.Parent.Name);
            var root = System.Xml.Linq.XDocument.Load(file.FullName).Root!;
            var definition = root.Elements().Single(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
            var block = new PlcBlock { Name = definition.Elements("AttributeList").Single().Elements("Name").Single().Value, Parent = target.Parent };
            target.Add(block);
            target.Parent.AfterImport?.Invoke(block);
            return new() { block };
        }
        internal static PlcBlockGroup BlockGroup(object plc) => ((PlcFoundationEngine.Software)plc).BlockGroup;
    }
}
namespace TiaOpenness.Shared
{
    internal sealed class DataLocations
    {
        internal static DataLocations Current { get; } = new();
        internal string RecoveryAttemptedPath => AppContext.BaseDirectory;
        internal string TempDirectory => AppContext.BaseDirectory;
    }
}
namespace TiaMcp.Adapters
{
    internal static class PlcBatchImportRecovery
    {
        internal static void Precheck() { }
    }
}

namespace Siemens.Engineering.Online { }
namespace Siemens.Engineering.SW.Types { public sealed class PlcTypeUserGroup : PlcTypeGroup { } }
namespace Siemens.Engineering.SW.Tags { public sealed class PlcTagTableUserGroup : PlcTagTableGroup { } }
namespace Siemens.Engineering.SW.ExternalSources { public sealed class PlcExternalSourceUserGroup { } }
namespace TiaMcpServer.ModelContextProtocol
{
    internal static class PortalFailureClassifier
    {
        internal static bool IsPortalProcessLost(Exception error) => HmiReadPolicy.ConnectionUnavailable(error);
    }
}
