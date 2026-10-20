using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

internal static class PlcDiagnosticsChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static void Run(Assembly adapter, string inventory, Action<bool, string> check)
    {
        int prepared = 0, open = 0;
        foreach (var method in adapter.GetTypes().Where(t => t.Name.StartsWith("__TiaMcpNativeCall_", StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
        {
            if (method.ContainsGenericParameters) { open++; continue; }
            RuntimeHelpers.PrepareMethod(method.MethodHandle); prepared++;
        }
        check(prepared > 1000, "Adapter production native wrappers can be JIT prepared without native calls");
        check(prepared + open == (int)JsonNode.Parse(File.ReadAllText(inventory))!["count"]!, "Adapter JIT plus open generics equals the exact weave inventory");
        Console.WriteLine("COMPLETE: " + prepared + " adapter native diagnostic wrappers JIT prepared; " + open + " open generic wrappers");
        string scratch = Path.Combine(Path.GetTempPath(), "plc-harness-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", scratch);
            var journal = adapter.GetType("TiaMcpServer.ModelContextProtocol.InvocationJournal", true)!;
            var diagnostics = adapter.GetType("TiaMcpServer.ModelContextProtocol.NativeCallDiagnostics", true)!;
            async Task Call()
            {
                string id = (string)journal.GetMethod("Begin", All)!.Invoke(null, new object?[] { "AdapterJournalFixture", null })!;
                using var scope = (IDisposable)journal.GetMethod("UseCorrelation", All)!.Invoke(null, new object[] { id })!;
                await Task.Yield();
                var span = diagnostics.GetMethod("Enter", All)!.Invoke(null, new object?[] { "adapter-fixture-site", "adapter-fixture", "direct", null, null, null });
                diagnostics.GetMethod("Returned", All)!.Invoke(null, new object?[] { span, null });
                journal.GetMethod("WriteRow", All)!.Invoke(null, new object?[] { id, "AdapterJournalFixture", "RETURNED", null, null, null });
            }
            Call().GetAwaiter().GetResult(); Call().GetAwaiter().GetResult();
            var files = Directory.GetFiles(scratch, "calls-*.jsonl");
            check(files.Length == 1, "Adapter tool and native span share one journal file");
            var rows = File.ReadAllLines(files.Single()).Select(line => JsonNode.Parse(line)!).ToArray();
            check(rows.Length == 8, "Two adapter calls each flush tool/native BEFORE and RETURNED rows");
            for (int i = 0; i < 2; i++)
            {
                var call = rows.Skip(4 * i).Take(4).ToArray();
                check(call.Select(row => (string)row["id"]!).Distinct().Count() == 1, "Adapter retains correlation across an asynchronous call");
                check(call.Select(row => (string)row["tool"]! + ":" + (string)row["phase"]!).SequenceEqual(new[] {
                    "AdapterJournalFixture:BEFORE", "native:adapter-fixture:BEFORE", "native:adapter-fixture:RETURNED", "AdapterJournalFixture:RETURNED"
                }), "Adapter retains tool/native ordering");
                check(!string.IsNullOrEmpty((string?)call[1]["nativeCallId"]) && (string?)call[1]["nativeCallId"] == (string?)call[2]["nativeCallId"], "Adapter retains native span identity");
            }
            check((string)rows[0]["id"]! != (string)rows[4]["id"]!, "Separate adapter calls have separate correlation ids");
            Console.WriteLine("COMPLETE: 9 adapter integration diagnostic checks passed");
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previous); Directory.Delete(scratch, true); }
    }
}
