using TiaMcpServer.Siemens.Services;
using System.Linq;

namespace TiaMcp.Engine.Tests
{
    internal static class HmiToolFixture
    {
        internal static FakeHmiToolSession Session { get; private set; } = new FakeHmiToolSession();
        internal static HmiInspectionService HmiInspection { get; private set; } = new HmiInspectionService(CallUnified, () => "fixture");
        internal static HmiInspectionPortService HmiInspectionPort { get; } = new HmiInspectionPortService(CallInspection, () => "fixture");
        internal static MigrationReadService MigrationRead { get; private set; } = HmiToolFixture.CreateMigrationRead(Session);
        internal static RuntimeSettingsService RuntimeSettings { get; private set; } = HmiToolFixture.CreateRuntimeSettings(Session);
        internal static GraphicSelectionService GraphicSelection { get; private set; } = HmiToolFixture.CreateGraphicSelection(Session);
        internal static GlobalScriptEditService GlobalScriptEdit { get; private set; } = HmiToolFixture.CreateGlobalScriptEdit(Session);
        internal static MigrationReadService CreateMigrationRead(FakeHmiToolSession session)
            => new MigrationReadService((operation, args) => CallUnified(session, args), () => "fixture");
        internal static RuntimeSettingsService CreateRuntimeSettings(FakeHmiToolSession session)
            => new RuntimeSettingsService((operation, args) => CallUnified(session, args), () => "fixture");
        internal static GraphicSelectionService CreateGraphicSelection(FakeHmiToolSession session)
            => new GraphicSelectionService((operation, args) => CallUnified(session, args), () => "fixture");
        internal static GlobalScriptEditService CreateGlobalScriptEdit(FakeHmiToolSession session)
            => new GlobalScriptEditService((operation, args) => CallUnified(session, args), () => "fixture");
        private static System.Text.Json.Nodes.JsonNode? CallUnified(FakeHmiToolSession fixture, System.Text.Json.Nodes.JsonObject args)
        {
            var request = args["request"] == null
                ? new TiaMcp.Adapters.Contracts.UnifiedRequest { Operation = "ReleaseUnifiedReadCursor", Cursor = (string)args["cursor"]! }
                : System.Text.Json.JsonSerializer.Deserialize<TiaMcp.Adapters.Contracts.UnifiedRequest>(args["request"]!.ToJsonString())!;
            TiaMcpServer.Siemens.UnifiedPrimitiveCompatibility.Configure();
            TiaMcp.Adapters.Native.Hmi.UnifiedRequestValues.Inputs = request.Inputs.ToDictionary(p => p.Key,
                p => TiaMcp.Adapters.Native.Hmi.UnifiedRequestValues.Decode(p.Value));
            var session = new UnifiedSession(fixture);
            object value = request.Operation switch
            {
                "ListUnifiedGlobalScripts" => new TiaMcp.Adapters.Native.Hmi.MigrationReadAdapter(session).ListUnifiedGlobalScripts(request.SoftwarePath, request.ExpectedProject, request.Cursor, request.PageSize, request.BudgetMs),
                "GetUnifiedGlobalScript" => new TiaMcp.Adapters.Native.Hmi.MigrationReadAdapter(session).ReadUnifiedGlobalScript(request.SoftwarePath, request.ExpectedProject, request.ModuleName, request.Cursor, request.PageSize, request.BudgetMs),
                "ListUnifiedTagDefinitions" => new TiaMcp.Adapters.Native.Hmi.MigrationReadAdapter(session).ReadUnifiedTagDefinitions(request.SoftwarePath, request.ExpectedProject, request.Cursor, request.PageSize, request.BudgetMs),
                "GetUnifiedScreenBranch" => new TiaMcp.Adapters.Native.Hmi.MigrationReadAdapter(session).ReadUnifiedScreenBranch(request.SoftwarePath, request.ExpectedProject, request.ScreenPath, request.ItemName, request.BranchJson, request.Cursor, request.PageSize, request.BudgetMs),
                "GetUnifiedLibraryType" => new TiaMcp.Adapters.Native.Hmi.MigrationReadAdapter(session).ReadUnifiedLibraryType(request.SoftwarePath, request.ExpectedProject, request.TypePath, request.Version, request.Cursor, request.PageSize, request.BudgetMs),
                "ListUnifiedLibraryFolderEntries" => new TiaMcp.Adapters.Native.Hmi.MigrationReadAdapter(session).ListUnifiedLibraryFolder(request.SoftwarePath, request.ExpectedProject, request.FolderPath, request.Cursor, request.PageSize, request.BudgetMs),
                "GetUnifiedFaceplateInstance" => new TiaMcp.Adapters.Native.Hmi.MigrationReadAdapter(session).ReadUnifiedFaceplateInstance(request.SoftwarePath, request.ExpectedProject, request.ScreenPath, request.ItemName, request.TypePath, request.Version, request.Cursor, request.PageSize, request.BudgetMs),
                "ReleaseUnifiedReadCursor" => new TiaMcp.Adapters.Native.Hmi.MigrationReadAdapter(session).ReleaseUnifiedReadCursor(request.Cursor),
                "GetUnifiedGraphicSelection" => new TiaMcp.Adapters.Native.Hmi.GraphicSelectionAdapter(session, new TiaMcp.Adapters.Native.Hmi.MigrationReadAdapter(session)).ReadUnifiedGraphicSelection(request.SoftwarePath, request.ExpectedProject, request.ScreenPath, request.ItemNamesJson, request.Cursor, request.PageSize, request.BudgetMs),
                "GetUnifiedRuntimeSettings" => new TiaMcp.Adapters.Native.Hmi.RuntimeSettingsAdapter(session).ReadUnifiedRuntimeSettings(request.SoftwarePath, request.ExpectedProject, request.FieldsJson),
                "SetUnifiedRuntimeSettings" => new TiaMcp.Adapters.Native.Hmi.RuntimeSettingsAdapter(session).UpdateUnifiedRuntimeSettings(request.SoftwarePath, request.ExpectedProject, request.ChangesJson, request.DryRun, request.ExpectedToken),
                "SetUnifiedGlobalScript" => new TiaMcp.Adapters.Native.Hmi.GlobalScriptEditAdapter(session).UpdateUnifiedGlobalScript(request.SoftwarePath, request.ExpectedProject, request.ModuleName, request.ScriptCode, request.DryRun, request.ExpectedToken),
                _ => throw new System.ArgumentException(request.Operation)
            };
            return System.Text.Json.JsonSerializer.SerializeToNode(new TiaMcp.Adapters.Contracts.HmiReply { Value = value });
        }
        private sealed class UnifiedSession(FakeHmiToolSession fixture) : TiaMcp.Adapters.Native.Hmi.IUnifiedSession
        {
            public object? CurrentProject => fixture.CurrentProject;
            public object? HmiReadFault => fixture.HmiReadFault == null ? null : Native(fixture.HmiReadFault);
            public TiaMcp.Adapters.Native.Hmi.MigrationPages MigrationPages => (TiaMcp.Adapters.Native.Hmi.MigrationPages)fixture.MigrationPages.state;
            public object ResolveHmiSoftwareOrThrow(string path) => fixture.ResolveHmiSoftwareOrThrow(path);
            public object ExactUnifiedRoot(string path) => ResolveHmiSoftwareOrThrow(path);
            public object ExactPlantViews() => throw new System.NotSupportedException("Fixture has no plant views.");
            public string UnifiedCollection(string category) => throw new System.NotSupportedException(category);
            public System.IDisposable AcquireHmiEditAccess() => fixture.AcquireHmiEditAccess();
            public void RecordHmiReadFault(TiaMcp.Adapters.Native.Hmi.HmiObject values)
            {
                var meta = System.Text.Json.JsonSerializer.SerializeToNode(values)!.AsObject();
                fixture.RecordHmiReadFault(meta);
                values.Clear(); foreach (var pair in Native(meta)) values[pair.Key] = pair.Value;
            }
            public TiaMcp.Adapters.Contracts.HmiMessage RunHmiStepTool(string tool, System.Func<TiaMcp.Adapters.Native.Hmi.HmiObject, string> action, bool requiresProject = true)
            {
                var response = fixture.RunHmiStepTool(tool, meta => {
                    var values = Native(meta);
                    try { return action(values); }
                    finally { foreach (var pair in values) meta[pair.Key] = System.Text.Json.JsonSerializer.SerializeToNode(pair.Value); }
                }, requiresProject);
                return new TiaMcp.Adapters.Contracts.HmiMessage { Message = response.Message, Meta = Native(response.Meta!) };
            }
            private static TiaMcp.Adapters.Native.Hmi.HmiObject Native(System.Text.Json.Nodes.JsonObject value)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(value.ToJsonString());
                return (TiaMcp.Adapters.Native.Hmi.HmiObject)TiaMcp.Adapters.Native.Hmi.UnifiedRequestValues.Decode(UnifiedInputCodec.Decode(doc.RootElement))!;
            }
        }

        internal static void Configure(FakeHmiToolSession session)
        {
            Session = session;
            HmiInspection = new HmiInspectionService(CallUnified, () => "fixture");
            MigrationRead = HmiToolFixture.CreateMigrationRead(session);
            RuntimeSettings = HmiToolFixture.CreateRuntimeSettings(session);
            GraphicSelection = HmiToolFixture.CreateGraphicSelection(session);
            GlobalScriptEdit = HmiToolFixture.CreateGlobalScriptEdit(session);
            ToolBridgeFixture.Configure();
        }
        private static System.Text.Json.Nodes.JsonNode? CallUnified(string operation, System.Text.Json.Nodes.JsonObject args)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<TiaMcp.Adapters.Contracts.UnifiedHmiRequest>(args["request"]!.ToJsonString())!;
            var previous = TiaMcp.Adapters.Native.Hmi.HmiValues.Renderer;
            var token = TiaMcp.Adapters.Native.Hmi.HmiExactAccess.ContentToken;
            TiaMcp.Adapters.Native.Hmi.HmiValues.Renderer = values => System.Text.Json.JsonSerializer.Serialize(values);
            TiaMcp.Adapters.Native.Hmi.HmiExactAccess.ContentToken = (scope, values) => TiaMcpServer.Siemens.HmiExactAccess.Token(scope, System.Text.Json.JsonSerializer.SerializeToNode(values)!);
            try
            {
                var adapter = new TiaMcp.Adapters.Native.Hmi.UnifiedHmiInspectionAdapter(new InspectionSession());
                var value = request.Operation switch
                {
                    "GetUnifiedHmiButtonEvent" => adapter.ReadUnifiedHmiButtonEvent(request.SoftwarePath, request.ScreenPath, request.ButtonName, request.EventType),
                    "DeleteUnifiedHmiButtonEvent" => adapter.DeleteUnifiedHmiButtonEvent(request.SoftwarePath, request.ScreenPath, request.ButtonName, request.EventType, request.DryRun, request.ExpectedToken, request.OnlyIfEmpty),
                    "GetUnifiedHmiDynamization" => adapter.ReadUnifiedHmiDynamization(request.SoftwarePath, request.ScreenPath, request.ItemName, request.PropertyName),
                    "DeleteUnifiedHmiDynamization" => adapter.DeleteUnifiedHmiDynamization(request.SoftwarePath, request.ScreenPath, request.ItemName, request.PropertyName, request.DryRun, request.ExpectedToken),
                    "DeleteEmptyUnifiedHmiScreenGroup" => adapter.DeleteEmptyUnifiedHmiScreenGroup(request.SoftwarePath, request.GroupPath, request.DryRun),
                    _ => throw new System.InvalidOperationException("Unexpected Unified HMI fixture operation: " + operation)
                };
                return System.Text.Json.JsonSerializer.SerializeToNode(new TiaMcp.Adapters.Contracts.HmiReply { Value = value });
            }
            finally { TiaMcp.Adapters.Native.Hmi.HmiValues.Renderer = previous; TiaMcp.Adapters.Native.Hmi.HmiExactAccess.ContentToken = token; }
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
        private sealed class InspectionSession : TiaMcp.Adapters.Native.Hmi.IUnifiedHmiInspectionSession
        {
            public System.IDisposable AcquireHmiEditAccess() => Session.AcquireHmiEditAccess();
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
                    catch (TiaMcp.Adapters.Contracts.PlcSoftwareException error)
                    {
                        throw new TiaMcpServer.Siemens.PortalException(
                            (TiaMcpServer.Siemens.PortalErrorCode)System.Enum.Parse(typeof(TiaMcpServer.Siemens.PortalErrorCode), error.Code), error.Message);
                    }
                    finally { foreach (var pair in values) meta[pair.Key] = System.Text.Json.JsonSerializer.SerializeToNode(pair.Value); }
                }, requiresProject);
                return new TiaMcp.Adapters.Contracts.HmiMessage { Message = response.Message, Meta = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object?>>(response.Meta!.ToJsonString())! };
            }
        }
    }
}
