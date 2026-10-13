using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using TiaMcp.Adapters.Contracts;
namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class UnifiedCodecs
    {
        [ThreadStatic] internal static IUnifiedCodecs? Current;
        internal static UnifiedScriptPair ReadPair(string directory, string name) => (Current?.ReadUnifiedScriptPair ?? throw new InvalidOperationException("Unified script file codec unavailable."))(directory, name);
        internal static void RequirePlainFile(string path, string directory) => (Current?.RequireUnifiedPlainFile ?? throw new InvalidOperationException("Unified file validation unavailable."))(path, directory);
        internal static void ValidateScript(string code) => (Current?.ValidateUnifiedScript ?? throw new InvalidOperationException("Unified JavaScript parser unavailable."))(code);
        internal static IEnumerable<Dictionary<string, object?>> Document(string text, string path, string extension) => (Current?.DecodeUnifiedDocument ?? throw new InvalidOperationException("Unified document codec unavailable."))(text, path, extension);
        internal static HmiObject AnalyzeScript(string code, string path) => new HmiObject((Current?.AnalyzeUnifiedScript ?? throw new InvalidOperationException("Unified script analyzer unavailable."))(code, path));
        internal static HmiObject Evidence(this UnifiedScriptPair pair)
        {
            string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
            return new HmiObject { ["scriptFile"] = pair.JsName, ["yamlFile"] = pair.YamlName, ["scriptSha256"] = Hash(pair.Js), ["yamlSha256"] = Hash(pair.Yaml), ["scriptCode"] = pair.Text };
        }
    }
}
