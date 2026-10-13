using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;

internal static class UnifiedNativeFixture
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static string Json(object value) => JsonSerializer.Serialize(value, value.GetType());
    internal static Type Helper(Assembly server, string name)
    {
        var reference = server.GetReferencedAssemblies().Single(a => a.Name!.StartsWith("TiaMcp.Adapter.", StringComparison.Ordinal));
        var adapter = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(server.Location)!, reference.Name + ".dll"));
        var values = adapter.GetType("TiaMcp.Adapters.Native.Hmi.HmiValues", true)!;
        values.GetField("Renderer", All)!.SetValue(null,
            new Func<Dictionary<string, object?>, string>(content => JsonSerializer.Serialize(content)));
        adapter.GetType("TiaMcp.Adapters.Native.Hmi.UnifiedCodecs", true)!.GetField("Current", All)!.SetValue(null, new Codecs(server));
        if (name != "UnifiedNativeRead")
        {
            var requests = adapter.GetType("TiaMcp.Adapters.Native.Hmi.UnifiedRequestValues", true)!;
            var decoder = requests.GetField("Decoder", All)!;
            var decode = server.GetType("TiaMcpServer.Siemens.Services.UnifiedInputCodec", true)!.GetMethod("Decode", All)!;
            Func<string, object> read = text => { using var document = JsonDocument.Parse(text); return decode.Invoke(null, new object[] { document.RootElement })!; };
            var input = Expression.Parameter(typeof(string));
            decoder.SetValue(null, Expression.Lambda(decoder.FieldType,
                Expression.Convert(Expression.Invoke(Expression.Constant(read), input), decoder.FieldType.GetGenericArguments()[1]), input).Compile());
            requests.GetField("Major", All)!.SetValue(null, server.GetName().Name!.Contains("V20") ? 20 : 21);
            var exact = adapter.GetType("TiaMcp.Adapters.Native.Hmi.HmiExactAccess", true)!;
            var token = server.GetType("TiaMcpServer.Siemens.HmiExactAccess", true)!.GetMethod("Token", All)!;
            exact.GetField("ContentToken", All)!.SetValue(null, new Func<string, Dictionary<string, object?>, string>(
                (scope, content) => (string)token.Invoke(null, new object[] { scope, JsonSerializer.SerializeToNode(content)! })!));
        }
        return adapter.GetType("TiaMcp.Adapters.Native.Hmi." + name, true)!;
    }

    // The production codecs are managed; the fixture does not instantiate a
    // native session just to wire their delegate properties.
    private sealed class Codecs : IUnifiedCodecs
    {
        public Func<string, string, UnifiedScriptPair>? ReadUnifiedScriptPair { get; }
        public Action<string, string>? RequireUnifiedPlainFile { get; }
        public Action<string>? ValidateUnifiedScript { get; }
        public Func<string, string, string, IEnumerable<Dictionary<string, object?>>>? DecodeUnifiedDocument { get; }
        public Func<string, string, Dictionary<string, object?>>? AnalyzeUnifiedScript { get; }
        internal Codecs(Assembly server)
        {
            var files = server.GetType("TiaMcpServer.Siemens.Services.UnifiedScriptFileCodec", true)!;
            ReadUnifiedScriptPair = (Func<string, string, UnifiedScriptPair>)Delegate.CreateDelegate(typeof(Func<string, string, UnifiedScriptPair>), files.GetMethod("ReadPair", All)!);
            RequireUnifiedPlainFile = (Action<string, string>)Delegate.CreateDelegate(typeof(Action<string, string>), files.GetMethod("RequirePlainFile", All)!);
            ValidateUnifiedScript = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), files.GetMethod("ValidateScript", All)!);
            var documents = server.GetType("TiaMcpServer.Siemens.Services.UnifiedDocumentCodec", true)!;
            DecodeUnifiedDocument = (Func<string, string, string, IEnumerable<Dictionary<string, object?>>>)Delegate.CreateDelegate(
                typeof(Func<string, string, string, IEnumerable<Dictionary<string, object?>>>), documents.GetMethod("Document", All)!);
            var logic = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(server.Location)!, "TiaMcp.Logic.dll"));
            var analyze = logic.GetType("TiaMcpServer.Siemens.JavaScriptEvidence", true)!.GetMethod("Analyze", All)!;
            AnalyzeUnifiedScript = (code, path) => (Dictionary<string, object?>)Plain(
                JsonSerializer.SerializeToElement(analyze.Invoke(null, new object[] { code, path })))!;
        }
        private static object? Plain(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
                return value.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value), StringComparer.Ordinal);
            if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Select(Plain).ToList();
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            return value.ValueKind == JsonValueKind.Number ? value.TryGetInt32(out var integer) ? (object)integer : value.GetDecimal() : null;
        }
    }
}
