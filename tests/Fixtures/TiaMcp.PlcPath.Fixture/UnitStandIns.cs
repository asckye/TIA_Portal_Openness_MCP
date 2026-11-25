using System.Globalization;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW.Units;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Engineering;

namespace Siemens.Engineering
{
    public interface IEngineeringObject { public object GetAttribute(string name); public void SetAttribute(string name, object value); }
    public sealed class MultilingualText { public List<MultilingualTextItem> Items { get; } = new(); }
    public sealed class Language { public CultureInfo Culture => CultureInfo.GetCultureInfo("en-US"); }
    public sealed class MultilingualTextItem
    {
        public Language Language { get; } = new();
        public bool Refuse;
        public string Text
        {
            get => Retained;
            set
            {
                if (Refuse) throw new InvalidOperationException("Comment write refused.");
                Retained = value;
                throw new IOException("Reply lost after setting text.");
            }
        }
        public string Retained { get; private set; } = "before";
    }
}
namespace Siemens.Engineering.Library.MasterCopies
{
    public sealed class MasterCopy { public string Name => throw new NotSupportedException(); }
    public enum MasterCopyMode { Default }
}
namespace Siemens.Engineering.SW.Blocks
{
    public sealed class PlcBlockSystemGroup : PlcBlockGroup { public object[] SystemBlockGroups => []; }
}
namespace Siemens.Engineering.SW.Types { public sealed class PlcTypeSystemGroup : PlcTypeGroup { } }
namespace Siemens.Engineering.SW.Tags { public sealed class PlcTagTableSystemGroup : PlcTagTableGroup { } }
namespace Siemens.Engineering.SW.ExternalSources
{
    public sealed class PlcExternalSourceSystemGroup
    {
        public string Name => "";
        public object[] ExternalSources => [];
        public object[] Groups => [];
    }
}
namespace Siemens.Engineering.SW.Units
{
    public sealed class PlcUnitProvider { public PlcUnitSystemGroup UnitGroup { get; } = new(); }
    public sealed class PlcUnitSystemGroup { public PlcUnitComposition Units { get; } = new(); }
    public sealed class PlcUnitComposition : Blocks.NamedList<PlcUnit>
    {
        public PlcUnit Create(string name) => throw new NotSupportedException();
        public PlcUnit CreateFrom(MasterCopy source) => throw new NotSupportedException();
    }
    public sealed class PlcUnit
    {
        public string Name { get; set; } = "Unit1";
        public string Author { get; set; } = "original";
        public MultilingualText Comment { get; } = new();
        public PlcUnitRelationComposition Relations { get; } = new();
        public Blocks.PlcBlockSystemGroup BlockGroup => throw new NotSupportedException();
        public Types.PlcTypeSystemGroup TypeGroup => throw new NotSupportedException();
        public Tags.PlcTagTableSystemGroup TagTableGroup => throw new NotSupportedException();
        public ExternalSources.PlcExternalSourceSystemGroup ExternalSourceGroup => throw new NotSupportedException();
        public void Delete() => throw new NotSupportedException();
    }
    public enum UnitAccessType { Default }
    public enum UnitRelationType { Default }
    public sealed class PlcUnitRelation
    {
        public string RelatedObject => throw new NotSupportedException();
        public UnitRelationType RelationType => throw new NotSupportedException();
        public void Delete() => throw new NotSupportedException();
    }
    public sealed class PlcUnitRelationComposition : List<PlcUnitRelation>
    {
        public PlcUnitRelation? Find(string name) => this.SingleOrDefault(r => r.RelatedObject == name);
        public PlcUnitRelation Create(string name, UnitRelationType kind) => throw new NotSupportedException();
    }
}
namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcUnitScope
    {
        internal static PlcUnitProvider RequireProvider(object plc) => ((Siemens.Engineering.SW.PlcSoftware)plc).UnitProvider;
        internal static PlcUnit? FindSafety(PlcUnitSystemGroup group, string name) => null;
        internal static PlcUnit Exact(PlcUnitSystemGroup group, string kind, string name) => group.Units.Find(name)!;
    }
    public partial interface IPlcOrganisationSession
    {
        public MasterCopy ExactMasterCopy(string library, string path) => throw new NotSupportedException();
    }
    public sealed partial class PlcOrganisationAdapter
    {
        private static void Page(object?[] rows, int offset, int limit, Dictionary<string, object?> meta) => throw new NotSupportedException();
    }
}
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public bool UnitMutationStarted => Artifacts.MutationStarted;
        public Dictionary<string, object?> UpdateUnitComment(PlcUnit unit)
        {
            Plc.UnitProvider.UnitGroup.Units.Add(unit);
            var meta = new Dictionary<string, object?>();
            Step = (_, operation) => new HardwareAddressingReply {
                Message = EngineeringWriteJournal.RunReported(meta, () => operation(meta), error => error.Message), Meta = meta
            };
            Artifacts.ManagePlcSoftwareUnit("PLC", "update", unit.Name, "", "", new() {
                ["Author"] = new HardwareScalar { Kind = "string", Text = "changed" }
            }, false, "unit", [new PlcTagComment { Culture = "en-US", Text = "after" }], "", "", "", "");
            return meta;
        }
    }
}
