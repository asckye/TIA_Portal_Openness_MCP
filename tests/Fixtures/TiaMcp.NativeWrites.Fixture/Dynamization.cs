using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Native.Plc;

namespace Siemens.Engineering.HmiUnified.UI.Dynamization
{
    internal sealed class TagDynamization
    {
        public string PropertyName { get; set; } = "";
        private int offset;
        public int Offset
        {
            get => offset;
            set { offset = value; if (value == 7) throw new IOException("Offset reply lost."); }
        }
        public ValueConverter ValueConverter { get; } = new();
    }
    internal sealed class ValueConverter { public MappingTable MappingTable { get; } = new(); }
    internal sealed class MappingTable { public MappingEntries Entries { get; } = new(); }
    internal sealed class MappingEntries : List<object>
    {
        public T Create<T>() where T : new() { var item = new T(); Add(item!); return item; }
    }
}
namespace Siemens.Engineering.HmiUnified.UI.Dynamization.Tag
{
    internal sealed class MappingTableEntrySimple
    {
        public int Offset { get => 0; set => throw new IOException("Entry setter refused."); }
    }
}
namespace TiaMcp.Adapters.Native.Hmi
{
    using Siemens.Engineering.HmiUnified.UI.Dynamization;
    internal sealed class DynCollection : List<TagDynamization>
    {
        internal bool RefuseCreate;
        public T Create<T>(string propertyName) where T : new()
        {
            var item = new T();
            if (RefuseCreate) throw new InvalidOperationException("Dynamization creation refused.");
            var dyn = (TagDynamization)(object)item;
            dyn.PropertyName = propertyName;
            Add(dyn);
            return item;
        }
    }
    internal sealed class DynSession
    {
        public DynCollection Dynamizations { get; } = new();
        public HmiObject Meta { get; } = new();
        internal object ExactUnifiedRoot(string path) => this;
        internal IDisposable? AcquireHmiEditAccess() => null;
        internal HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> run)
        {
            var message = EngineeringWriteJournal.RunReported(Meta, () => run(Meta), error => error.GetBaseException().Message);
            return new HmiMessage { Message = message, Meta = Meta };
        }
    }
    internal sealed partial class UnifiedUiModelAdapter
    {
        private readonly DynSession _session;
        public UnifiedUiModelAdapter(DynSession session) => _session = session;
    }
    internal static class HmiExactAccess { internal static HmiObject EventDto(object handler) => throw new NotSupportedException(); }
    internal static class UnifiedMultilingualText { internal static object Read(object value) => throw new NotSupportedException(); }
}
