using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class ResponseMetaTests
    {
        private static readonly string[] Modes = { "sdk", "bridge", "discipline", "node" };
        private static readonly JsonSerializerOptions Sdk = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        // McpServer.ToolBridge.cs:841 and McpServer.CallDiscipline.cs:27.
        private static readonly JsonSerializerOptions Bridge = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        private static readonly JsonSerializerOptions Discipline = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        private static DateTime FixedDate(DateTimeKind kind) => new DateTime(2001, 2, 3, 4, 5, 6, kind).AddTicks(1234567);
        private static DateTimeOffset Instant => new DateTimeOffset(FixedDate(DateTimeKind.Unspecified), TimeSpan.FromHours(8));

        internal static void Run(Action<bool, string> check)
        {
            using (new LocalZoneScope())
            using (ResponseClock.Pin(Instant))
            {
                check(ResponseClock.Now.Kind == DateTimeKind.Local && ResponseClock.Now == FixedDate(DateTimeKind.Local), "pinned Local clock keeps kind, ticks and +08:00 zone");
                check(ResponseClock.UtcNow.Kind == DateTimeKind.Utc && ResponseClock.UtcNow == Instant.UtcDateTime, "pinned UTC clock represents the same instant");
                GoldenBytes(check);
                Initializers(check);
                ExecutorFactories(check);
                Mutations(check);
                BridgeCases(check);
                Values(check);
                ClockScopes(check);
            }
            var before = DateTime.UtcNow;
            var local = ResponseClock.Now;
            var utc = ResponseClock.UtcNow;
            check(local.Kind == DateTimeKind.Local && utc.Kind == DateTimeKind.Utc && utc >= before && utc <= DateTime.UtcNow,
                "disposing pin restores the real Local and UTC clocks");
            var methods = typeof(ResponseMeta).GetMethods(BindingFlags.Static | BindingFlags.NonPublic);
            check(methods.SelectMany(m => m.GetParameters()).All(p => p.ParameterType.IsArray || p.ParameterType == typeof(JsonObject)
                || p.ParameterType == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(p.ParameterType)), "builder signatures have no IEnumerable parameters");
            check(methods.SelectMany(m => m.GetParameters()).All(p => p.ParameterType != typeof(object)
                && !typeof(Exception).IsAssignableFrom(p.ParameterType)), "builder receives JSON values and verdicts, never caller objects or exceptions to format");
        }

        // Paths below are relative to tools/tiaportal-mcp/src. The sole change to
        // copied clock expressions is DateTime -> ResponseClock, under the same pin.
        private static void Initializers(Action<bool, string> check)
        {
            // TiaMcpServer/ModelContextProtocol/Tools/McpServer.cs:70 (Connect).
            Compare("Stamp/Connect", new JsonObject { ["timestamp"] = ResponseClock.Now }, ResponseMeta.Stamp(), check);
            // TiaMcpServer/ModelContextProtocol/Tools/McpServer.cs:338 (Bootstrap).
            Compare("Basic/Bootstrap", new JsonObject
            {
                ["timestamp"] = ResponseClock.Now,
                ["success"] = true,
            }, ResponseMeta.Basic(true), check);
            // TiaMcpServer/ModelContextProtocol/Tools/McpServer.RuntimeChannels.cs:31.
            bool ok = false;
            Compare("Basic/RuntimeMeta", new JsonObject { ["timestamp"] = ResponseClock.Now, ["success"] = ok }, ResponseMeta.Basic(ok), check);
            // TiaMcpServer/ModelContextProtocol/Tools/McpServer.BlockLogic.cs:54.
            string lang = "中文 <>& '\" / \\ \n 😀";
            Compare("Basic/after", new JsonObject { ["timestamp"] = ResponseClock.Now, ["success"] = true, ["language"] = lang },
                ResponseMeta.Basic(true, ("language", lang)), check);
            // TiaMcpServer/ModelContextProtocol/Tools/McpServer.BaseLeftovers.cs:74.
            bool dryRun = true;
            Compare("StampThen/success-in-middle", new JsonObject { ["timestamp"] = ResponseClock.Now, ["tool"] = "RunToolsInTransaction", ["success"] = false, ["dryRun"] = dryRun, ["mayHaveChanged"] = false },
                ResponseMeta.StampThen(("tool", "RunToolsInTransaction"), ("success", false), ("dryRun", dryRun), ("mayHaveChanged", false)), check);
            // TiaMcpServer/Siemens/Portal/Portal.HmiOperation.cs:49.
            string toolName = "GoldenHmi";
            Compare("StampThen/success-last", new JsonObject
            {
                ["timestamp"] = ResponseClock.Now,
                ["tool"] = toolName,
                ["success"] = false
            }, ResponseMeta.StampThen(("tool", toolName), ("success", false)), check);
            Compare("Step/Hmi", HmiLiteral(toolName), ResponseMeta.Step(toolName), check);
            // TiaMcpServer/ModelContextProtocol/Tools/McpServer.OfflineAnalysis.cs:163.
            Compare("Step/Offline", new JsonObject { ["timestamp"] = ResponseClock.Now, ["tool"] = toolName, ["success"] = false, ["offlineOnly"] = true },
                ResponseMeta.Step(toolName, ("offlineOnly", true)), check);
            // TiaMcpServer/ModelContextProtocol/Tools/McpServer.Batch.cs:116.
            var batch = ResponseMeta.LegacyBatch();
            Compare("LegacyBatch", new JsonObject { ["success"] = false, ["timestamp"] = ResponseClock.UtcNow }, batch, check);
            check(batch["timestamp"]!.GetValue<DateTime>().Kind == DateTimeKind.Utc && batch.First().Key == "success", "batch preserves UTC DateTime and success-first order");
            // TiaMcpServer/Siemens/Portal/Portal.Software.LibrarySeed.cs:48.
            string libraryPath = "C:/中文/library.al21";
            var roundTrip = ResponseMeta.RoundTripStamp();
            roundTrip["inputPath"] = libraryPath;
            Compare("RoundTripStamp", new JsonObject
            {
                ["timestamp"] = ResponseClock.Now.ToString("O"),
                ["inputPath"] = libraryPath
            }, roundTrip, check);
            check(roundTrip["timestamp"]!.GetValue<string>() == "2001-02-03T04:05:06.1234567+08:00", "round-trip stamp remains a string with seven fractional digits");
        }

        private static JsonObject HmiLiteral(string toolName) => new JsonObject
        {
            // TiaMcpServer/Siemens/Portal/Portal.HmiOperation.cs:49.
            ["timestamp"] = ResponseClock.Now,
            ["tool"] = toolName,
            ["success"] = false
        };

        private static void ExecutorFactories(Action<bool, string> check)
        {
            foreach (bool ok in new[] { false, true })
            {
                // McpServer.PlcSoftware.XmlBuilders.cs: BuildOfflineXmlBuilderReport.
                Compare("Basic/XmlBuilder/" + ok, new JsonObject
                {
                    ["timestamp"] = ResponseClock.Now, ["success"] = ok, ["offlineOnly"] = true
                }, ResponseMeta.Basic(ok, ("offlineOnly", true)), check);

                // McpServer.Deletion.cs: BuildDeletionReport retains the caller's array.
                var nextActions = new JsonArray("中文 <>&", "CompileSoftware");
                var deletion = ResponseMeta.Basic(ok, ("dryRun", !ok), ("deleted", ok),
                    ("verifiedAbsent", ok), ("crossReferenceAvailable", false), ("nextActions", nextActions));
                Compare("Basic/Deletion/" + ok, new JsonObject
                {
                    ["timestamp"] = ResponseClock.Now, ["success"] = ok, ["dryRun"] = !ok, ["deleted"] = ok,
                    ["verifiedAbsent"] = ok, ["crossReferenceAvailable"] = false,
                    ["nextActions"] = new JsonArray("中文 <>&", "CompileSoftware")
                }, deletion, check);
                check(ReferenceEquals(nextActions, deletion["nextActions"]), "deletion keeps nextActions ownership " + ok);

                // McpServer.RuntimeChannels.cs: RuntimeMeta appends only supplied nullable flags.
                foreach (bool? flag in new bool?[] { null, false, true })
                {
                    var old = new JsonObject { ["timestamp"] = ResponseClock.Now, ["success"] = ok };
                    var built = ResponseMeta.Basic(ok);
                    if (flag != null)
                    {
                        old["dryRun"] = flag; old["mayHaveChanged"] = flag; old["passwordProvided"] = flag;
                        built["dryRun"] = flag; built["mayHaveChanged"] = flag; built["passwordProvided"] = flag;
                    }
                    Compare("Basic/RuntimeMeta-flags/" + ok + "/" + flag, old, built, check);
                }
            }

            // PlcListingRead.Metadata takes its stamp before evaluating the remaining fields.
            var listing = new PlcListingRead();
            foreach (bool failed in new[] { false, true })
            {
                var failures = new JsonArray();
                if (failed)
                {
                    listing.Optional<int>("/PLC/中文", () => throw new ArgumentException("optional <>&"), -1);
                    failures.Add(new JsonObject { ["path"] = "/PLC/中文", ["exceptionType"] = typeof(ArgumentException).FullName, ["message"] = "optional <>&" });
                }
                var old = new JsonObject
                {
                    ["timestamp"] = ResponseClock.Now, ["serverVersion"] = typeof(PlcListingRead).Assembly.GetName().Version?.ToString(),
                    ["success"] = true, ["apiCallSuccess"] = true, ["dataComplete"] = !failed,
                    ["traversalComplete"] = true, ["scope"] = "PLC/中文", ["failureCount"] = failed ? 1 : 0,
                    ["failures"] = failures
                };
                var meta = listing.Metadata("PLC/中文");
                Compare("Stamp/PlcListingRead/" + failed, old, meta, check);
                check(((JsonValue)meta["failureCount"]!).TryGetValue<int>(out var count) && count == (failed ? 1 : 0), "listing failure count stays Int32 " + failed);
                check(meta["timestamp"]!.GetValue<DateTime>().Kind == DateTimeKind.Local, "listing stamp stays Local " + failed);
                meta["failures"]!.AsArray().Clear();
                check(listing.Metadata("PLC/中文")["failures"]!.AsArray().Count == (failed ? 1 : 0), "listing clones its failures " + failed);
            }
        }

        private static void Mutations(Action<bool, string> check)
        {
            // TiaMcpServer/Siemens/Portal/Portal.HmiOperation.cs:78-79.
            foreach (bool? verdict in new bool?[] { null, true, false })
            {
                var old = HmiLiteral("GoldenHmi");
                var built = ResponseMeta.Step("GoldenHmi");
                if (verdict.HasValue) { old["operationSuccess"] = verdict.Value; built["operationSuccess"] = verdict.Value; }
                old["rows"] = new JsonArray(1, "中文", null); built["rows"] = new JsonArray(1, "中文", null);
                old["success"] = old["operationSuccess"]?.GetValue<bool>() ?? true;
                old["operationSuccess"] = old["success"]?.DeepClone();
                ResponseMeta.Complete(built);
                Compare("Complete/Hmi/" + verdict, old, built, check);
                check(built.ElementAt(2).Key == "success" && built["success"]!.GetValue<bool>() == (verdict ?? true), "success overwrite retains placeholder position " + verdict);
            }
            // TiaMcpServer/ModelContextProtocol/Tools/McpServer.OfflineAnalysis.cs:167.
            var offlineOld = new JsonObject { ["timestamp"] = ResponseClock.Now, ["tool"] = "GoldenOffline", ["success"] = false, ["offlineOnly"] = true };
            var offline = ResponseMeta.Step("GoldenOffline", ("offlineOnly", true));
            offlineOld["operationSuccess"] = false; offline["operationSuccess"] = false;
            offlineOld["success"] = true; offlineOld["operationSuccess"] = true;
            ResponseMeta.Complete(offline, true);
            Compare("Complete/Offline-unconditional", offlineOld, offline, check);
            // TiaMcpServer/ModelContextProtocol/Tools/McpServer.BaseLeftovers.cs:103.
            offlineOld["success"] = false; offlineOld["operationSuccess"] = false;
            ResponseMeta.Complete(offline, false);
            Compare("Complete/explicit-false", offlineOld, offline, check);

            // TiaMcpServer/ModelContextProtocol/Tools/McpServer.OfflineAnalysis.cs:173-177.
            // Exception rendering/status selection remain at the caller, outside Failed.
            foreach (bool success in new[] { false, true })
            {
                var old = HmiLiteral("GoldenOffline");
                var built = ResponseMeta.Step("GoldenOffline");
                old["success"] = success; built["success"] = success;
                old["error"] = "ArgumentException: 中文\ncaller stack"; built["error"] = "ArgumentException: 中文\ncaller stack";
                old["operationSuccess"] = false; old["apiCallSuccess"] = false; old["dataComplete"] = false;
                ResponseMeta.Failed(built);
                old["status"] = "InvalidParams"; built["status"] = "InvalidParams";
                Compare("Failed/retained-success-" + success, old, built, check);
                check(built["success"]!.GetValue<bool>() == success, "Failed does not invent a success assignment " + success);
            }

            // TiaMcpServer/ModelContextProtocol/Tools/McpServer.cs:70-72, with synthetic native output.
            var connectOld = new JsonObject { ["timestamp"] = ResponseClock.Now };
            var connect = ResponseMeta.Stamp();
            using (ResponseClock.Pin(Instant.AddHours(1)))
            {
                connectOld["boundProcessId"] = 42; connect["boundProcessId"] = 42;
                connectOld["success"] = true; connect["success"] = true;
            }
            Compare("Stamp/append-after-work", connectOld, connect, check);
            check(connect["timestamp"]!.GetValue<DateTime>() == FixedDate(DateTimeKind.Local), "appended values do not retake the clock");
            // Same indexer semantics as an initializer, including duplicate keys.
            Compare("Basic/overwrite-after", new JsonObject { ["timestamp"] = ResponseClock.Now, ["success"] = false, ["last"] = 7, ["success"] = true },
                ResponseMeta.Basic(false, ("last", 7), ("success", true)), check);
            Compare("StampThen/empty", new JsonObject { ["timestamp"] = ResponseClock.Now }, ResponseMeta.StampThen(), check);
        }

        private static void BridgeCases(Action<bool, string> check)
        {
            // TiaMcp.Logic/ModelContextProtocol/ToolBridgeStatus.cs:10-22, copied literally.
            int index = 0;
            foreach (bool bridgeSuccess in new[] { true, false })
                foreach (var operationMeta in new JsonObject?[] { null, new JsonObject(), new JsonObject { ["success"] = true },
                    new JsonObject { ["success"] = false }, new JsonObject { ["success"] = "true" }, new JsonObject { ["success"] = null } })
                {
                    var unchanged = operationMeta?.ToJsonString();
                    bool? operationSuccess = null;
                    if (bridgeSuccess && operationMeta?["success"] is JsonValue value && value.TryGetValue<bool>(out var success))
                        operationSuccess = success;
                    var old = new JsonObject
                    {
                        ["timestamp"] = ResponseClock.Now,
                        ["bridgeSuccess"] = bridgeSuccess,
                        ["operationSuccess"] = operationSuccess,
                        ["success"] = bridgeSuccess ? operationSuccess : false,
                        ["operationStatus"] = !bridgeSuccess ? "notCompleted" : operationSuccess == true ? "succeeded" : operationSuccess == false ? "failed" : "unknown"
                    };
                    Compare("Bridge/" + index++, old, ResponseMeta.Bridge(bridgeSuccess, operationMeta), check);
                    Compare("ToolBridgeStatus/" + index, old, ToolBridgeStatus.Create(bridgeSuccess, operationMeta), check);
                    check(operationMeta?.ToJsonString() == unchanged, "Bridge leaves operation Meta unchanged " + index);
                }
        }

        private static void Values(Action<bool, string> check)
        {
            var child = new JsonObject { ["empty"] = new JsonArray() };
            var meta = ResponseMeta.Basic(true, ("int", 42), ("long", 9007199254740993L), ("double", 1.2345678901234567d),
                ("decimal", 1.2300m), ("nothing", null), ("child", child));
            check(((JsonValue)meta["int"]!).TryGetValue<int>(out var i) && i == 42, "Int32 stays Int32");
            check(((JsonValue)meta["long"]!).TryGetValue<long>(out var l) && l == 9007199254740993L, "Int64 keeps values above 2^53");
            check(((JsonValue)meta["double"]!).TryGetValue<double>(out var d) && d == 1.2345678901234567d, "Double retains precision and CLR type");
            check(((JsonValue)meta["decimal"]!).TryGetValue<decimal>(out var dec) && decimal.GetBits(dec).SequenceEqual(decimal.GetBits(1.2300m)), "Decimal retains CLR type and scale");
            check(ReferenceEquals(meta["child"], child) && ReferenceEquals(child.Parent, meta), "Append preserves initializer node ownership without cloning");
            Compare("numeric-types-and-null", new JsonObject { ["timestamp"] = ResponseClock.Now, ["success"] = true, ["int"] = 42,
                ["long"] = 9007199254740993L, ["double"] = 1.2345678901234567d, ["decimal"] = 1.2300m,
                ["nothing"] = null, ["child"] = new JsonObject { ["empty"] = new JsonArray() } }, meta, check);
            var opaque = new NoString();
            var opaqueMeta = ResponseMeta.Basic(true, ("value", JsonValue.Create(opaque)));
            check(ReferenceEquals(opaque, opaqueMeta["value"]!.GetValue<NoString>()), "caller values are never stringified during construction");
            foreach (string mode in Modes)
            {
                var response = new ResponseMessage { Message = "No project open.", Meta = null };
                string expected = mode == "sdk" ? "{\"message\":\"No project open.\"}" : "{\"Message\":\"No project open.\",\"Meta\":null}";
                check(Serialize(response, mode).SequenceEqual(Encoding.UTF8.GetBytes(expected)), "Meta null handling " + mode);
                check(Serialize(new ResponseMessage { Message = "x", Meta = ResponseMeta.Basic(false) }, mode)
                    .SequenceEqual(Serialize(new ResponseMessage { Message = "x", Meta = new JsonObject { ["timestamp"] = ResponseClock.Now, ["success"] = false } }, mode)), "Meta stays JsonObject on response " + mode);
            }
        }

        private static void ClockScopes(Action<bool, string> check)
        {
            var first = ResponseClock.UtcNow;
            try
            {
                using (ResponseClock.Pin(Instant.AddDays(2)))
                {
                    check(ResponseClock.UtcNow == first.AddDays(2), "nested clock pin");
                    throw new InvalidOperationException("test scope unwind");
                }
            }
            catch (InvalidOperationException) { }
            check(ResponseClock.UtcNow == first, "nested pin restores after exception");
            var readings = Task.WhenAll(Enumerable.Range(1, 4).Select(async n =>
            {
                using (ResponseClock.Pin(Instant.AddDays(n)))
                {
                    await Task.Yield();
                    return ResponseClock.UtcNow == first.AddDays(n);
                }
            })).GetAwaiter().GetResult();
            check(readings.All(x => x) && ResponseClock.UtcNow == first, "clock pin flows across await and isolates concurrent contexts");
            var scope = ResponseClock.Pin(Instant.AddHours(1));
            scope.Dispose(); scope.Dispose();
            check(ResponseClock.UtcNow == first, "clock scope disposal is idempotent");
        }

        private static void Compare(string name, object old, object built, Action<bool, string> check)
        {
            foreach (string mode in Modes)
                check(Serialize(old, mode).SequenceEqual(Serialize(built, mode)), name + " exact UTF-8 " + mode);
        }

        private static byte[] Serialize(object value, string mode)
        {
            if (mode == "node") return Encoding.UTF8.GetBytes((value as JsonNode ?? JsonValue.Create(value)!).ToJsonString());
            return JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), mode == "sdk" ? Sdk : mode == "bridge" ? Bridge : Discipline);
        }

        private static void GoldenBytes(Action<bool, string> check)
        {
            // The same nine values as HttpTests/ResponseGoldenTests.cs:107-131.
            // GoldenMeta is built through Basic; UTC is deliberately the same *wall*
            // time as Local here, matching E1's two independent scalar samples.
            JsonObject GoldenMeta() => ResponseMeta.Basic(true, ("中文", "画面 <>& '\" / \\ \n 😀"), ("nothing", null),
                ("utc", FixedDate(DateTimeKind.Utc)), ("int", 42), ("long", 9007199254740993L),
                ("double", 1.2345678901234567d), ("wholeDouble", 1.0d), ("exponent", 1.25e-20d),
                ("nested", new JsonArray(new JsonArray(1, "中文", null), new JsonObject { ["empty"] = new JsonArray() })));
            var values = new (string Name, object Value)[]
            {
                ("poco-null", new GoldenXmlBuild { Ok = false, Message = "中文响应", Meta = GoldenMeta() }),
                ("poco-populated", new GoldenXmlBuild { Ok = true, Message = "完成", Meta = GoldenMeta(), Xml = "<root name=\"中文\" />",
                    Data = new JsonObject { ["ok"] = true }, Errors = new[] { "error", "中文" }, Warnings = new[] { "warning" } }),
                ("meta", GoldenMeta()), ("local", FixedDate(DateTimeKind.Local)), ("utc", FixedDate(DateTimeKind.Utc)),
                ("int", -42), ("long", long.MaxValue), ("double", 1.2345678901234567d),
                ("arrays", new JsonArray(new JsonArray(1, 2, 3), null, new JsonArray("中文", true)))
            };
            foreach (string release in new[] { "20", "21" })
            {
                using var stream = typeof(ResponseMetaTests).Assembly.GetManifestResourceStream("ResponseGolden.v" + release + ".txt")!;
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
                var records = reader.ReadToEnd().Split('\n').Where(line => line.StartsWith("serializer/", StringComparison.Ordinal))
                    .Select(line => line.Split(new[] { '\t' }, 2)).ToDictionary(pair => pair[0], pair => Encoding.UTF8.GetBytes(pair[1]));
                check(records.Count == values.Length * Modes.Length, "complete E1 serializer coverage V" + release);
                foreach (var value in values)
                    foreach (string mode in Modes)
                    {
                        string key = "serializer/" + value.Name + "/" + mode;
                        check(Serialize(value.Value, mode).SequenceEqual(records[key]), "engine golden V" + release + " " + key);
                    }
            }
        }

        // Test-only shapes copied from Responses.cs:305-318, 665-668. Production
        // POCOs stay untouched; the E1 bytes above validate inheritance/property order.
        private class GoldenJsonReport : ResponseMessage
        {
            public bool? Ok { get; set; }
            public JsonObject? Data { get; set; }
            public string[]? Errors { get; set; }
            public string[]? Warnings { get; set; }
            public string? OutputPath { get; set; }
            public string[]? OutputFiles { get; set; }
        }
        private sealed class GoldenXmlBuild : GoldenJsonReport { public string? Xml { get; set; } }
        private sealed class NoString { public override string ToString() => throw new InvalidOperationException("Caller ToString was invoked"); }

        private sealed class LocalZoneScope : IDisposable
        {
            private readonly object cached;
            private readonly FieldInfo field;
            private readonly object? previous;
            private readonly CultureInfo culture = CultureInfo.CurrentCulture;
            private readonly CultureInfo uiCulture = CultureInfo.CurrentUICulture;

            internal LocalZoneScope()
            {
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                cached = typeof(TimeZoneInfo).GetField("s_cachedData", flags)!.GetValue(null)!;
                field = cached.GetType().GetField("_localTimeZone", flags) ?? cached.GetType().GetField("m_localTimeZone", flags)!;
                previous = field.GetValue(cached);
                field.SetValue(cached, TimeZoneInfo.CreateCustomTimeZone("ResponseGolden", TimeSpan.FromHours(8), "ResponseGolden", "ResponseGolden"));
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            }

            public void Dispose()
            {
                field.SetValue(cached, previous);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
            }
        }
    }
}
