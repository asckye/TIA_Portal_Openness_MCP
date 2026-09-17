using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Runtime;

namespace TiaMcpServer.Tests
{
    // Offline tests for the RuntimeChannels logic (S7 Web API + WinCC Unified Open Pipe):
    // request building, response parsing and every refusal path. Needs only
    // src/TiaMcpServer/Runtime/RuntimeChannelsLogic.cs linked in.
    internal static class RuntimeChannelsTests
    {
        private static bool Fails<TEx>(Action a) where TEx : Exception { try { a(); return false; } catch (TEx) { return true; } }

        internal static void Run(Action<bool, string> check)
        {
            Console.WriteLine("== RuntimeChannels: S7 Web API / Open Pipe request building, parsing, refusals ==");
            NameLists(check);
            WriteLists(check);
            ValueMatching(check);
            S7WebInputs(check);
            OpenPipeRequests(check);
            OpenPipeResponses(check);
            RawRequests(check);
        }

        private static void NameLists(Action<bool, string> check)
        {
            var a = RuntimeChannelsLogic.ParseNameList("[\"\\\"DB1\\\".\\\"Speed\\\"\", \" Tag_2 \"]", "varsJson");
            check(a.Count == 2 && a[0] == "\"DB1\".\"Speed\"" && a[1] == "Tag_2", "[sentinel] JSON array of names parses and trims");
            var b = RuntimeChannelsLogic.ParseNameList("Tag_1, Tag_2;Tag_3", "varsJson");
            check(b.SequenceEqual(new[] { "Tag_1", "Tag_2", "Tag_3" }), "[sentinel] delimiter list parses");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseNameList("", "x")), "empty list refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseNameList("[]", "x")), "empty JSON array refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseNameList("[\"a\", 1]", "x")), "non-string entry refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseNameList("[\"a\", \"\"]", "x")), "empty name refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseNameList("[\"a\", \"a\"]", "x")), "duplicate name refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseNameList("[\"a\"", "x")), "malformed JSON refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseNameList("{\"a\":1}", "x")), "JSON object instead of array refused");
            var many = "[" + string.Join(",", Enumerable.Range(0, 501).Select(i => "\"T" + i + "\"")) + "]";
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseNameList(many, "x")), "more than 500 names refused");
        }

        private static void WriteLists(Action<bool, string> check)
        {
            var w = RuntimeChannelsLogic.ParseWriteList("[{\"name\":\"A\",\"value\":true},{\"name\":\"B\",\"value\":42},{\"name\":\"C\",\"value\":1.5},{\"name\":\"D\",\"value\":\"txt\"}]", "writesJson");
            check(w.Count == 4 && w[0].ClrValue is bool && w[1].ClrValue is long l && l == 42 && w[2].ClrValue is double && w[3].ClrValue is string, "[sentinel] array form maps JSON scalars to bool/long/double/string");
            check(w.Select(x => x.ValueKind).SequenceEqual(new[] { "bool", "integer", "number", "string" }), "value kinds reported");
            var m = RuntimeChannelsLogic.ParseWriteList("{\"Tag_1\":50,\"Flag\":false}", "writesJson");
            check(m.Count == 2 && m[0].Name == "Tag_1" && m[1].Name == "Flag" && m[1].ClrValue is bool f && !f, "[sentinel] object form parses");
            check(m[0].Value!.ToJsonString() == "50", "original JSON value kept for readback comparison");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseWriteList("", "x")), "empty writes refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseWriteList("[]", "x")), "no writes refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseWriteList("[{\"name\":\"A\"}]", "x")), "missing value refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseWriteList("[{\"value\":1}]", "x")), "missing name refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseWriteList("[{\"name\":\"A\",\"value\":null}]", "x")), "null value refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseWriteList("[{\"name\":\"A\",\"value\":[1,2]}]", "x")), "array value refused (no struct/array writes)");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseWriteList("{\"A\":{\"x\":1}}", "x")), "object value refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseWriteList("[{\"name\":\"A\",\"value\":1},{\"name\":\"A\",\"value\":2}]", "x")), "duplicate write target refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseWriteList("[1,2]", "x")), "non-object entries refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParseWriteList("\"A\"", "x")), "bare string refused");
        }

        private static void ValueMatching(Action<bool, string> check)
        {
            JsonNode N(string json) => JsonNode.Parse(json)!;
            check(RuntimeChannelsLogic.ValuesMatch(N("true"), N("true")), "[sentinel] bool matches bool");
            check(!RuntimeChannelsLogic.ValuesMatch(N("true"), N("false")), "bool mismatch detected");
            check(RuntimeChannelsLogic.ValuesMatch(N("42"), N("42.0")), "integer matches float of same value");
            check(RuntimeChannelsLogic.ValuesMatch(N("1.5"), N("\"1.5\"")), "number matches Open Pipe string value");
            check(RuntimeChannelsLogic.ValuesMatch(N("0.1"), N("0.10000000149011612")), "REAL round-trip tolerance accepted");
            check(!RuntimeChannelsLogic.ValuesMatch(N("100"), N("101")), "numeric mismatch detected");
            check(RuntimeChannelsLogic.ValuesMatch(N("true"), N("\"TRUE\"")), "bool matches Open Pipe 'TRUE'");
            check(RuntimeChannelsLogic.ValuesMatch(N("false"), N("\"0\"")), "bool matches Open Pipe '0'");
            check(!RuntimeChannelsLogic.ValuesMatch(N("true"), N("\"0\"")), "bool true vs '0' mismatch detected");
            check(RuntimeChannelsLogic.ValuesMatch(N("true"), N("1")) && !RuntimeChannelsLogic.ValuesMatch(N("true"), N("2")), "bool matches 1/0 only");
            check(RuntimeChannelsLogic.ValuesMatch(N("\"MC001\""), N("\"MC001\"")), "string matches string");
            check(!RuntimeChannelsLogic.ValuesMatch(N("\"MC001\""), N("\"mc001\"")), "string comparison is case-sensitive");
            check(!RuntimeChannelsLogic.ValuesMatch(N("1"), null), "missing readback never verifies");
            check(RuntimeChannelsLogic.ToJsonNode(7)!.ToJsonString() == "7" && RuntimeChannelsLogic.ToJsonNode(true)!.ToJsonString() == "true"
                && RuntimeChannelsLogic.ToJsonNode("s")!.ToJsonString() == "\"s\"" && RuntimeChannelsLogic.ToJsonNode(null) == null, "CLR scalars convert to JSON nodes");
            check(RuntimeChannelsLogic.ToJsonNode(new object(), o => "{\"a\":1}")!["a"]!.ToString() == "1", "complex values go through the supplied serializer");
        }

        private static void S7WebInputs(Action<bool, string> check)
        {
            check(RuntimeChannelsLogic.NormalizeS7WebHost(" 192.168.0.1 ") == "192.168.0.1", "[sentinel] plain IP accepted");
            check(RuntimeChannelsLogic.NormalizeS7WebHost("plc.local:8443") == "plc.local:8443", "[sentinel] host:port accepted");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.NormalizeS7WebHost("")), "empty host refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.NormalizeS7WebHost("https://192.168.0.1")), "scheme refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.NormalizeS7WebHost("192.168.0.1/api")), "path refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.NormalizeS7WebHost("plc:99999")), "bad port refused");
            check(RuntimeChannelsLogic.S7WebSessionKey("PLC", "u", false) != RuntimeChannelsLogic.S7WebSessionKey("PLC", "u", true), "session key separates certificate policies");
            check(RuntimeChannelsLogic.S7WebSessionKey("PLC", "u", false) == RuntimeChannelsLogic.S7WebSessionKey("plc", "u", false), "session key is host-case-insensitive");
            check(RuntimeChannelsLogic.NormalizeOperatingModeRequest(" RUN ") == "run" && RuntimeChannelsLogic.NormalizeOperatingModeRequest("stop") == "stop", "[sentinel] run/stop accepted");
            foreach (var bad in new[] { "", "hold", "startup", "reset", "memory reset" })
                check(Fails<ArgumentException>(() => RuntimeChannelsLogic.NormalizeOperatingModeRequest(bad)), "mode '" + bad + "' refused");
            check(RuntimeChannelsLogic.OperatingModeSatisfies("run", "Run") && RuntimeChannelsLogic.OperatingModeSatisfies("run", "run_redundant"), "run satisfied by Run/run_redundant");
            check(!RuntimeChannelsLogic.OperatingModeSatisfies("run", "Startup") && !RuntimeChannelsLogic.OperatingModeSatisfies("stop", "Stop_fwupdate"), "startup/fw-update stop are not accepted as final modes");
            check(RuntimeChannelsLogic.OperatingModeSatisfies("stop", "Stop") && !RuntimeChannelsLogic.OperatingModeSatisfies("stop", null), "stop satisfied only by Stop");
            check(RuntimeChannelsLogic.ClampTimeout(0) == 5000 && RuntimeChannelsLogic.ClampTimeout(10) == 500 && RuntimeChannelsLogic.ClampTimeout(999999) == 120000 && RuntimeChannelsLogic.ClampTimeout(7000) == 7000, "timeout clamping");
        }

        private static void OpenPipeRequests(Action<bool, string> check)
        {
            check(RuntimeChannelsLogic.ParsePipeName(@"\\.\pipe\HmiRuntime") == (".", "HmiRuntime"), "[sentinel] manual pipe form parses");
            check(RuntimeChannelsLogic.ParsePipeName("HmiRuntime") == (".", "HmiRuntime"), "[sentinel] bare pipe name parses");
            check(RuntimeChannelsLogic.ParsePipeName("") == (".", "HmiRuntime"), "empty pipe name falls back to the Runtime default");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParsePipeName(@"\\otherpc\pipe\HmiRuntime")), "remote pipe refused (Open Pipe is local only)");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParsePipeName(@"\\.\mailslot\x")), "non-pipe path refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.ParsePipeName("a/b")), "slash in bare name refused");

            // Exact shapes from the Open Pipe manual (expert syntax 3.2.3 / 3.2.4 / 3.2.7).
            string read = RuntimeChannelsLogic.BuildReadTagRequest(new[] { "Tag_0", "Tag_1" }, "myRequest1");
            check(read == "{\"Message\":\"ReadTag\",\"Params\":{\"Tags\":[\"Tag_0\",\"Tag_1\"]},\"ClientCookie\":\"myRequest1\"}", "ReadTag request matches the manual byte for byte");
            var writes = RuntimeChannelsLogic.ParseWriteList("[{\"name\":\"Tag_0\",\"value\":\"50\"},{\"name\":\"Tag_1\",\"value\":40}]", "w");
            string write = RuntimeChannelsLogic.BuildWriteTagRequest(writes, "myRequest2");
            check(write == "{\"Message\":\"WriteTag\",\"Params\":{\"Tags\":[{\"Name\":\"Tag_0\",\"Value\":\"50\"},{\"Name\":\"Tag_1\",\"Value\":40}]},\"ClientCookie\":\"myRequest2\"}", "WriteTag request uses Name/Value objects and keeps JSON value types");
            string alarm = RuntimeChannelsLogic.BuildReadAlarmRequest(new[] { "System0" }, "AlarmClassName != 'Warning'", 1033, "c");
            check(alarm == "{\"Message\":\"ReadAlarm\",\"Params\":{\"SystemNames\":[\"System0\"],\"Filter\":\"AlarmClassName != 'Warning'\",\"LanguageId\":1033},\"ClientCookie\":\"c\"}", "ReadAlarm request carries SystemNames/Filter/LanguageId with literal quotes");
            check(RuntimeChannelsLogic.BuildReadTagRequest(new[] { "HMI_RT_1::电机转速" }, "c").Contains("\"HMI_RT_1::电机转速\""), "non-ASCII tag names are sent as literal UTF-8, not \\u escapes");
            check(!read.Contains("\n") && !write.Contains("\n") && !alarm.Contains("\n"), "requests are single-line (pipe protocol is newline-terminated)");
            check(RuntimeChannelsLogic.NewClientCookie("x") != RuntimeChannelsLogic.NewClientCookie("x"), "client cookies are unique per request");
        }

        private static void OpenPipeResponses(Action<bool, string> check)
        {
            // NotifyReadTag example from the manual.
            string ok = "{\"Message\":\"NotifyReadTag\",\"Params\":{\"Tags\":[{\"Name\":\"Tag_0\",\"Quality\":\"Good\",\"QualityCode\":\"192\",\"TimeStamp\":\"2019-01-30T11:25:35Z\",\"Value\":\"16\",\"ErrorCode\":0,\"ErrorDescription\":\"\"},{\"Name\":\"Tag_1\",\"Quality\":\"Uncertain\",\"QualityCode\":\"76\",\"TimeStamp\":\"2019-01-30T11:25:35Z\",\"Value\":\"1\",\"ErrorCode\":-2147483620,\"ErrorDescription\":\"Tag does not exist\"}]},\"ClientCookie\":\"myRequest1\"}";
            var r = RuntimeChannelsLogic.ParseResponseLine(ok, "myRequest1")!;
            check(!r.IsError && r.Message == "NotifyReadTag" && r.Tags.Count == 2, "[sentinel] NotifyReadTag parses");
            check(r.Tags[0].Ok && r.Tags[0].Value == "16" && r.Tags[0].Quality == "Good" && r.Tags[0].QualityCode == "192" && r.Tags[0].TimeStamp == "2019-01-30T11:25:35Z", "tag value/quality/timestamp extracted");
            check(!r.Tags[1].Ok && r.Tags[1].ErrorCode == -2147483620 && r.Tags[1].ErrorDescription == "Tag does not exist", "per-tag error extracted (negative 32-bit code kept)");
            check(RuntimeChannelsLogic.IsResponseFor(r, "ReadTag") && !RuntimeChannelsLogic.IsResponseFor(r, "WriteTag"), "response/command matching");
            check(RuntimeChannelsLogic.ParseResponseLine(ok, "otherCookie") == null, "line for another cookie is skipped, not misattributed");
            check(RuntimeChannelsLogic.ParseResponseLine("   ", "c") == null, "blank line skipped");

            string err = "{\"Message\":\"ErrorReadTag\",\"ErrorCode\":-2147483621,\"ErrorDescription\":\"Failed to Read\", \"ClientCookie\":\"myRequest1\"}";
            var e = RuntimeChannelsLogic.ParseResponseLine(err, "myRequest1")!;
            check(e.IsError && e.ErrorCode == -2147483621 && e.ErrorDescription == "Failed to Read" && e.Tags.Count == 0, "ErrorReadTag parses as error");

            string wr = "{\"Message\":\"NotifyWriteTag\",\"Params\":{\"Tags\":[{\"Name\":\"Tag_0\",\"ErrorCode\":0,\"ErrorDescription\":\"\"},{\"Name\":\"Tag_1\",\"ErrorCode\":-2147483620,\"ErrorDescription\":\"Tag does not exist\"}]},\"ClientCookie\":\"myRequest2\"}";
            var w = RuntimeChannelsLogic.ParseResponseLine(wr, "myRequest2")!;
            check(w.Tags.Count == 2 && w.Tags[0].Ok && !w.Tags[1].Ok && w.Tags[0].Value == null, "NotifyWriteTag per-tag results parse (no values)");

            // NotifyReadAlarm uses lowercase "params" in the manual.
            string al = "{\"Message\":\"NotifyReadAlarm\",\"ClientCookie\":\"c\",\"params\":{\"Alarms\":[{\"Name\":\"RUNTIME_1::Tag_2:Alarm2\",\"State\":\"1\"}]}}";
            var a = RuntimeChannelsLogic.ParseResponseLine(al, "c")!;
            check(a.Params is JsonObject po && po["Alarms"] is JsonArray aa && aa.Count == 1 && a.Tags.Count == 0, "NotifyReadAlarm lowercase params exposed");

            // Bool values may come back as JSON booleans or numbers depending on the Runtime build.
            string boolLine = "{\"Message\":\"NotifyReadTag\",\"Params\":{\"Tags\":[{\"Name\":\"F\",\"Value\":true,\"ErrorCode\":0},{\"Name\":\"N\",\"Value\":12.5,\"ErrorCode\":\"0\"}]},\"ClientCookie\":\"c\"}";
            var b = RuntimeChannelsLogic.ParseResponseLine(boolLine, "c")!;
            check(b.Tags[0].Value == "TRUE" && b.Tags[1].Value == "12.5" && b.Tags[1].Ok, "non-string JSON values and string error codes normalised");
            check(Fails<FormatException>(() => RuntimeChannelsLogic.ParseResponseLine("not json", "c")), "garbage line raises FormatException");
            check(Fails<FormatException>(() => RuntimeChannelsLogic.ParseResponseLine("[1,2]", "c")), "non-object JSON line raises FormatException");
        }

        private static void RawRequests(Action<bool, string> check)
        {
            var (line, msg, cookie, ro) = RuntimeChannelsLogic.PrepareRawRequest("{\"Message\":\"BrowseTags\",\"Params\":{\"PageSize\":50}}");
            check(msg == "BrowseTags" && ro && cookie.StartsWith("tiamcp-raw-") && line.Contains("\"ClientCookie\":\"" + cookie + "\""), "[sentinel] raw read-only request gets a generated cookie");
            var (_, msg2, cookie2, ro2) = RuntimeChannelsLogic.PrepareRawRequest("{\"Message\":\"WriteTag\",\"Params\":{\"Tags\":[]},\"ClientCookie\":\"mine\"}");
            check(msg2 == "WriteTag" && !ro2 && cookie2 == "mine", "raw write request keeps caller cookie and is flagged non-read-only");
            check(RuntimeChannelsLogic.IsReadOnlyOpenPipeMessage("ReadConfig") && RuntimeChannelsLogic.IsReadOnlyOpenPipeMessage("BrowseAlarmClasses") && !RuntimeChannelsLogic.IsReadOnlyOpenPipeMessage("WriteConfig"), "read-only message allow-list");
            check(Fails<NotSupportedException>(() => RuntimeChannelsLogic.PrepareRawRequest("{\"Message\":\"SubscribeTag\",\"Params\":{\"Tags\":[\"a\"]}}")), "SubscribeTag refused (streaming)");
            check(Fails<NotSupportedException>(() => RuntimeChannelsLogic.PrepareRawRequest("{\"Message\":\"UnsubscribeAlarm\"}")), "UnsubscribeAlarm refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.PrepareRawRequest("")), "empty raw request refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.PrepareRawRequest("{\"Params\":{}}")), "raw request without Message refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.PrepareRawRequest("{\"Message\":\"ReadTag\"}\n{\"Message\":\"ReadTag\"}")), "multi-line raw request refused");
            check(Fails<ArgumentException>(() => RuntimeChannelsLogic.PrepareRawRequest("[\"ReadTag\"]")), "non-object raw request refused");
        }
    }
}
