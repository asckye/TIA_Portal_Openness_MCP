using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// E1/E3: all response types, options and executors come from the selected woven EXE.
// The actions below contain only in-memory data; no session is constructed or connected.
internal sealed class ResponseGoldenTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private readonly Assembly server;
    private readonly EngineSurface surface;
    private readonly JsonSerializerOptions bridge;
    private readonly JsonSerializerOptions discipline;
    private readonly JsonSerializerOptions sdk;
    private readonly List<KeyValuePair<string, string>> records = new List<KeyValuePair<string, string>>();

    private ResponseGoldenTests(Assembly server)
    {
        this.server = server;
        surface = EngineSurface.For(server);
        var tools = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        bridge = (JsonSerializerOptions)tools.GetField("BridgeJson", All)!.GetValue(null)!;
        discipline = (JsonSerializerOptions)tools.GetField("DisciplineJson", All)!.GetValue(null)!;
        // McpServerToolCreateOptions.SerializerOptions defaults to this SDK singleton.
        sdk = (JsonSerializerOptions)typeof(McpServerTool).Assembly.GetType("ModelContextProtocol.McpJsonUtilities", true)!
            .GetProperty("DefaultOptions", All)!.GetValue(null)!;
    }

    internal static void Run(Assembly server, string directory, bool record, Action<bool, string> check)
    {
        // Keep Local DateTime values Local, with a deterministic offset on every
        // test machine. This changes only this net48 process's BCL cache, restored
        // below; it does not change Windows timezone or any engine options.
        var cached = typeof(TimeZoneInfo).GetField("s_cachedData", All)!.GetValue(null)!;
        var local = cached.GetType().GetField("m_localTimeZone", All)!;
        var previous = local.GetValue(cached);
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            local.SetValue(cached, TimeZoneInfo.CreateCustomTimeZone("ResponseGolden", TimeSpan.FromHours(8), "ResponseGolden", "ResponseGolden"));
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            check(TimeZoneInfo.Local.GetUtcOffset(FixedDate(DateTimeKind.Local)) == TimeSpan.FromHours(8), "golden process local offset is +08:00");
            var tests = new ResponseGoldenTests(server);
            tests.CheckMasks(check);
            tests.Serializers();
            tests.Executors(check);
            int version = server.GetReferencedAssemblies().First(a => a.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)).Version!.Major;
            string path = Path.Combine(directory, "response-bytes-v" + version + ".txt");
            var actual = Encoding.UTF8.GetBytes(string.Concat(tests.records.Select(pair => pair.Key + "\t" + pair.Value + "\n")));
            if (record)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(path, actual);
                Console.WriteLine("RECORDED " + tests.records.Count + " exact UTF-8 responses: " + path);
                return;
            }
            var expectedBytes = File.ReadAllBytes(path);
            var expected = File.ReadAllLines(path, new UTF8Encoding(false, true));
            check(expected.Length == tests.records.Count, "golden case count " + tests.records.Count);
            for (int i = 0; i < tests.records.Count; i++)
            {
                var pair = tests.records[i];
                var line = Encoding.UTF8.GetBytes(pair.Key + "\t" + pair.Value);
                check(line.SequenceEqual(Encoding.UTF8.GetBytes(expected[i])), "golden bytes " + pair.Key);
            }
            check(actual.SequenceEqual(expectedBytes), "complete golden file bytes including BOM/newlines");
        }
        finally
        {
            local.SetValue(cached, previous);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    private static DateTime FixedDate(DateTimeKind kind) => new DateTime(2001, 2, 3, 4, 5, 6, kind).AddTicks(1234567);
    private Type Type(string name) => Program.FindServerType(server, "TiaMcpServer." + name);
    private object Response(string name, params (string Name, object? Value)[] values)
    {
        var type = Type("ModelContextProtocol." + name);
        var response = Activator.CreateInstance(type)!;
        foreach (var value in values) type.GetProperty(value.Name)!.SetValue(response, value.Value);
        return response;
    }
    private static JsonObject Meta(object response) => (JsonObject)response.GetType().GetProperty("Meta")!.GetValue(response)!;
    private static string Serialize(object value, JsonSerializerOptions options) => JsonSerializer.Serialize(value, value.GetType(), options);
    private void Add(string name, string value) => records.Add(new KeyValuePair<string, string>(name, value));

    private void Serializers()
    {
        var meta = new JsonObject
        {
            ["timestamp"] = FixedDate(DateTimeKind.Local), ["success"] = true,
            ["中文"] = "画面 <>& '\" / \\ \n 😀", ["nothing"] = null,
            ["utc"] = FixedDate(DateTimeKind.Utc), ["int"] = 42,
            ["long"] = 9007199254740993L, ["double"] = 1.2345678901234567d,
            ["wholeDouble"] = 1.0d, ["exponent"] = 1.25e-20d,
            ["nested"] = new JsonArray(new JsonArray(1, "中文", null), new JsonObject { ["empty"] = new JsonArray() })
        };
        var values = new (string Name, object Value)[]
        {
            ("poco-null", Response("ResponseXmlBuild", ("Ok", false), ("Message", "中文响应"), ("Meta", meta.DeepClone()))),
            ("poco-populated", Response("ResponseXmlBuild", ("Ok", true), ("Message", "完成"), ("Meta", meta.DeepClone()),
                ("Xml", "<root name=\"中文\" />"), ("Data", new JsonObject { ["ok"] = true }),
                ("Errors", new[] { "error", "中文" }), ("Warnings", new[] { "warning" }))),
            ("meta", meta), ("local", FixedDate(DateTimeKind.Local)), ("utc", FixedDate(DateTimeKind.Utc)),
            ("int", -42), ("long", long.MaxValue), ("double", 1.2345678901234567d),
            ("arrays", new JsonArray(new JsonArray(1, 2, 3), null, new JsonArray("中文", true)))
        };
        foreach (var value in values)
        {
            Add("serializer/" + value.Name + "/sdk", Serialize(value.Value, sdk));
            Add("serializer/" + value.Name + "/bridge", Serialize(value.Value, bridge));
            Add("serializer/" + value.Name + "/discipline", Serialize(value.Value, discipline));
            // ToJsonString is a node API; wrap scalars/POCOs in JsonValue, leaving
            // JsonObject/JsonArray untouched, and supply no serializer options.
            Add("serializer/" + value.Name + "/node", (value.Value as JsonNode ?? JsonValue.Create(value.Value)!).ToJsonString());
        }
    }

    // Closed E3 mask list. These paths alone contain volatile evidence. Replacing
    // a JsonValue<DateTime> with text would hide a serialization regression.
    private static void MaskMeta(JsonObject meta, bool includeLastFailure = true)
    {
        MaskTimestamp(meta, "timestamp"); // executor/ToolBridgeStatus wall clock
        if (meta["elapsedMs"] is JsonValue elapsed)
        {
            if (!elapsed.TryGetValue<long>(out _)) throw new Exception("elapsedMs lost Int64 type");
            meta["elapsedMs"] = 0L; // RunPlcSimTool Stopwatch.ElapsedMilliseconds
        }
        if (meta["operationId"] is JsonValue id)
        {
            if (!id.TryGetValue<string>(out var value) || !Guid.TryParse(value, out _)) throw new Exception("operationId lost GUID string type");
            meta["operationId"] = "00000000-0000-0000-0000-000000000000";
        }
        if (meta["error"] is JsonValue error)
        {
            var text = error.GetValue<string>();
            meta["error"] = text.Split(new[] { '\r', '\n' }, 2)[0]; // D1: exception type/message first line only
        }
        if (includeLastFailure && meta["lastFailure"] is JsonObject failure) MaskMeta(failure, false);
    }

    private static void MaskTimestamp(JsonObject meta, string key)
    {
        if (meta[key] == null) return;
        if (!(meta[key] is JsonValue value) || !value.TryGetValue<DateTime>(out var stamp))
            throw new Exception(key + " lost DateTime type");
        meta[key] = FixedDate(stamp.Kind);
    }

    private void CheckMasks(Action<bool, string> check)
    {
        foreach (var kind in new[] { DateTimeKind.Local, DateTimeKind.Utc, DateTimeKind.Unspecified })
        {
            var meta = new JsonObject { ["timestamp"] = new DateTime(2026, 10, 3, 1, 2, 3, kind),
                ["elapsedMs"] = 123L, ["operationId"] = Guid.NewGuid().ToString(), ["error"] = "Type: 消息\r\nstack",
                ["data"] = new JsonObject { ["timestamp"] = "unchanged", ["elapsedMs"] = 321, ["error"] = "a\nb" } };
            string data = meta["data"]!.ToJsonString();
            MaskMeta(meta);
            check(meta["timestamp"]!.GetValue<DateTime>().Kind == kind && meta["timestamp"]!.GetValue<DateTime>() == FixedDate(kind), "timestamp sentinel retains DateTime " + kind);
            check(((JsonValue)meta["elapsedMs"]!).TryGetValue<long>(out var ms) && ms == 0 && meta["error"]!.GetValue<string>() == "Type: 消息"
                && meta["operationId"]!.GetValue<string>() == "00000000-0000-0000-0000-000000000000" && meta["data"]!.ToJsonString() == data,
                "E3 masks only reviewed paths " + kind);
        }
    }

    private void Capture(string name, object response)
    {
        MaskMeta(Meta(response));
        Add("executor/" + name + "/direct", Serialize(response, sdk));
        Add("executor/" + name + "/bridge", Serialize(response, bridge));
    }

    private Exception PortalError() => (Exception)Activator.CreateInstance(Type("Siemens.PortalException"),
        Enum.Parse(Type("Siemens.PortalErrorCode"), "NotFound"), "synthetic portal failure", new[] { "候选" }, null)!;

    private void Executors(Action<bool, string> check)
    {
        var actions = new (string Name, Func<JsonObject, string> Action)[]
        {
            ("success", meta => { meta["rows"] = new JsonArray(1, "中文", null); return "synthetic success 中文"; }),
            ("operation-false", meta => { meta["operationSuccess"] = false; return "synthetic business refusal"; }),
            ("argument", meta => throw new ArgumentException("synthetic argument failure")),
            ("portal", meta => throw PortalError()),
            ("target-invocation", meta => throw new TargetInvocationException("synthetic invocation failure", new ArgumentException("synthetic inner failure")))
        };
        var hmi = surface.Method("RunHmiStepTool", All);
        var portal = FormatterServices.GetUninitializedObject(hmi.DeclaringType!);
        foreach (var action in actions)
        {
            Capture("hmi/" + action.Name, hmi.Invoke(portal, new object[] { "GoldenHmi", action.Action, false })!);
            Capture("offline/" + action.Name, surface.Invoke(surface.ToolMethod("RunOfflineAnalysisTool"), new object[] { "GoldenOffline", action.Action })!);
            Capture("batch/" + action.Name, surface.Invoke(surface.ToolMethod("BatchResult"), new object[] { action.Action })!);
        }
        bool invoked = false;
        Func<JsonObject, string> unreachable = meta => { invoked = true; throw new Exception("Blocked action ran"); };
        Capture("hmi/no-project", hmi.Invoke(portal, new object[] { "GoldenHmi", unreachable, true })!);
        surface.Field("_hmiReadFault", All).SetValue(portal, new JsonObject
        {
            ["timestamp"] = DateTime.Now, ["operationId"] = Guid.NewGuid().ToString(),
            ["tool"] = "PreviousHmi", ["error"] = "SyntheticFault: disconnected\nold stack"
        });
        Capture("hmi/blocked", hmi.Invoke(portal, new object[] { "GoldenHmi", unreachable, false })!);
        check(!invoked, "HMI project/blocked refusals do not invoke action");

        var plc = surface.ToolMethod("RunPlcSimTool");
        var failures = new (string Name, Exception Error)[]
        {
            ("argument", new ArgumentException("synthetic argument failure")),
            ("unsupported", new NotSupportedException("synthetic unsupported")),
            ("api-missing", new InvalidOperationException("PLCSIM Advanced API (synthetic) unavailable")),
            ("other", new InvalidOperationException("synthetic failure")),
            ("target-invocation", new TargetInvocationException("synthetic invocation failure", new ArgumentException("synthetic inner failure")))
        };
        foreach (var failure in failures)
        {
            Func<JsonObject, JsonObject, string> body = (data, meta) => { data["retained"] = "中文"; throw failure.Error; };
            Capture("plcsim/" + failure.Name, surface.Invoke(plc, new object?[] { "GoldenPlcSim", null, body })!);
        }
        foreach (bool success in new[] { true, false })
        {
            Func<JsonObject, JsonObject, string> body = (data, meta) => { meta["operationSuccess"] = success; return "synthetic preview"; };
            Capture("plcsim/preview-" + success, surface.Invoke(plc, new object?[] { "GoldenPlcSim", true, body })!);
        }
        // Actual public refusal: RequireInstanceName precedes API load/acquire.
        Capture("plcsim/empty-instance", surface.Invoke(surface.Tool("ManagePlcSimAdvancedInstance"),
            new object[] { "", "run", "", 60000, false, true, "", "" })!);

        var builder = surface.ToolMethod("BuildOfflineXmlBuilderReport");
        foreach (var sample in new (string Name, JsonObject Data)[] {
            ("success", new JsonObject { ["ok"] = true, ["xml"] = "<root name=\"中文\" />", ["warnings"] = new JsonArray() }),
            ("single-error", new JsonObject { ["ok"] = false, ["error"] = "single error 中文" }),
            ("error-array", new JsonObject { ["ok"] = false, ["errors"] = new JsonArray("first", null, "second"), ["warnings"] = new JsonArray("warning", null) }),
            ("empty-arrays", new JsonObject { ["errors"] = new JsonArray(), ["warnings"] = new JsonArray() }) })
            Capture("xml/" + sample.Name, surface.Invoke(builder, new object[] { sample.Data, "synthetic XML" })!);

        var create = Type("ModelContextProtocol.ToolBridgeStatus").GetMethod("Create", All)!;
        int index = 0;
        foreach (bool completed in new[] { true, false })
            foreach (var operation in new JsonObject?[] { null, new JsonObject(), new JsonObject { ["success"] = true }, new JsonObject { ["success"] = false }, new JsonObject { ["success"] = "true" } })
            {
                var meta = (JsonObject)create.Invoke(null, new object?[] { completed, operation })!;
                Capture("bridge-status/" + index++, Response("ResponseMessage", ("Message", "synthetic bridge"), ("Meta", meta)));
            }
    }
}
