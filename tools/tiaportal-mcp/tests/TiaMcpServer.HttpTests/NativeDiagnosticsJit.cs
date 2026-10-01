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
        string previous = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
        string scratch = Path.Combine(Path.GetTempPath(), "tia-journal-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", scratch);
            string prior = Path.Combine(scratch, "calls-fixture.jsonl.previous");
            File.WriteAllText(prior, "{\"nativeCallId\":\"pair\",\"phase\":\"BEFORE\"}\n");
            File.SetLastWriteTimeUtc(prior, DateTime.UtcNow.AddMinutes(-1));
            File.WriteAllText(Path.Combine(scratch, "calls-fixture.jsonl"), "{\"nativeCallId\":\"pair\",\"phase\":\"RETURNED\"}\n{partial");
            File.WriteAllText(Path.Combine(scratch, "calls-fixture.jsonl.unrelated"), "{\"phase\":\"unrelated\"}\n");
            var read = Server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!.GetMethod("ReadNativeInvocationLog", All)!;
            string Read(int take)
            {
                object response = read.Invoke(null, new object[] { take })!;
                return response.GetType().GetProperty("Meta")!.GetValue(response)!.ToString()!;
            }
            var meta = Parse(Read(100));
            Check(Convert.ToInt32(meta["filesRead"]) == 2 && Read(100).Contains("BEFORE") && Read(100).Contains("RETURNED"), "Rotated native pair was not read");
            Check(Convert.ToInt32(meta["malformedLines"]) == 1 && !Read(100).Contains("unrelated"), "Reader accepted unrelated file or hid truncation");
            Check(!Read(1).Contains("BEFORE") && Read(1).Contains("RETURNED"), "Reader bounded window is incorrect");
            Console.WriteLine("COMPLETE: 3 native journal reader checks passed; no native call executed");
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previous); Directory.Delete(scratch, true); }
    }
}
