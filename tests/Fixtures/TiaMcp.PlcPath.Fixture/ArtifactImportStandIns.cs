using System.Xml.Linq;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;

namespace Siemens.Engineering.HW.Features
{
    public sealed class SoftwareContainer { public SW.PlcSoftware? Software { get; set; } }
}

namespace Siemens.Engineering.SW
{
    public class PlcSoftware
    {
        public Units.PlcUnitProvider UnitProvider { get; } = new();
        public PlcBlockGroup BlockGroup { get; } = new();
        public PlcTypeGroup TypeGroup { get; } = new();
        public PlcTagTableGroup TagTableGroup { get; } = new();
    }
}

namespace Siemens.Engineering.SW.Tags
{
    public sealed class PlcTagTable
    {
        public string Name { get; set; } = "";
        public List<object> UserConstants { get; } = new();
        public Action? OnDelete { get; set; }
        public void Delete() => OnDelete!();
        public NamedList<CrossReference.ServiceProvider> Tags { get; } = new();
        public NamedList<CrossReference.ServiceProvider> SystemConstants { get; } = new();
    }
    public class PlcTagTableGroup
    {
        public string Name { get; set; } = "";
        public NamedList<PlcTagTableGroup> Groups { get; } = new();
        public PlcTagTableComposition TagTables { get; } = new();
    }
    public sealed class PlcTagTableComposition : NamedList<PlcTagTable>
    {
        public Action<PlcTagTable>? OnImport;
        public Func<string, PlcTagTable?>? OnFind;
        public new PlcTagTable? Find(string name) => OnFind == null ? base.Find(name) : OnFind(name);
        public List<PlcTagTable> Import(FileInfo file, ImportOptions mode)
        {
            var table = new PlcTagTable { Name = SimaticMlImportWindow.ReadObject(XDocument.Load(file.FullName))!.Value.Name };
            OnImport?.Invoke(table);
            Add(table);
            return [table];
        }
    }
}

namespace Siemens.Engineering.SW.Types
{
    public sealed class PlcTypeComposition : NamedList<PlcType>
    {
        public List<PlcType> Import(FileInfo file, ImportOptions mode)
        {
            var type = new PlcType { Name = SimaticMlImportWindow.ReadObject(XDocument.Load(file.FullName))!.Value.Name };
            Add(type);
            return [type];
        }
    }
}
