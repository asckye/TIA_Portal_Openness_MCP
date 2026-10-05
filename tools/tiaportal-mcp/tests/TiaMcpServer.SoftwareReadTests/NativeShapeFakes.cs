// Test doubles only. These prove control flow and C# source integration, never Siemens API compatibility.
namespace Siemens.Engineering
{
    public interface IEngineeringInstance { IEngineeringObject? Parent { get; } }
    public interface IEngineeringServiceProvider { T GetService<T>(); }
    public interface IEngineeringObject : IEngineeringInstance, IEngineeringServiceProvider { object? GetAttribute(string name); }
    public class EngineeringObject : IEngineeringObject
    {
        public IEngineeringObject? Parent { get; set; }
        public virtual object? GetAttribute(string name) => throw new NotSupportedException();
        public T GetService<T>() => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.SW.Blocks
{
    public enum ProgrammingLanguage { SCL, LAD, FBD, DB }
    public class PlcBlock : Siemens.Engineering.EngineeringObject
    {
        public string Name { get; set; } = "";
        public ProgrammingLanguage ProgrammingLanguage { get; set; } = ProgrammingLanguage.SCL;
        public bool FailNumber { get; set; }
        public override object? GetAttribute(string name) => FailNumber ? throw new InvalidOperationException("unreadable") : 1;
        public bool IsConsistent => throw new NotSupportedException();
        public bool AutoNumber => throw new NotSupportedException();
        public int Number => throw new NotSupportedException();
        public bool IsKnowHowProtected => throw new NotSupportedException();
        public void Delete() => throw new NotSupportedException();
        public void Export(FileInfo file, Siemens.Engineering.ExportOptions options) => throw new NotSupportedException();
        public DocumentExportResult ExportAsDocuments(DirectoryInfo directory, string name) => throw new NotSupportedException();
    }
    public class PlcBlockGroup
    {
        public string Name { get; set; } = "";
        public bool FailEnumeration { get; set; }
        public PlcBlockComposition Items { get; } = new();
        public PlcBlockComposition Blocks => FailEnumeration ? throw new IOException("native composition unavailable") : Items;
        public PlcBlockUserGroupComposition Groups { get; } = new();
    }
    public class PlcBlockSystemGroup : PlcBlockGroup { }
    public class PlcBlockUserGroup : PlcBlockGroup { public void Delete() => throw new NotSupportedException(); }
    public class PlcBlockUserGroupComposition : List<PlcBlockUserGroup>
    {
        public PlcBlockUserGroup Create(string name) => throw new NotSupportedException();
    }
    public class PlcBlockComposition : List<PlcBlock>
    {
        public IList<PlcBlock> Import(FileInfo file, Siemens.Engineering.ImportOptions options) => throw new NotSupportedException();
        public DocumentImportResultForBlocks ImportFromDocuments(DirectoryInfo directory, string name, ImportDocumentOptions options) => throw new NotSupportedException();
        public PlcBlock Find(string name) => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.SW.Types
{
    public class PlcType { public string Name { get; set; } = ""; public void Delete() => throw new NotSupportedException(); }
    public class PlcTypeGroup
    {
        public string Name { get; set; } = "";
        public PlcTypeComposition Types { get; } = new();
        public PlcTypeUserGroupComposition Groups { get; } = new();
    }
    public class PlcTypeSystemGroup : PlcTypeGroup { }
    public class PlcTypeUserGroup : PlcTypeGroup { }
    public class PlcTypeComposition : List<PlcType> { }
    public class PlcTypeUserGroupComposition : List<PlcTypeUserGroup>
    {
        public PlcTypeUserGroup Create(string name) => throw new NotSupportedException();
    }
}
namespace Siemens.Engineering.SW
{
    public class PlcSoftware : Siemens.Engineering.HW.Software
    {
        public string Name = "PLC";
        public Blocks.PlcBlockSystemGroup BlockGroup = new();
        public Types.PlcTypeSystemGroup? TypeGroup = new();
    }
}
namespace TiaMcp.PlcFoundation
{
    internal class FakeSoftware : Siemens.Engineering.SW.PlcSoftware
    {
        internal PlcAttributeValue[] Attributes = Array.Empty<PlcAttributeValue>();
    }
    public sealed partial class PlcFoundationEngine
    {
        internal FakeSoftware Software = new();
        private PlcReadCandidate<Siemens.Engineering.SW.PlcSoftware> ReadSelection(string path) => PlcReadPathPolicy.Select(new[]{new PlcReadCandidate<Siemens.Engineering.SW.PlcSoftware> { Value=Software,ExactPath="devices/D/CPU",Device="D",Host="CPU" }},path);
        private static PlcAttributeValue[] ReadAttributes(Siemens.Engineering.SW.PlcSoftware value) => ((FakeSoftware)value).Attributes;
        private static void Depth(int value) { if(value>128) throw new InvalidOperationException("too deep"); }
    }
}
