using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public string ReleaseKey => "21";
        internal Func<Exception, PlcSoftwareFailure>? EngineeringReflectionFailure => null;
        public HmiMessage NativeReply { get; set; } = new();
        public bool NativeMayHaveChanged { get; set; }
        public Exception? LookupFailure { get; set; }
        public int NativeCalls { get; set; }
        internal FakeSession EngineeringPlcSession { get; } = new();
        internal Action<string, string>? WriteEngineeringHmiTrace => null;
        internal void Check() { }
        internal string UnifiedHmiToken(string scope, Dictionary<string, object?> content) => "fixture-token";
    }

    internal sealed class FakeSession
    {
        internal string SerializeEvidence(Dictionary<string, object?> values) => "{}";
        internal void RecordExportFailure(Exception error) { }
    }
}

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class HmiSession
    {
        private readonly TiaMcp.Adapters.PlcFoundationEngine engine;
        internal HmiSession(TiaMcp.Adapters.PlcFoundationEngine engine, object session, HmiRequest request) => this.engine = engine;
        internal bool MayHaveChanged => engine.NativeMayHaveChanged;
        internal HmiMessage Invoke()
        {
            if (engine.LookupFailure != null) throw engine.LookupFailure;
            engine.NativeCalls++;
            return engine.NativeReply;
        }
    }

    internal static class HmiValues
    {
        internal static Func<Dictionary<string, object?>, string>? Renderer;
        internal static Action<string, string>? TraceWriter;
        internal static Action<Exception>? ExportFailure;
    }

    internal static class UnifiedHmiAccess
    {
        internal static Dictionary<string, UnifiedHmiInput>? Inputs;
    }

    internal static class HmiExactAccess
    {
        internal static Func<string, Dictionary<string, object?>, string>? ContentToken;
    }

    internal sealed class UnifiedHmiAdapter(HmiSession session)
    {
        public object EnsureStartStopUnifiedHmi(params object?[] args) => session.Invoke();
        public object EnsureUnifiedHmiScreen(params object?[] args) => session.Invoke();
        public object EnsureUnifiedHmiTagTable(params object?[] args) => session.Invoke();
        public object EnsureUnifiedHmiTag(params object?[] args) => session.Invoke();
        public object EnsureUnifiedHmiConnection(params object?[] args) => session.Invoke();
        public object EnsureUnifiedHmiScreenItem(params object?[] args) => session.Invoke();
        public object ReadUnifiedHmiTexts(params object?[] args) => session.Invoke();
        public object ApplyUnifiedHmiScreenDesignJson(params object?[] args) => session.Invoke();
        public object BindUnifiedHmiButtonPressedTag(params object?[] args) => session.Invoke();
        public object ListUnifiedHmiApiTypes(params object?[] args) => session.Invoke();
        public object EnsureUnifiedHmiButtonEventHandler(params object?[] args) => session.Invoke();
        public object DescribeUnifiedHmiButtonEventScript(params object?[] args) => session.Invoke();
        public object SetUnifiedHmiButtonEventScriptCode(params object?[] args) => session.Invoke();
        public object EnsureUnifiedHmiButtonAction(params object?[] args) => session.Invoke();
        public object EnsureUnifiedHmiDynamization(params object?[] args) => session.Invoke();
        public object BindUnifiedHmiTagDynamization(params object?[] args) => session.Invoke();
    }

    internal sealed class UnifiedScreenItemsAdapter(HmiSession session)
    {
        public object DescribeUnifiedScreenItemType(params object?[] args) => session.Invoke();
        public object ManageUnifiedScreenItem(params object?[] args) => session.Invoke();
    }

    internal sealed class UnifiedHmiGroupsAdapter(HmiSession session)
    {
        public object ManageUnifiedHmiGroup(params object?[] args) => session.Invoke();
    }

    internal sealed class UnifiedHmiInspectionAdapter(HmiSession session)
    {
        public object ReadUnifiedHmiButtonEvent(params object?[] args) => session.Invoke();
        public object DeleteUnifiedHmiButtonEvent(params object?[] args) => session.Invoke();
        public object ReadUnifiedHmiDynamization(params object?[] args) => session.Invoke();
        public object DeleteUnifiedHmiDynamization(params object?[] args) => session.Invoke();
        public object DeleteEmptyUnifiedHmiScreenGroup(params object?[] args) => session.Invoke();
    }
}

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class HmiDescribeAdapter(HmiSession session)
    {
        public object DescribeHmiScreen(params object?[] args) => session.Invoke();
        public object DescribeHmiScreenItem(params object?[] args) => session.Invoke();
        public object DescribeHmiSoftware(params object?[] args) => session.Invoke();
        public object DescribeHmiTag(params object?[] args) => session.Invoke();
        public object DescribeHmiTagTable(params object?[] args) => session.Invoke();
        public object GetHmiProgramInfo(params object?[] args) => session.Invoke();
    }
    internal sealed class HmiExchangeAdapter(HmiSession session)
    {
        public object ExportHmiConnection(params object?[] args) => session.Invoke();
        public object ExportHmiProgram(params object?[] args) => session.Invoke();
        public object ExportHmiScreen(params object?[] args) => session.Invoke();
        public object ExportHmiTagTable(params object?[] args) => session.Invoke();
        public object GetHmiConnections(params object?[] args) => session.Invoke();
        public object GetHmiScreens(params object?[] args) => session.Invoke();
        public object GetHmiTagTables(params object?[] args) => session.Invoke();
        public object GetHmiTags(params object?[] args) => session.Invoke();
        public object ImportHmiConnection(params object?[] args) => session.Invoke();
        public void CheckScreenImportTarget(string softwarePath, string folderPath) => session.Invoke();
        public object ImportHmiScreen(params object?[] args) => session.Invoke();
        public object ImportHmiScreensFromDirectory(params object?[] args) => session.Invoke();
        public object ImportHmiTagTable(params object?[] args) => session.Invoke();
        public object ImportHmiTagTablesFromDirectory(params object?[] args) => session.Invoke();
    }
    internal sealed class HmiInspectionPortAdapter(HmiSession session)
    {
        public object ListHmiScreenPaths(params object?[] args) => session.Invoke();
        public object ReadHmiScreenSnapshot(params object?[] args) => session.Invoke();
    }
    internal sealed class HmiTagDeletionAdapter(HmiSession session)
    {
        public object DeleteHmiTag(params object?[] args) => session.Invoke();
    }
    internal sealed class ClassicHmiFoldersAdapter(HmiSession session)
    {
        public object ManageClassicHmiFolder(params object?[] args) => session.Invoke();
        public object ManageClassicHmiGraphic(params object?[] args) => session.Invoke();
        public object ManageClassicHmiScreenObject(params object?[] args) => session.Invoke();
        public object ReadClassicHmiScreenTree(params object?[] args) => session.Invoke();
    }
    internal sealed class MotionProDiagClassicHmiAdapter(HmiSession session)
    {
        public object ManageClassicHmiCycle(params object?[] args) => session.Invoke();
        public object ManageClassicHmiScript(params object?[] args) => session.Invoke();
        public object ManageClassicHmiTextGraphicList(params object?[] args) => session.Invoke();
        public object ReadClassicHmiFaceplates(params object?[] args) => session.Invoke();
        public object ReadClassicHmiGlobalization(params object?[] args) => session.Invoke();
        public object ReadClassicHmiScripts(params object?[] args) => session.Invoke();
    }
}

namespace TiaMcp.Adapters.Native.Plc
{
    internal interface IPlcOrganisationSession { public FixtureSoftwareContainer? GetSoftwareContainer(string path); }
    internal sealed class FixtureSoftwareContainer { internal object? Software; }
    internal static class PlcGroupOperations
    {
        internal static object Get(object owner, string name) => throw new NotSupportedException();
        internal static object? Find(object owner, string name) => throw new NotSupportedException();
    }
}

namespace Siemens.Engineering
{
    public sealed class NonRecoverableException : Exception { }
}
