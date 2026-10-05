using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcpServer.ModelContextProtocol;

internal static class InvocationJournalGoldenTests
{
    internal static void Run(string directory)
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        const string id = "0123456789abcdef0123456789abcdef";
        const string text = "中文 😀 \"quotes\" \\ / <>&'+`\0\b\f\n\r\t\u001f\u007f\u0085\u2028\u2029";
        var utc = new DateTime(2026, 10, 3, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567);
        var binding = new JsonObject { ["path"] = text, ["pid"] = 42, ["missing"] = null,
            ["epoch"] = long.MaxValue, ["modified"] = false, ["utc"] = utc.ToString("O") };
        var oldNew = new JsonArray();
        foreach (string phase in new[] { "BEFORE", "RETURNED", "THREW" })
        foreach (bool withBinding in new[] { false, true })
        foreach (bool withDetails in new[] { false, true })
        {
            Exception? error = phase == "THREW" ? new InvalidOperationException("secret", new IOException("hidden")) : null;
            var exceptions = new JsonArray();
            for (var current = error; current != null && exceptions.Count < 8; current = current.InnerException)
                exceptions.Add(new JsonObject { ["type"] = current.GetType().FullName, ["hresult"] = current.HResult });
            var details = new JsonObject { ["nativeCallId"] = id, ["parentNativeCallId"] = null, ["callSite"] = text,
                ["member"] = "Native::Find()", ["dispatch"] = "direct", ["objectId"] = id,
                ["pathKind"] = "observed-access-lineage", ["exceptionType"] = error?.GetType().FullName,
                ["hresult"] = error == null ? (int?)null : error.HResult, ["exceptionChain"] = exceptions };
            // Frozen pre-refactor engine row construction; fixed clock and identities.
            var expected = new JsonObject { ["utc"] = utc.ToString("O"), ["id"] = id, ["tool"] = text, ["phase"] = phase,
                ["mcpProcessId"] = 123, ["objectType"] = null, ["objectPath"] = text,
                ["binding"] = withBinding ? binding.DeepClone() : null, ["threadId"] = 7, ["apartment"] = "MTA" };
            if (withDetails) foreach (var pair in details) expected[pair.Key] = pair.Value?.DeepClone();
            string actual = InvocationJournal.FormatRow(utc, id, text, phase, 123, null, text,
                withBinding ? binding.ToJsonString() : null, 7, "MTA",
                withDetails ? NativeCallDiagnostics.Details(id, null, text, "Native::Find()", "direct", id, error) : null);
            Check(Encoding.UTF8.GetBytes(actual).SequenceEqual(Encoding.UTF8.GetBytes(expected.ToJsonString())), "engine golden bytes " + phase);
            // The old adapter's JObject writer uses literal Unicode and different escaping.
            using (var reader = new JsonTextReader(new StringReader(expected.ToJsonString())) { DateParseHandling = DateParseHandling.None })
            {
                string old = JObject.Load(reader).ToString(Formatting.None);
                Check(JsonNode.DeepEquals(JsonNode.Parse(old), JsonNode.Parse(actual)), "old adapter decoded values " + phase);
                oldNew.Add(new JsonObject { ["old"] = old, ["new"] = actual });
            }
        }
        File.WriteAllText(Path.Combine(directory, "encoding-golden.json"), oldNew.ToJsonString(), new UTF8Encoding(false));
        // Exhaust the BMP, including every control and isolated surrogate, then pairs.
        for (int start = 0; start < 65536; start += 128)
        {
            string value = new string(Enumerable.Range(start, 128).Select(c => (char)c).ToArray());
            Check(InvocationJournal.JsonLineObject.Quote(value) == JsonValue.Create(value)!.ToJsonString(), "STJ escaping at " + start);
        }
        foreach (string value in new[] { "\ud800\udc00", "\udbff\udfff", "\ud800x\udc00", "\ud800\ud800\udc00", text })
            Check(InvocationJournal.JsonLineObject.Quote(value) == JsonValue.Create(value)!.ToJsonString(), "STJ UTF16 escaping");
        Exception chain = new IOException();
        for (int i = 0; i < 10; i++) chain = new InvalidOperationException("hidden", chain);
        var capped = JsonNode.Parse(NativeCallDiagnostics.Details(id, id, text, "member", "direct", id, chain).ToString())!;
        Check(capped["exceptionChain"]!.AsArray().Count == 8 && capped["parentNativeCallId"]!.GetValue<string>() == id, "exception cap and parent id");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Check(new InvocationJournal.JsonLineObject().Number("min", long.MinValue).Number("null", null).ToString() ==
                new JsonObject { ["min"] = long.MinValue, ["null"] = null }.ToJsonString(), "invariant numbers");
        }
        finally { CultureInfo.CurrentCulture = culture; }

        var lines = new List<string>();
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", "invalid-for-file-sink");
            InvocationJournal.ConfigureOutput(lines.Add, () => id);
            Check(InvocationJournal.Begin("sink") == id && InvocationJournal.CorrelationId == id, "host sink correlation seam");
            int thread = Thread.CurrentThread.ManagedThreadId;
            int result = InvocationJournal.Native("sink", () => {
                Check(lines.Count == 2 && JsonNode.Parse(lines[1])!["phase"]!.GetValue<string>() == "BEFORE", "sink flushed before native delegate");
                Check(Thread.CurrentThread.ManagedThreadId == thread, "native thread unchanged");
                return 42;
            });
            Check(result == 42 && lines.Count == 3 && lines.All(line => JsonNode.Parse(line)!["id"]!.GetValue<string>() == id), "sink result and correlation");
            var failure = new IOException("secret");
            try { InvocationJournal.Native<int>("throws", () => throw failure); }
            catch (IOException caught) { Check(ReferenceEquals(failure, caught), "original exception retained"); }
            Check(JsonNode.Parse(lines.Last())!["phase"]!.GetValue<string>() == "THREW", "sink exception completion");
            string forwarded = lines.Last();
            InvocationJournal.WriteLine(forwarded);
            Check(lines.Last() == forwarded && lines.Count == 6, "host accepts adapter row without re-encoding");
            long failed = JsonNode.Parse(InvocationJournal.Health().ToString())!["failedWrites"]!.GetValue<long>();
            InvocationJournal.ConfigureOutput(_ => throw new IOException());
            InvocationJournal.Begin("failed");
            var health = new JsonObject { ["failedWrites"] = failed + 1, ["lastFailure"] = "IOException",
                ["durability"] = "best-effort; successful writes flushed to disk",
                ["nativeBoundaryCoverage"] = typeof(InvocationJournal).Assembly.GetType("TiaMcpServer.Diagnostics.GeneratedNativeCalls", false) != null ? "build-instrumented" : "not-instrumented" };
#if JOURNAL_ADAPTER
            Check(InvocationJournal.Health().ToString() == health.ToJsonString(), "health bytes");
            InvocationJournal.ConfigureOutput(lines.Add);
            InvocationJournal.BindingSnapshot = () => new InvocationJournal.JsonLineObject().String("path", text).Number("pid", 42).String("missing", null);
            InvocationJournal.Write(id, "binding", "BEFORE");
            Check(JsonNode.Parse(lines.Last())!["binding"]!.ToJsonString() ==
                new JsonObject { ["path"] = text, ["pid"] = 42, ["missing"] = null }.ToJsonString(), "adapter binding encoding");
            InvocationJournal.BindingSnapshot = null;
#else
            Check(InvocationJournal.Health().ToJsonString() == health.ToJsonString(), "health bytes and JsonObject type");
            InvocationJournal.ConfigureOutput(lines.Add);
            InvocationJournal.BindingSnapshot = () => binding;
            var extra = new JsonObject { ["phase"] = "overridden", ["fraction"] = 1.250m, ["date"] = utc,
                [text] = new JsonArray(null, true, 1e-50), ["null"] = null };
            InvocationJournal.Write(id, "binding", "BEFORE", details: extra);
            var written = JsonNode.Parse(lines.Last())!.AsObject();
            Check(written["binding"]!.ToJsonString() == binding.ToJsonString(), "binding host bytes");
            Check(extra.All(pair => written[pair.Key]?.ToJsonString() == pair.Value?.ToJsonString()), "host detail values and overwrite");
            Check(written.Select(pair => pair.Key).Take(10).SequenceEqual(new[] { "utc", "id", "tool", "phase", "mcpProcessId", "objectType", "objectPath", "binding", "threadId", "apartment" }), "detail overwrite keeps property order");
            InvocationJournal.BindingSnapshot = null;
#endif
            InvocationJournal.ConfigureOutput(null);
            string rotation = Path.Combine(directory, "rotation");
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", rotation);
            new TiaOpenness.Shared.JournalRetention(1, 3).Save(TiaOpenness.Shared.JournalRetention.SettingsPath);
            InvocationJournal.ConfigureOutput(null);
            InvocationJournal.Begin("file", id);
            string file = Directory.GetFiles(rotation, "calls-*.jsonl").Single();
            using (var stream = new FileStream(file, FileMode.Open)) stream.SetLength(1024 * 1024 - 4096);
            InvocationJournal.Write(id, "limit", "BEFORE");
            Check(!File.Exists(file + ".1"), "row below configured limit remains in current file");
            using (var stream = new FileStream(file, FileMode.Open)) stream.SetLength(1024 * 1024);
            File.WriteAllText(file + ".previous", "old rotation");
            InvocationJournal.Write(id, "rotate", "BEFORE");
            Check(new FileInfo(file + ".1").Length == 1024 * 1024 && File.ReadAllLines(file).Length == 1 && File.ReadAllText(file + ".2") == "old rotation", "configured rotation migrates previous and keeps ordered copies");
            byte[] bytes = File.ReadAllBytes(file);
            Check(bytes[0] == (byte)'{' && Encoding.UTF8.GetString(bytes).EndsWith(Environment.NewLine, StringComparison.Ordinal), "no BOM and original newline");
        }
        finally
        {
            InvocationJournal.ConfigureOutput(null);
            new TiaOpenness.Shared.JournalRetention().Save(TiaOpenness.Shared.JournalRetention.SettingsPath);
            InvocationJournal.BindingSnapshot = null;
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previous);
        }
        Console.WriteLine("COMPLETE: " + checks + " invocation journal golden checks passed");
    }
}
