using TiaMcp.Adapters.Contracts;
using Siemens.Engineering.SW.Alarm;
using TiaMcp.Adapters.Native.Plc;

namespace Siemens.Engineering
{
    public enum ImportOptions { None }
}
namespace Siemens.Engineering.SW.Alarm
{
    internal enum ImportState { Success }
    internal sealed class AlarmClassExportImportResult
    {
        public ImportState State => ImportState.Success;
        public int ErrorCount => 0;
        public int WarningCount => 0;
    }
    internal sealed class AlarmClassDataProvider
    {
        public int Calls;
        public AlarmClassExportImportResult Import(FileInfo input)
        {
            Calls++;
            throw new IOException("Alarm class provider lost its import reply.");
        }
    }
    internal sealed class TextListXlsxResult
    {
        public ImportState State => ImportState.Success;
        public FileInfo? LogFilePath => null;
    }
    internal sealed class PlcAlarmTextListProvider
    {
        public int Calls;
        public TextListXlsxResult ImportFromXlsx(FileInfo file, ImportOptions options)
        {
            Calls++;
            throw new IOException("Text list provider lost its import reply.");
        }
    }
}
namespace Siemens.Engineering.SW.Alarm.TextLists { }
namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed class ImportSession
    {
        internal readonly AlarmClassDataProvider Classes = new();
        internal readonly PlcAlarmTextListProvider Texts = new();
        internal object ServerInterfaces { get; set; } = null!;
        internal ImportSession? CurrentProject => this;
        internal bool IsProjectNull() => false;
        internal ImportSession ResolvePlc(string path, bool write) => this;
        internal string AvailablePlcPathsSuffix() => "";
        internal T? GetService<T>() where T : class => Classes as T ?? Texts as T;
    }

    internal sealed partial class PlcB5Adapter
    {
        private readonly ImportSession _session;
        internal PlcB5Adapter(ImportSession session) => _session = session;
        private object GetOpcUaServerInterfaceGroup(ImportSession plc) => new { plc.ServerInterfaces };
        private static PlcB5Array AlarmClassMessages(AlarmClassExportImportResult? result) => throw new NotSupportedException();
        private static PlcB5Object NativeXlsxEvidence(PlcB5Object meta, string state, bool change, string? log, string path, string type)
            => throw new NotSupportedException();
    }
    internal sealed class Interfaces(string scenario) : List<ServerInterface>
    {
        public ServerInterface Create(string name)
        {
            if (scenario == "opc-create-refused") throw new InvalidOperationException("OPC creation interrupted.");
            var item = new ServerInterface(this, name, scenario == "opc-cleaned");
            Add(item);
            if (scenario == "opc-create-interrupted") throw new InvalidOperationException("OPC creation interrupted.");
            return item;
        }
    }
    internal sealed class ServerInterface(Interfaces owner, string name, bool remove)
    {
        public string Name => name;
        public void Import(FileInfo input) => throw new IOException("OPC UA import interrupted.");
        public void Delete()
        {
            if (remove) owner.Remove(this);
            throw new IOException("Cleanup reply lost.");
        }
    }
}
