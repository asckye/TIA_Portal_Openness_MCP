using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Contracts;

namespace Siemens.Engineering
{
    public enum ExportOptions { None, WithDefaults }
    public enum ImportOptions { None }
}
namespace Siemens.Engineering.SW.Blocks
{
    public sealed class NamedList<T> : List<T> where T : class
    {
        public T? Find(string name) => this.SingleOrDefault(item => string.Equals(
            item.GetType().GetProperty("Name")!.GetValue(item), name));
    }
    public class PlcBlockGroup
    {
        public string Name { get; set; } = "";
        public object? Parent { get; set; }
        public NamedList<PlcBlockGroup> Groups { get; } = new();
        public NamedList<PlcBlock> Blocks { get; } = new();
    }
    public sealed class OB : PlcBlock { }
    public sealed class PlcBlockUserGroup : PlcBlockGroup { }
    public class PlcBlock
    {
        public string Name { get; set; } = "";
        public object? Parent { get; set; }
        public bool IsConsistent => true;
        public int Exports { get; set; }
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
        public NamedList<PlcType> Types { get; } = new();
    }
    public sealed class PlcType { public string Name { get; set; } = ""; }
}
namespace TiaMcp.Adapters.Native.Plc
{
    public interface IPlcOrganisationSession
    {
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
        internal static void Export(PlcBlock block, FileInfo file, Siemens.Engineering.ExportOptions options)
        {
            block.Exports++;
            File.WriteAllText(file.FullName, "<Document><SW.Blocks.FC><AttributeList><Name>" +
                System.Security.SecurityElement.Escape(block.Name) + "</Name></AttributeList></SW.Blocks.FC></Document>");
        }
    }
}
namespace TiaOpenness.Shared
{
    internal static class NativeInputPolicy
    {
        internal static string FullPath(string path) => Path.GetFullPath(path);
    }
    internal static class NativeExportPolicy
    {
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
        public sealed class Software
        {
            public PlcBlockGroup BlockGroup { get; } = new();
            public PlcTypeGroup TypeGroup { get; } = new();
        }
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
        public Dictionary<string, object?> DeletePlcBlock(string path)
            => new PlcOrganisationAdapter(new OrganisationSession(this)).DeletePlcBlock("PLC", path, false);
        public HardwareAddressingReply MovePlcBlockToGroup(string path, string group)
            => new PlcOrganisationAdapter(new OrganisationSession(this)).MoveBlockToGroup("PLC", path, group);
        private sealed partial class OrganisationSession : IPlcOrganisationSession
        {
            private readonly PlcFoundationEngine engine;
            public OrganisationSession(PlcFoundationEngine engine) => this.engine = engine;
            public bool IsProjectNull() => false;
            public void RecordExportPath(string? path) { }
            public char[] RegexChars => new[] { '.', '*', '+', '?', '^', '$', '[', ']', '(', ')', '{', '}', '|', '\\' };
            public List<PlcBlock>? GetBlocks(string path, string regex = "") => engine.Plc.BlockGroup.Groups.SelectMany(group => group.Blocks).ToList();
            public string AvailablePlcPathsSuffix() => "";
            public List<PlcCrossReference>? GetCrossReferences(string path, string block, string kind, string filter, out string? reason, out bool queried)
            { reason = null; queried = false; return null; }
            public string SerializeEvidence(Dictionary<string, object?> evidence) => System.Text.Json.JsonSerializer.Serialize(evidence);
            public string BindingIdentity() => engine.ReleaseKey + ":fixture";
            public IDisposable AcquireHmiEditAccess() => new MemoryStream();
            public object ExactPlcForEngineering(string path, bool write) => engine.Plc;
        }
    }
}

namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcOrganisationPrimitives
    {
        internal static bool AutoNumber(PlcBlock block) => true;
        internal static int Number(PlcBlock block) => 1;
        internal static string Name(object value) => value.GetType().GetProperty("Name")!.GetValue(value)!.ToString()!;
        internal static int Count(object values) => ((System.Collections.IEnumerable)values).Cast<object>().Count();
        internal static object Groups(object value) => PlcGroupOperations.Get(value, "Groups");
        internal static object Types(object value) => PlcGroupOperations.Get(value, "Types");
        internal static object TagTables(object value) => PlcGroupOperations.Get(value, "TagTables");
        internal static object ExternalSources(object value) => PlcGroupOperations.Get(value, "ExternalSources");
        internal static object Parent(PlcBlock block) => block.Parent!;
        internal static string Language(PlcBlock block) => "LAD";
        internal static void Delete(PlcBlock block) => block.Delete();
        internal static IEnumerable<PlcBlock> Blocks(PlcBlockGroup group) => group.Blocks;
        internal static void Export(PlcBlock block, FileInfo file, Siemens.Engineering.ExportOptions options)
            => PlcBlockPrimitives.Export(block, file, options);
        internal static List<PlcBlock> Import(IEnumerable<PlcBlock> blocks, FileInfo file, Siemens.Engineering.ImportOptions options)
            => throw new NotSupportedException("Move apply is outside this path fixture.");
        internal static PlcBlockGroup BlockGroup(object plc) => ((PlcFoundationEngine.Software)plc).BlockGroup;
    }
}
namespace TiaOpenness.Shared
{
    internal sealed class DataLocations
    {
        internal static DataLocations Current { get; } = new();
        internal string RecoveryAttemptedPath => Path.GetTempPath();
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
namespace Siemens.Engineering.SW.Tags { public sealed class PlcTagTableUserGroup { } }
namespace Siemens.Engineering.SW.ExternalSources { public sealed class PlcExternalSourceUserGroup { } }
namespace TiaMcpServer.ModelContextProtocol { }
