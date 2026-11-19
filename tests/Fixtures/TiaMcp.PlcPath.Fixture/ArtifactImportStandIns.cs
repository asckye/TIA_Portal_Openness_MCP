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
        public PlcBlockGroup BlockGroup { get; } = new();
        public PlcTypeGroup TypeGroup { get; } = new();
        public PlcTagTableGroup TagTableGroup { get; } = new();
    }
}

namespace Siemens.Engineering.SW.Tags
{
    public sealed class PlcTagTable { public string Name { get; set; } = ""; }
    public class PlcTagTableGroup
    {
        public string Name { get; set; } = "";
        public NamedList<PlcTagTableGroup> Groups { get; } = new();
        public PlcTagTableComposition TagTables { get; } = new();
    }
    public sealed class PlcTagTableComposition : NamedList<PlcTagTable>
    {
        public List<PlcTagTable> Import(FileInfo file, ImportOptions mode)
        {
            var table = new PlcTagTable { Name = SimaticMlImportWindow.ReadObject(XDocument.Load(file.FullName))!.Value.Name };
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
