using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Rpc;
using Xunit;
using Legacy = TiaOpenness.Core.Tests.Legacy;

namespace TiaOpenness.Core.Tests;

public sealed class BridgeJsonGoldenTests
{
    private static JObject ReadGolden()
    {
        using var stream = typeof(BridgeJsonGoldenTests).Assembly.GetManifestResourceStream(
            "TiaOpenness.Core.Tests.BridgeJsonGolden.json")!;
        using var reader = new StreamReader(stream);
        return (JObject)Parse(reader.ReadToEnd());
    }

    internal static JToken Parse(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JToken.ReadFrom(reader);
    }

    internal static string OldJson(object? value) => JsonConvert.SerializeObject(value, Legacy.BridgeJson.Settings);

    private static Type DtoType(string name) => typeof(ProjectInfo).Assembly.GetTypes().Single(t => t.Name == name);
    private static Type OldType(Type type) => type.Namespace == typeof(RpcRequest).Namespace
        ? typeof(Legacy.RpcRequest).Assembly.GetType(typeof(Legacy.RpcRequest).Namespace + "." + type.Name)!
        : type;

    private static object? ReadNew(string json, Type type) => typeof(BridgeJson).GetMethods()
        .Single(m => m.Name == nameof(BridgeJson.Deserialize) && m.GetParameters()[0].ParameterType == typeof(string))
        .MakeGenericMethod(type).Invoke(null, new object[] { json });

    internal static object? Sample(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var item = Sample(type.GetGenericArguments()[0]);
            var array = Array.CreateInstance(type.GetGenericArguments()[0], 1);
            array.SetValue(item, 0);
            return array;
        }
        return JsonConvert.DeserializeObject(ReadGolden()[type.Name]!.ToString(), type, Legacy.BridgeJson.Settings);
    }

    public static IEnumerable<object[]> Dtos() => ReadGolden().Properties().Select(p => new object[] { p.Name });

    [Fact]
    public void Golden_inventory_includes_every_contract_DTO()
    {
        var types = typeof(ProjectInfo).Assembly.GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !typeof(Exception).IsAssignableFrom(t)).Select(t => t.Name).OrderBy(n => n);
        Assert.Equal(types, ReadGolden().Properties().Select(p => p.Name).OrderBy(n => n));
    }

    [Theory]
    [MemberData(nameof(Dtos))]
    public void Every_DTO_has_a_populated_default_and_null_golden_in_both_codecs(string name)
    {
        var type = DtoType(name);
        var oldType = OldType(type);
        var golden = ReadGolden()[name]!.ToString();
        foreach (var json in new[] { golden, OldJson(Activator.CreateInstance(oldType)), "null" })
        {
            var oldObject = JsonConvert.DeserializeObject(json, oldType, Legacy.BridgeJson.Settings);
            var newObject = ReadNew(json, type);
            var oldWire = OldJson(oldObject);
            var newWire = BridgeJson.Serialize(newObject);
            Assert.True(JToken.DeepEquals(Parse(oldWire), Parse(newWire)), name + "\n" + oldWire + "\n" + newWire);

            // Both receiving codecs accept either sender, with no offset/kind loss
            // at the direct DTO boundary. The old client's extra date pass is below.
            foreach (var wire in new[] { oldWire, newWire })
            {
                var oldRead = JsonConvert.DeserializeObject(wire, oldType, Legacy.BridgeJson.Settings);
                var newRead = ReadNew(wire, type);
                Assert.Equal(Parse(oldWire).ToString(), Parse(OldJson(oldRead)).ToString());
                Assert.Equal(Parse(newWire).ToString(), Parse(BridgeJson.Serialize(newRead)).ToString());
                if (type == oldType) EqualObjects(oldObject, newRead);
            }
        }
    }

    private static void EqualObjects(object? expected, object? actual)
    {
        if (expected == null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        if (expected is DateTimeOffset offset)
        {
            Assert.True(offset.EqualsExact(Assert.IsType<DateTimeOffset>(actual)));
        }
        else if (expected is DateTime date)
        {
            var value = Assert.IsType<DateTime>(actual);
            Assert.Equal(date.Ticks, value.Ticks);
            Assert.Equal(date.Kind, value.Kind);
        }
        else if (expected is string || expected.GetType().IsValueType) Assert.Equal(expected, actual);
        else if (expected is IEnumerable sequence)
        {
            var values = Assert.IsAssignableFrom<IEnumerable>(actual).Cast<object?>().ToArray();
            var originals = sequence.Cast<object?>().ToArray();
            Assert.Equal(originals.Length, values.Length);
            for (int i = 0; i < originals.Length; i++) EqualObjects(originals[i], values[i]);
        }
        else foreach (var property in expected.GetType().GetProperties())
            EqualObjects(property.GetValue(expected), property.GetValue(actual));
    }

    public static IEnumerable<object[]> Enums() => typeof(ProjectInfo).Assembly.GetExportedTypes()
        .Where(t => t.IsEnum).SelectMany(t => Enum.GetValues(t).Cast<object>()).Select(v => new[] { v });

    [Theory]
    [MemberData(nameof(Enums))]
    public void Enum_names_are_preserved(object value)
    {
        Assert.Equal("\"" + value + "\"", BridgeJson.Serialize(value));
        Assert.Equal(OldJson(value), BridgeJson.Serialize(value));
        Assert.Equal(value, ReadNew(OldJson(value), value.GetType()));
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void DateTime_keeps_ticks_kind_and_the_original_wire_format(DateTimeKind kind)
    {
        var value = new DateTime(2026, 1, 2, 3, 4, 5, kind).AddTicks(1234567);
        Assert.Equal(OldJson(value), BridgeJson.Serialize(value));
        EqualObjects(value, BridgeJson.Deserialize<DateTime>(OldJson(value)));
        EqualObjects(value, JsonConvert.DeserializeObject<DateTime>(BridgeJson.Serialize(value), Legacy.BridgeJson.Settings));
    }

    [Theory]
    [InlineData("2026-01-02T03:04:05.1234567Z")]
    [InlineData("2026-01-02T03:04:05+00:00")]
    [InlineData("2026-01-02T03:04:05+05:30")]
    [InlineData("2026-01-02T03:04:05-07:00")]
    [InlineData("2026-01-02T03:04:05")]
    public void Client_keeps_the_old_JObject_date_conversion(string date)
    {
        var json = "{\"CreationTime\":\"" + date + "\"}";
        var expected = JObject.Parse(json).ToObject<ProjectInfo>(Newtonsoft.Json.JsonSerializer.Create(Legacy.BridgeJson.Settings))!;
        var actual = BridgeJson.DeserializeClient<ProjectInfo>(BridgeJson.Deserialize<JsonElement>(json));
        EqualObjects(expected, actual);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"type\":\"NativeFailure\",\"stack\":\"original\\r\\nstack\",\"inner\":\"inner 轴\"}")]
    [InlineData("{\"type\":\"NativeFailure\",\"stack\":null}")]
    [InlineData("{\"type\":\"NativeFailure\",\"stack\":null,\"inner\":\"2026-01-02T03:04:05+05:30\"}")]
    public void Error_diagnostics_and_UI_message_match_the_old_client(string data)
    {
        var json = "{\"code\":-32002,\"message\":\"故障\",\"data\":" + data + "}";
        var expected = JObject.Parse(json).ToObject<Legacy.RpcError>(Newtonsoft.Json.JsonSerializer.Create(Legacy.BridgeJson.Settings))!;
        var actual = new BridgeRpcException("block.export", BridgeJson.Deserialize<RpcError>(json));
        Assert.Equal("block.export failed (-32002): 故障", actual.Message);
        Assert.Equal(expected.Data?.ToString(), actual.Data2);
        Assert.Equal(expected.Code, actual.Code);
        Assert.Equal("block.export", actual.Method);
        Assert.Equal(Parse(json).ToString(), Parse(BridgeJson.Serialize(BridgeJson.Deserialize<RpcError>(json))).ToString());
    }

    [Fact]
    public void Dispatcher_keeps_the_original_exception_type_stack_and_inner_message()
    {
        var failure = new FixedStackException("native 轴 failed", new Exception("inner failure"));
        Func<ITiaSessionFactory> factory = () => throw failure;
        using var old = new Legacy.RpcDispatcher(factory, null);
        using var current = new RpcDispatcher(factory, null);
        var expected = old.Handle(new Legacy.RpcRequest { Id = "1", Method = RpcMethods.Ping });
        var actual = current.Handle(new RpcRequest { Id = "1", Method = RpcMethods.Ping });
        Assert.Equal(RpcErrorCodes.OpennessFailure, actual.Error.Code);
        Assert.Equal(Parse(OldJson(expected)).ToString(), Parse(BridgeJson.Serialize(actual)).ToString());
        Assert.Equal(failure.StackTrace, actual.Error.Data!.Value.GetProperty("stack").GetString());
        Assert.Equal(failure.InnerException!.Message, actual.Error.Data.Value.GetProperty("inner").GetString());
    }

    private sealed class FixedStackException(string message, Exception inner) : Exception(message, inner)
    {
        public override string StackTrace => "original native stack\r\nnext frame";
    }

    [Fact]
    public void Success_null_is_explicit_and_error_and_success_remain_exclusive()
    {
        Assert.Equal(OldJson(Legacy.RpcResponse.Ok("1", null)), BridgeJson.Serialize(RpcResponse.Ok("1", null)));
        Assert.Equal(OldJson(Legacy.RpcResponse.Ok("1", null)),
            BridgeJson.Serialize(BridgeJson.Deserialize<RpcResponse>("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":null}")));
        Assert.Equal(OldJson(Legacy.RpcResponse.Fail("1", -32602, "missing")),
            BridgeJson.Serialize(RpcResponse.Fail("1", -32602, "missing")));
    }

    [Theory]
    [InlineData("\u001b", "\"\\u001b\"", "\"\\u001B\"")]
    [InlineData("😀", "\"😀\"", "\"\\uD83D\\uDE00\"")]
    [InlineData("\u00a0", "\"\u00a0\"", "\"\\u00A0\"")]
    public void Allowed_byte_differences_are_JSON_escapes_accepted_by_both_receivers(string value, string oldWire, string newWire)
    {
        Assert.Equal(oldWire, OldJson(value));
        Assert.Equal(newWire, BridgeJson.Serialize(value));
        Assert.Equal(value, BridgeJson.Deserialize<string>(oldWire));
        Assert.Equal(value, JsonConvert.DeserializeObject<string>(newWire, Legacy.BridgeJson.Settings));
    }

    public static IEnumerable<object[]> Methods() => typeof(RpcMethods).GetFields()
        .Where(f => f.IsLiteral).Select(f => new object[] { (string)f.GetRawConstantValue()! });

    public static IEnumerable<object[]> Parameters()
    {
        foreach (var value in new[] { "null", "\"\"", "\" \"", "\"TRUE\"", "true", "false", "0", "2.5", "\"bad\"", "[]", "{}", "[\"a\",null,\"\"]", "{\"x\":\"a\"}", "\"2026-01-02T03:04:05+05:30\"" })
        {
            yield return new object[] { "session.connect", "{\"withUserInterface\":" + value + "}" };
            yield return new object[] { "project.open", "{\"path\":" + value + "}" };
            yield return new object[] { "block.export", "{\"deviceId\":\"PLC_1\",\"outputDirectory\":\"D:\\\\export\",\"blocks\":" + value + "}" };
        }
        yield return new object[] { "block.export", "{\"deviceId\":\"PLC_1\",\"outputDirectory\":\"D:\\\\export\",\"format\":\"bad\"}" };
        yield return new object[] { "project.open", "{}" };
        yield return new object[] { "unknown", "{}" };
    }

    [Theory]
    [InlineData("2026-01-02T03:04:05Z")]
    [InlineData("2026-01-02T03:04:05+05:30")]
    public void Date_like_UI_strings_keep_the_previous_client_text(string value)
    {
        var json = "{\"Name\":\"" + value + "\"}";
        var expected = JObject.Parse(json).ToObject<ProjectInfo>(Newtonsoft.Json.JsonSerializer.Create(Legacy.BridgeJson.Settings))!;
        var actual = BridgeJson.DeserializeClient<ProjectInfo>(BridgeJson.Deserialize<JsonElement>(json));
        Assert.Equal(expected.Name, actual.Name);
    }

    [Theory]
    [MemberData(nameof(Parameters))]
    public void Parameter_defaults_coercions_and_error_texts_match_the_old_dispatcher(string method, string parameters)
    {
        var oldCalls = new List<string>();
        var newCalls = new List<string>();
        using var old = new Legacy.RpcDispatcher(() => RecordingBackend.Create<ITiaSessionFactory>(oldCalls), null);
        using var current = new RpcDispatcher(() => RecordingBackend.Create<ITiaSessionFactory>(newCalls), null);
        old.Handle(new Legacy.RpcRequest { Method = RpcMethods.SessionConnect });
        current.Handle(new RpcRequest { Method = RpcMethods.SessionConnect });
        var expected = old.Handle(new Legacy.RpcRequest { Method = method, Params = JsonConvert.DeserializeObject<JObject>(parameters, Legacy.BridgeJson.Settings) });
        var actual = current.Handle(new RpcRequest { Method = method, Params = BridgeJson.Deserialize<JsonElement>(parameters) });
        Assert.Equal(expected.Error?.Code, actual.Error?.Code);
        Assert.Equal(expected.Error?.Message, actual.Error?.Message);
        Assert.Equal(expected.Error?.Data?["type"]?.ToString(), actual.Error?.Data?.GetProperty("type").GetString());
        Assert.Equal(oldCalls, newCalls);
        Assert.Equal(Parse(OldJson(expected.Result)).ToString(), Parse(BridgeJson.Serialize(actual.Result)).ToString());
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public void Every_RPC_method_matches_the_old_request_result_arguments_and_progress(string method)
    {
        const string parameters = "{\"withUserInterface\":false,\"attachToRunning\":false,\"version\":\"21\",\"path\":\"Line.ap21\",\"deviceId\":\"PLC_1\",\"includeSystemBlocks\":true,\"blocks\":[\"Motion/轴\",null,\"\"],\"files\":[\"轴.xml\"],\"outputDirectory\":\"D:\\\\export\",\"format\":\"Source\",\"preserveFolders\":false,\"overwrite\":true,\"tableName\":\"Default\",\"softwareOnly\":false,\"name\":\"LineGit\",\"folderPath\":\"D:\\\\git\",\"workspaceName\":\"LineGit\",\"file\":null,\"dryRun\":false,\"changedOnly\":false,\"direction\":\"WorkspaceToProject\",\"blockNamePattern\":\"^FB_\",\"requireBlockComment\":false,\"findUnusedBlocks\":false,\"flagInconsistentBlocks\":false}";
        var oldCalls = new List<string>();
        var newCalls = new List<string>();
        var oldProgress = new List<string>();
        var newProgress = new List<string>();
        using var old = new Legacy.RpcDispatcher(() => RecordingBackend.Create<ITiaSessionFactory>(oldCalls), n => oldProgress.Add(OldJson(n)));
        using var current = new RpcDispatcher(() => RecordingBackend.Create<ITiaSessionFactory>(newCalls), n => newProgress.Add(BridgeJson.Serialize(n)));
        old.Handle(new Legacy.RpcRequest { Method = RpcMethods.SessionConnect });
        current.Handle(new RpcRequest { Method = RpcMethods.SessionConnect });
        var oldRequest = new Legacy.RpcRequest { Id = "17", Method = method, Params = JsonConvert.DeserializeObject<JObject>(parameters, Legacy.BridgeJson.Settings) };
        var newRequest = BridgeJson.Deserialize<RpcRequest>(OldJson(oldRequest));
        var oldWire = OldJson(oldRequest);
        var newWire = BridgeJson.Serialize(newRequest);
        Assert.Equal(Parse(oldWire).ToString(), Parse(newWire).ToString());
        var oldResponse = old.Handle(JsonConvert.DeserializeObject<Legacy.RpcRequest>(newWire, Legacy.BridgeJson.Settings)!);
        var newResponse = current.Handle(BridgeJson.Deserialize<RpcRequest>(oldWire));
        Assert.Null(oldResponse.Error);
        Assert.Null(newResponse.Error);
        var expected = Parse(OldJson(oldResponse));
        var actual = Parse(BridgeJson.Serialize(newResponse));
        if (method == RpcMethods.DoctorRun)
        {
            // Doctor samples the current clock once per call; its DTO has a fixed golden above.
            Assert.NotNull(actual["result"]!["TimestampUtc"]);
            actual["result"]!["TimestampUtc"] = expected["result"]!["TimestampUtc"];
        }
        Assert.Equal(expected.ToString(), actual.ToString());
        Assert.Equal(oldCalls, newCalls);
        Assert.Equal(oldProgress.Select(p => Parse(p).ToString()), newProgress.Select(p => Parse(p).ToString()));
        Assert.Equal(Parse(newWire).ToString(), Parse(OldJson(JsonConvert.DeserializeObject<Legacy.RpcRequest>(newWire, Legacy.BridgeJson.Settings))).ToString());
        Assert.Equal(expected.ToString(), Parse(OldJson(JsonConvert.DeserializeObject<Legacy.RpcResponse>(expected.ToString(), Legacy.BridgeJson.Settings))).ToString());
        Assert.Equal(actual.ToString(), Parse(BridgeJson.Serialize(BridgeJson.Deserialize<RpcResponse>(actual.ToString()))).ToString());
    }

    public class RecordingBackend : DispatchProxy
    {
        private List<string> calls = null!;

        public static T Create<T>(List<string> calls) where T : class
        {
            var proxy = Create<T, RecordingBackend>();
            ((RecordingBackend)(object)proxy).calls = calls;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            calls.Add(method.Name + ":" + OldJson(args!.Where(a => !(a is ProgressCallback)).ToArray()));
            foreach (var callback in args.OfType<ProgressCallback>())
            {
                callback("export", 0, 2, null);
                callback("export", 1, 2, "轴 <>&\"\r\n");
            }
            if (method.ReturnType == typeof(void)) return null;
            if (method.ReturnType == typeof(bool)) return true;
            if (method.ReturnType == typeof(SessionMode)) return SessionMode.Mock;
            if (method.ReturnType == typeof(ITiaSession)) return Create<ITiaSession>(calls);
            if (method.ReturnType == typeof(IVersionControl)) return Create<IVersionControl>(calls);
            return Sample(method.ReturnType);
        }
    }
}
