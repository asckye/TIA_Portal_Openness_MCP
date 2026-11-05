using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public string ReleaseKey => "21";
        public HmiMessage NativeReply { get; set; } = new();
        public bool NativeMayHaveChanged { get; set; }
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
