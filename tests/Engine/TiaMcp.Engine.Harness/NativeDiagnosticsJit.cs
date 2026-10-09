using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;

internal static partial class Program
{
    private static void NativeDiagnosticsJit(string apiDirectory)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) => {
            string path = Path.Combine(Path.GetFullPath(apiDirectory), new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        SerializedCallGuardTests().GetAwaiter().GetResult();
        var type = Server.GetType("TiaMcpServer.Diagnostics.GeneratedNativeCalls", true)!;
        int prepared = 0, open = 0;
        foreach (var method in Server.GetTypes().Where(t => t.Name.StartsWith("__TiaMcpNativeCall_", StringComparison.Ordinal)).SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)))
        {
            if (method.ContainsGenericParameters) { open++; continue; }
            try { RuntimeHelpers.PrepareMethod(method.MethodHandle); prepared++; }
            catch (Exception ex) { throw new Exception("Invalid diagnostic wrapper " + method.Name, ex); }
        }
        Check(prepared > 1000, "Missing production native diagnostic wrappers");
        var pagesType = Server.GetType("TiaMcpServer.Siemens.MigrationPages", true)!;
        using (var pages = (IDisposable)Activator.CreateInstance(pagesType, All, null, new object?[] { null }, null)!)
        { pagesType.GetMethod("Expire", All)!.Invoke(pages, null); }
        Console.WriteLine("PASS native diagnostic wrappers preserve private-type access in timer cleanup");
        Console.WriteLine("COMPLETE: " + prepared + " native diagnostic wrappers JIT prepared; " + open + " open generic wrappers not prepared (generic patterns tested separately); no native call executed");
        int adapterChecks = 0;
        AdapterIntegrationChecks.Diagnostics(Server, EngineSurface.For(Server).Adapter!, (ok, message) => { Check(ok, message); adapterChecks++; });
        Console.WriteLine("COMPLETE: " + adapterChecks + " adapter integration diagnostic checks passed");
        EngineSurface.CheckHostRetirement(Server, Check);
        string previous = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
        string scratch = Path.Combine(Path.GetTempPath(), "tia-journal-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", scratch);
            string prior = Path.Combine(scratch, "calls-fixture.jsonl.previous");
            const string oldEncoding = "{\"text\":\"中文 😀 <>&'`+ \\\"quote\\\" \\n\\u0000\",\"null\":null,\"number\":-2146233079}";
            const string newEncoding = "{\"text\":\"\\u4E2D\\u6587 \\uD83D\\uDE00 \\u003C\\u003E\\u0026\\u0027\\u0060\\u002B \\u0022quote\\u0022 \\n\\u0000\",\"null\":null,\"number\":-2146233079}";
            File.WriteAllText(prior, "{\"nativeCallId\":\"pair\",\"phase\":\"BEFORE\",\"binding\":" + oldEncoding + "}\n");
            File.SetLastWriteTimeUtc(prior, DateTime.UtcNow.AddMinutes(-1));
            File.WriteAllText(Path.Combine(scratch, "calls-fixture.jsonl"), "{\"nativeCallId\":\"pair\",\"phase\":\"RETURNED\",\"binding\":" + newEncoding + "}\n{partial");
            File.WriteAllText(Path.Combine(scratch, "calls-fixture.jsonl.unrelated"), "{\"phase\":\"unrelated\"}\n");
            System.Text.Json.Nodes.JsonObject Read(int take)
            {
                var result = HostPortRunner.Call(Server, "GetNativeInvocationLog", new System.Text.Json.Nodes.JsonObject { ["take"] = take });
                Check(result["ok"]!.GetValue<bool>(), "Host native journal tool failed: " + result.ToJsonString());
                return result["data"]!["evidence"]!.AsObject();
            }
            var meta = Read(100);
            var records = meta["records"]!.AsArray();
            int readerChecks = 0;
            void ReaderCheck(bool ok, string message) { Check(ok, message); readerChecks++; }
            ReaderCheck(records.Count == 2 && records[0]!["phase"]!.GetValue<string>() == "BEFORE"
                && records[1]!["phase"]!.GetValue<string>() == "RETURNED"
                && records.All(row => row!["nativeCallId"]!.GetValue<string>() == "pair")
                && System.Text.Json.Nodes.JsonNode.DeepEquals(records[0]!["binding"], records[1]!["binding"])
                && meta["filesRead"]!.GetValue<int>() == 2, "Rotated native pair was not read");
            ReaderCheck(meta["malformedLines"]!.GetValue<int>() == 1 && !meta.ToJsonString().Contains("unrelated"),
                "Reader accepted unrelated file or hid truncation");
            var bounded = Read(1)["records"]!.AsArray();
            ReaderCheck(bounded.Count == 1 && bounded[0]!["phase"]!.GetValue<string>() == "RETURNED"
                && bounded[0]!["nativeCallId"]!.GetValue<string>() == "pair", "Reader bounded window is incorrect");
            Console.WriteLine("COMPLETE: " + readerChecks + " native journal reader checks passed; no native call executed");
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previous); Directory.Delete(scratch, true); }
    }
}
