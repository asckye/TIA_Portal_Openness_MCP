using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Runtime;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// PLCSIM Advanced channel, pure part: API probing order, value conversion per primitive type,
    /// comparison with tolerance and scenario parsing. The Siemens assembly is never loaded here.
    /// </summary>
    internal static class PlcSimAdvancedTests
    {
        internal static void Run(Action<bool, string> check)
        {
            bool Fails(Action action) { try { action(); return false; } catch { return true; } }

            // ---- probing order (paths built with the platform separator: the suite also runs on Linux)
            var temp = Path.GetTempPath();
            var root = Path.Combine(temp, "Common Files", "Siemens", "PLCSIMADV", "API");
            var roots = new[] { new KeyValuePair<string, IEnumerable<string>>(root, new[] { Path.Combine(root, "4.0"), Path.Combine(root, "5.0"), Path.Combine(root, "6.0"), Path.Combine(root, "old") }) };
            var explicitDir = Path.Combine(temp, "api");
            var envFile = Path.Combine(temp, "env", PlcSimAdvancedLogic.ApiFileName);
            var probe = PlcSimAdvancedLogic.CandidateApiPaths(explicitDir, envFile, roots);
            check(probe[0] == Path.Combine(explicitDir, PlcSimAdvancedLogic.ApiFileName), "explicit folder first, DLL name appended");
            check(probe[1] == envFile, "environment file second");
            check(probe[2].Contains("6.0") && probe[3].Contains("5.0") && probe[4].Contains("4.0") && probe[5].Contains("old"), "installed folders newest first: " + string.Join(" | ", probe.Skip(2)));
            check(PlcSimAdvancedLogic.CandidateApiPaths(null, null, roots).Count == 4, "no explicit/env -> only installed candidates");
            check(PlcSimAdvancedLogic.CandidateApiPaths(explicitDir, explicitDir + Path.DirectorySeparatorChar, roots).Count == 5, "duplicates collapse");
            check(PlcSimAdvancedLogic.VersionKey("6.0") > PlcSimAdvancedLogic.VersionKey("5.1") && PlcSimAdvancedLogic.VersionKey("x") == new Version(0, 0), "version folder ordering");

            // ---- actions / names
            check(PlcSimAdvancedLogic.NormalizeAction("POWERON") == "powerOn", "actions are case-insensitive");
            check(Fails(() => PlcSimAdvancedLogic.NormalizeAction("reboot")), "[sentinel] unknown action refused");
            check(Fails(() => PlcSimAdvancedLogic.RequireInstanceName("a\\b")) && Fails(() => PlcSimAdvancedLogic.RequireInstanceName("a/b")) && PlcSimAdvancedLogic.RequireInstanceName(" PLC_1 ") == "PLC_1", "instance name validation");

            // ---- 2.7.51 network mode mapping (PLCSIM Advanced 6+: IInstance.CommunicationInterface is read-only, the choice is SimulationRuntimeManager.NetworkMode)
            check(PlcSimAdvancedLogic.NetworkModeFor("Softbus", "TCPIPMultipleAdapter") == "Softbus" && PlcSimAdvancedLogic.NetworkModeFor("softbus", null) == "Softbus", "network mode: Softbus maps to Softbus");
            check(PlcSimAdvancedLogic.NetworkModeFor("TCPIP", "TCPIPSingleAdapter") == "TCPIPSingleAdapter", "network mode: TCPIP keeps the current TCPIP variant");
            check(PlcSimAdvancedLogic.NetworkModeFor("TCPIP", "Softbus") == "TCPIPMultipleAdapter" && PlcSimAdvancedLogic.NetworkModeFor("tcpip", "") == "TCPIPMultipleAdapter", "network mode: TCPIP from Softbus / unknown falls back to the multi-adapter mode");
            check(PlcSimAdvancedLogic.NetworkModeFor("tcpipsingleadapter", "Softbus") == "TCPIPSingleAdapter", "network mode: an ENetworkMode name is taken literally, case-insensitive");
            check(Fails(() => PlcSimAdvancedLogic.NetworkModeFor("None", "Softbus")) && Fails(() => PlcSimAdvancedLogic.NetworkModeFor("", "Softbus")), "[sentinel] None / empty cannot be mapped to a network mode");

            // ---- 2.7.54 operating mode names (PLCSIM Advanced 4+: SingleStep_C / _CT / _P / _CP / ...; "SingleStep" alone no longer exists)
            check(PlcSimAdvancedLogic.OperatingModeCandidates("singleStep").SequenceEqual(new[] { "SingleStep_C", "SingleStep_CP", "SingleStep" }), "operating mode: singleStep prefers the one-sync-point-per-cycle mode (SingleStep_C), then _CP, then the legacy name");
            check(PlcSimAdvancedLogic.SyncPointWaitMs >= 1000 && PlcSimAdvancedLogic.SyncPointPollMs >= 1, "operating mode: sync point wait budget is a real wait, not a spin");
            check(PlcSimAdvancedLogic.OperatingModeCandidates("default").SequenceEqual(new[] { "Default" }) && PlcSimAdvancedLogic.OperatingModeCandidates("TimespanSynchronized_CP").SequenceEqual(new[] { "TimespanSynchronized_CP" }), "operating mode: default and literal names pass through");

            // ---- value map / name list
            var map = PlcSimAdvancedLogic.ParseValueMap("{\"\\\"Start\\\"\": true, \"\\\"DB\\\".Speed\": 50}", "valuesJson");
            check(map.Count == 2 && map[0].Key == "\"Start\"" && map[1].Key == "\"DB\".Speed", "object form keeps quoted names");
            var arr = PlcSimAdvancedLogic.ParseValueMap("[{\"name\":\"\\\"A\\\"\",\"value\":1}]", "valuesJson");
            check(arr.Count == 1 && arr[0].Key == "\"A\"", "array form");
            check(Fails(() => PlcSimAdvancedLogic.ParseValueMap("{}", "v")) && Fails(() => PlcSimAdvancedLogic.ParseValueMap("[1]", "v")) && Fails(() => PlcSimAdvancedLogic.ParseValueMap("{\"\":1}", "v")), "[sentinel] empty/invalid maps refused");
            check(PlcSimAdvancedLogic.ParseNameList("\"A\", \"B\"", "n").SequenceEqual(new[] { "\"A\"", "\"B\"" }) && PlcSimAdvancedLogic.ParseNameList("[\"A\"]", "n").Count == 1 && PlcSimAdvancedLogic.ParseNameList("", "n").Count == 0, "name list forms");

            // ---- conversion
            check((bool)PlcSimAdvancedLogic.ConvertValue(JsonValue.Create(true), "Bool") && (bool)PlcSimAdvancedLogic.ConvertValue(JsonValue.Create("1"), "Bool") && !(bool)PlcSimAdvancedLogic.ConvertValue(JsonValue.Create(0), "Bool"), "Bool from bool/string/number");
            check(PlcSimAdvancedLogic.ConvertValue(JsonValue.Create(-5), "Int16") is short s16 && s16 == -5, "Int16");
            check(PlcSimAdvancedLogic.ConvertValue(JsonValue.Create("16#FF"), "UInt8") is byte u8 && u8 == 255, "UInt8 from 16# hex");
            check(PlcSimAdvancedLogic.ConvertValue(JsonValue.Create(3.5), "Float") is float f && Math.Abs(f - 3.5f) < 1e-6, "Float");
            check(PlcSimAdvancedLogic.ConvertValue(JsonValue.Create(7.0), "Int32") is int i32 && i32 == 7, "integral double accepted for Int32");
            check(Fails(() => PlcSimAdvancedLogic.ConvertValue(JsonValue.Create(300), "UInt8")), "[sentinel] UInt8 overflow refused");
            check(Fails(() => PlcSimAdvancedLogic.ConvertValue(JsonValue.Create(1.5), "Int32")), "[sentinel] fractional value refused for Int32");
            check(Fails(() => PlcSimAdvancedLogic.ConvertValue(JsonValue.Create(1), "Struct")), "[sentinel] struct type refused");
            check(Fails(() => PlcSimAdvancedLogic.ConvertValue(new JsonObject(), "Int32")), "[sentinel] non-scalar refused");
            check(PlcSimAdvancedLogic.ConvertValue(JsonValue.Create("A"), "WChar") is char c && c == 'A', "WChar");

            // ---- comparison
            check(PlcSimAdvancedLogic.ValuesMatch(true, JsonValue.Create(true), 0) && PlcSimAdvancedLogic.ValuesMatch(true, JsonValue.Create(1), 0) && !PlcSimAdvancedLogic.ValuesMatch(false, JsonValue.Create(true), 0), "bool comparison");
            check(PlcSimAdvancedLogic.ValuesMatch(1.0005f, JsonValue.Create(1.0), 0.001) && !PlcSimAdvancedLogic.ValuesMatch(1.01f, JsonValue.Create(1.0), 0.001), "float tolerance");
            check(PlcSimAdvancedLogic.ValuesMatch((short)5, JsonValue.Create(5), 0) && PlcSimAdvancedLogic.ValuesMatch((short)5, JsonValue.Create(6), 1) && !PlcSimAdvancedLogic.ValuesMatch((short)5, JsonValue.Create(7), 1), "integer tolerance");
            check(!PlcSimAdvancedLogic.ValuesMatch(null, JsonValue.Create(1), 0) && PlcSimAdvancedLogic.ValuesMatch(null, null, 0), "null handling");
            check(PlcSimAdvancedLogic.ToJson((ushort)9)!.ToString() == "9" && PlcSimAdvancedLogic.ToJson('x')!.ToString() == "x" && PlcSimAdvancedLogic.ToJson(null) == null, "ToJson");

            // ---- scenario
            var scenario = PlcSimAdvancedLogic.ParseScenario("{\"instance\":\"PLC_1\",\"steps\":[{\"write\":{\"\\\"Start\\\"\":true}},{\"cycles\":5},{\"waitMs\":20},{\"assert\":{\"\\\"Run\\\"\":true},\"tolerance\":0.5,\"note\":\"n\"},{\"run\":true}]}");
            check(scenario.Instance == "PLC_1" && scenario.Mode == "singleStep" && scenario.StopOnFailure, "scenario defaults");
            check(scenario.Steps.Select(x => x.Kind).SequenceEqual(new[] { "write", "cycles", "wait", "assert", "run" }), "step kinds in order");
            check(scenario.Steps[3].Tolerance == 0.5 && scenario.Steps[3].Note == "n" && scenario.Steps[1].Count == 5, "step details");
            var plan = PlcSimAdvancedLogic.ScenarioPlan(scenario);
            check(plan["writes"]!.GetValue<int>() == 1 && plan["asserts"]!.GetValue<int>() == 1 && plan["cycles"]!.GetValue<int>() == 5 && plan["modeChanges"]!.AsArray().Count == 1, "scenario plan counts");
            check(Fails(() => PlcSimAdvancedLogic.ParseScenario("{\"instance\":\"P\",\"steps\":[{\"write\":{\"a\":1},\"cycles\":2}]}")), "[sentinel] step with two kinds refused");
            check(Fails(() => PlcSimAdvancedLogic.ParseScenario("{\"instance\":\"P\",\"steps\":[]}")), "[sentinel] empty steps refused");
            check(Fails(() => PlcSimAdvancedLogic.ParseScenario("{\"instance\":\"P\",\"mode\":\"fast\",\"steps\":[{\"cycles\":1}]}")), "[sentinel] unknown mode refused");
            check(Fails(() => PlcSimAdvancedLogic.ParseScenario("{\"instance\":\"P\",\"steps\":[{\"cycles\":0}]}")), "[sentinel] cycles range enforced");

            // ---- channel without PLCSIM Advanced on this machine: clear failure, never a crash
            var probed = PlcSimAdvancedChannel.ProbePaths(null);
            check(probed.All(p => p.EndsWith(PlcSimAdvancedLogic.ApiFileName, StringComparison.OrdinalIgnoreCase)), "probe paths end with the API file name");
            try
            {
                PlcSimAdvancedChannel.Load(Path.Combine(Path.GetTempPath(), "no-plcsim-" + Guid.NewGuid().ToString("N")));
                check(true, "PLCSIM Advanced API present on this machine (skipping not-found assertion)");
            }
            catch (InvalidOperationException ex)
            {
                check(ex.Message.StartsWith("PLCSIM Advanced API (", StringComparison.Ordinal) && ex.Message.Contains("Probed:"), "missing API reports probe list: " + ex.Message.Substring(0, Math.Min(80, ex.Message.Length)));
            }
        }
    }
}
