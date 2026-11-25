using TiaMcp.Adapters.Contracts;
using Siemens.Engineering.SW.Alarm;
using TiaMcp.Adapters.Native.Plc;

namespace Siemens.Engineering
{
    public enum ImportOptions { None }
}
namespace Siemens.Engineering.SW.Alarm
{
    internal enum AlarmClassExportImportResultState { Success, Warning, Error }
    internal enum TextListXlsxResultState { OK, Warning, Error }
    internal sealed class AlarmClassExportImportResultMessage
    {
        public string Message => "Invalid alarm class.";
        public AlarmClassExportImportResultState State => AlarmClassExportImportResultState.Error;
    }
    internal sealed class AlarmClassExportImportResult
    {
        public AlarmClassExportImportResultState State => AlarmClassExportImportResultState.Error;
        public AlarmClassExportImportResultMessage[] Messages => new[] { new AlarmClassExportImportResultMessage() };
        public int ErrorCount => Messages.Length;
        public int WarningCount => 0;
    }
    internal sealed class AlarmClassDataProvider(Exception? failure)
    {
        public int Calls;
        public AlarmClassExportImportResult Import(FileInfo input)
        {
            Calls++;
            if (failure != null) throw failure;
            return new AlarmClassExportImportResult();
        }
    }
    internal sealed class TextListXlsxResult
    {
        public TextListXlsxResultState State => TextListXlsxResultState.Error;
        public FileInfo? LogFilePath => null;
    }
    internal sealed class PlcAlarmTextListProvider(Exception? failure)
    {
        public int Calls;
        public TextListXlsxResult ImportFromXlsx(FileInfo file, ImportOptions options)
        {
            Calls++;
            if (failure != null) throw failure;
            return new TextListXlsxResult();
        }
    }
}
namespace Siemens.Engineering.SW.Alarm.TextLists { }
namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed class ImportSession(Exception? failure)
    {
        internal readonly AlarmClassDataProvider Classes = new(failure);
        internal readonly PlcAlarmTextListProvider Texts = new(failure);
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
    }
    internal sealed class Interfaces(string scenario, Exception? failure) : List<ServerInterface>
    {
        public int ImportCalls;
        public ServerInterface Create(string name)
        {
            if (scenario == "opc-create-refused") throw new InvalidOperationException("OPC creation interrupted.");
            var item = new ServerInterface(this, name, scenario == "opc-cleaned", failure);
            Add(item);
            if (scenario == "opc-create-interrupted") throw new InvalidOperationException("OPC creation interrupted.");
            return item;
        }
    }
    internal sealed class ServerInterface(Interfaces owner, string name, bool remove, Exception? failure)
    {
        public string Name => name;
        public void Import(FileInfo input)
        {
            owner.ImportCalls++;
            if (failure != null) throw failure;
        }
        public void Delete()
        {
            if (remove) owner.Remove(this);
            throw new IOException("Cleanup reply lost.");
        }
    }
}
