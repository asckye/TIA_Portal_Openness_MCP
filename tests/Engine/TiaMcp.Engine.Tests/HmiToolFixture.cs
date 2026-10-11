using TiaMcpServer.Siemens.Services;

namespace TiaMcp.Engine.Tests
{
    internal static class HmiToolFixture
    {
        internal static FakeHmiToolSession Session { get; private set; } = new FakeHmiToolSession();
        internal static HmiInspectionService HmiInspection { get; private set; } = new HmiInspectionService(Session);
        internal static HmiInspectionPortService HmiInspectionPort { get; } = new HmiInspectionPortService(CallInspection, () => "fixture");
        internal static MigrationReadService MigrationRead { get; private set; } = new MigrationReadService(Session);
        internal static RuntimeSettingsService RuntimeSettings { get; private set; } = new RuntimeSettingsService(Session);
        internal static GraphicSelectionService GraphicSelection { get; private set; } = new GraphicSelectionService(Session, MigrationRead);
        internal static GlobalScriptEditService GlobalScriptEdit { get; private set; } = new GlobalScriptEditService(Session);

        internal static void Configure(FakeHmiToolSession session)
        {
            Session = session;
            HmiInspection = new HmiInspectionService(session);
            MigrationRead = new MigrationReadService(session);
            RuntimeSettings = new RuntimeSettingsService(session);
            GraphicSelection = new GraphicSelectionService(session, MigrationRead);
            GlobalScriptEdit = new GlobalScriptEditService(session);
            ToolBridgeFixture.Configure();
        }
        private static System.Text.Json.Nodes.JsonNode? CallInspection(string operation, System.Text.Json.Nodes.JsonObject args)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<TiaMcp.Adapters.Contracts.HmiRequest>(args["request"]!.ToJsonString())!;
            var previous = TiaMcp.Adapters.Native.Hmi.HmiValues.Renderer;
            var previousTraceWriter = TiaMcp.Adapters.Native.Hmi.HmiValues.TraceWriter;
            TiaMcp.Adapters.Native.Hmi.HmiValues.Renderer = values => System.Text.Json.JsonSerializer.Serialize(values);
            TiaMcp.Adapters.Native.Hmi.HmiValues.TraceWriter = TiaMcpServer.Siemens.HmiReadTrace.WriteLine;
            try
            {
                var adapter = new TiaMcp.Adapters.Native.Hmi.HmiInspectionPortAdapter(new InspectionSession());
                var value = request.Operation == "GetHmiScreenSnapshot" ? adapter.ReadHmiScreenSnapshot(request.SoftwarePath, request.ScreenPath, request.MaxDepth, request.MaxNodes)
                    : adapter.ListHmiScreenPaths(request.SoftwarePath, request.Offset, request.Limit);
                return System.Text.Json.JsonSerializer.SerializeToNode(new TiaMcp.Adapters.Contracts.HmiReply { Value = value });
            }
            finally { TiaMcp.Adapters.Native.Hmi.HmiValues.Renderer = previous; TiaMcp.Adapters.Native.Hmi.HmiValues.TraceWriter = previousTraceWriter; }
        }
        private sealed class InspectionSession : TiaMcp.Adapters.Native.Hmi.IHmiInspectionSession
        {
            public object ResolveHmiSoftwareOrThrow(string path) => Session.ResolveHmiSoftwareOrThrow(path);
            public void RecordHmiReadFault(TiaMcp.Adapters.Native.Hmi.HmiObject values)
            {
                var meta = System.Text.Json.JsonSerializer.SerializeToNode(values)!.AsObject();
                Session.RecordHmiReadFault(meta);
                foreach (var pair in meta) values[pair.Key] = pair.Value == null ? null : System.Text.Json.JsonSerializer.Deserialize<object>(pair.Value.ToJsonString());
            }
            public TiaMcp.Adapters.Contracts.HmiMessage RunHmiStepTool(string tool, System.Func<TiaMcp.Adapters.Native.Hmi.HmiObject, string> action, bool requiresProject = true)
            {
                var response = Session.RunHmiStepTool(tool, meta => {
                    var values = new TiaMcp.Adapters.Native.Hmi.HmiObject();
                    foreach (var pair in meta) values[pair.Key] = pair.Value == null ? null : System.Text.Json.JsonSerializer.Deserialize<object>(pair.Value.ToJsonString());
                    try { return action(values); }
                    finally { foreach (var pair in values) meta[pair.Key] = System.Text.Json.JsonSerializer.SerializeToNode(pair.Value); }
                }, requiresProject);
                return new TiaMcp.Adapters.Contracts.HmiMessage { Message = response.Message, Meta = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object?>>(response.Meta!.ToJsonString())! };
            }
        }
    }
}
