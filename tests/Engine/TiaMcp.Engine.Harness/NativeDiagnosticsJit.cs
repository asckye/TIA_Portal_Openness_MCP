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
            var read = EngineSurface.For(Server).Tool("GetNativeInvocationLog")!;
            string Read(int take)
            {
                object response = EngineSurface.For(Server).Invoke(read, new object[] { take })!;
                return ((global::ModelContextProtocol.Protocol.CallToolResult)response).StructuredContent!["data"]!["evidence"]!.ToJsonString();
            }
            var meta = Parse(Read(100));
            var records = ((System.Collections.IEnumerable)meta["records"]).Cast<System.Collections.Generic.Dictionary<string, object>>().ToArray();
            Check(Json.Serialize(records[0]["binding"]) == Json.Serialize(records[1]["binding"]) && Convert.ToInt32(meta["filesRead"]) == 2 && Read(100).Contains("BEFORE") && Read(100).Contains("RETURNED"), "Rotated native pair was not read");
            Check(Convert.ToInt32(meta["malformedLines"]) == 1 && !Read(100).Contains("unrelated"), "Reader accepted unrelated file or hid truncation");
            Check(!Read(1).Contains("BEFORE") && Read(1).Contains("RETURNED"), "Reader bounded window is incorrect");
            Console.WriteLine("COMPLETE: 3 native journal reader checks passed; no native call executed");
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previous); Directory.Delete(scratch, true); }
    }
}
