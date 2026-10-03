using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class ArgumentRulesCharacterizationTests
    {
        // Keep the original declaring types as the test boundary, including their private helpers.
        private static readonly Type[] DeclaringTypes = {
            typeof(CfcLogic), typeof(ClassicHmiFoldersLogic), typeof(DccLogic), typeof(HardwareNetworkLogic),
            typeof(HardwareServicesLogic), typeof(LibraryDeepLogic), typeof(SafetyValidationLogic), typeof(SivarcLogic),
            typeof(SoftwareUnitDeepLogic), typeof(Step7LeftoversLogic), typeof(TeamcenterLogic), typeof(TestSuiteLogic),
            typeof(BaseLeftoversLogic), typeof(SecurityDeepLogic), typeof(StartdriveLogic), typeof(SafetyLogic),
            typeof(PlcBuilderToolJson), typeof(PlcDocumentEditing), typeof(GlobalLibraryPackageAnalyzer),
            typeof(PlcBuilderFixtureReadinessAnalyzer), typeof(NativeFileOutput)
        };

        internal static void Run(Action<bool, string> check)
        {
            using var stream = typeof(ArgumentRulesCharacterizationTests).Assembly.GetManifestResourceStream("ArgumentRulesCharacterization.json")!;
            var rows = JsonNode.Parse(stream)!.AsArray();
            foreach (var node in rows)
            {
                var row = node!.AsObject();
                var actual = Observe(row);
                var expected = row["expected"]!.GetValue<string>();
                check(actual == expected, row["id"]!.GetValue<string>() + (actual == expected ? "" : " expected: " + expected + " actual: " + actual));
            }
        }

        // Expected outcomes were captured before extracting ArgumentRules; this suite never updates them.
        internal static string Observe(JsonObject row)
        {
            var culture = CultureInfo.CurrentCulture;
            var uiCulture = CultureInfo.CurrentUICulture;
            string? file = null;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
                var type = DeclaringTypes.Single(t => t.Name == row["type"]!.GetValue<string>());
                var method = type.GetMethod(row["method"]!.GetValue<string>(), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
                var parameters = method.GetParameters();
                var args = row["args"]!.AsArray().Select((value, i) => Argument(value, parameters[i].ParameterType, ref file)).ToList();
                while (args.Count < parameters.Length) args.Add(Type.Missing);
                object? result;
                try { result = method.Invoke(null, args.ToArray()); }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    var error = ex.InnerException;
                    // Only the temporary file name is normalized; error text, type and ParamName are exact.
                    return new JsonObject {
                        ["exception"] = error.GetType().FullName,
                        ["message"] = file == null ? error.Message : error.Message.Replace(file, "$FILE"),
                        ["parameter"] = (error as ArgumentException)?.ParamName
                    }.ToJsonString();
                }
                if (result is JsonObject obj && file != null) obj["path"] = "$FILE";
                if (result is ValueTuple<bool, string?, string?> hash)
                    result = new JsonObject { ["ok"] = hash.Item1, ["sha256"] = hash.Item2, ["error"] = file == null ? hash.Item3 : hash.Item3?.Replace(file, "$FILE") };
                return new JsonObject { ["return"] = result is JsonNode json ? json : JsonValue.Create(result as string) }.ToJsonString();
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
                if (file != null && File.Exists(file)) File.Delete(file);
            }
        }

        private static object? Argument(JsonNode? value, Type type, ref string? file)
        {
            if (value == null) return null;
            if (type == typeof(JsonObject)) return value.DeepClone().AsObject();
            if (value is JsonObject spec)
            {
                if (spec["file"] != null)
                {
                    file = Path.Combine(Path.GetTempPath(), "argument-rules-" + Guid.NewGuid().ToString("N"));
                    if (spec["file"]!.GetValue<string>() != "missing") File.WriteAllBytes(file, Bytes(spec["file"]!.GetValue<string>()));
                    return type == typeof(FileInfo) ? new FileInfo(file) : file;
                }
                if (spec["absolute"] != null) return Path.Combine(Path.GetTempPath(), "argument-rules-output");
                if (spec["bytes"] != null) return Bytes(spec["bytes"]!.GetValue<string>());
                if (spec["entries"] != null)
                    return "{" + string.Join(",", Enumerable.Range(0, spec["entries"]!.GetValue<int>()).Select(i => "\"k" + i + "\":0")) + "}";
                return (spec["prefix"]?.GetValue<string>() ?? "") + new string(spec["char"]!.GetValue<string>()[0], spec["repeat"]!.GetValue<int>());
            }
            if (type == typeof(string[])) return value.AsArray().Select(n => n?.GetValue<string>()!).ToArray();
            if (type == typeof(int)) return value.GetValue<int>();
            return value.GetValue<string>();
        }

        private static byte[] Bytes(string kind) => kind switch {
            "empty" => Array.Empty<byte>(),
            "binary" => Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(),
            "unicode" => Encoding.UTF8.GetBytes("工程\r\nΔ\0"),
            _ => Encoding.UTF8.GetBytes("abc")
        };
    }
}
