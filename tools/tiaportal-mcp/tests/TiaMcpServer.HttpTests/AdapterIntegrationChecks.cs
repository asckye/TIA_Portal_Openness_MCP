using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

internal static class AdapterIntegrationChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    internal static void Surface(Assembly engine, EngineSurface surface, Action<bool, string> check)
    {
        var adapter = surface.Adapter ?? throw new InvalidOperationException("The engine has no shared adapter reference.");
        check(Path.GetDirectoryName(adapter.Location) == Path.GetDirectoryName(engine.Location),
            "EngineSurface loads the adapter next to the woven engine");
        var assemblies = new[] { engine, adapter, Assembly.Load("TiaMcp.Logic"), Assembly.Load("TiaMcp.Adapters.Contracts") };
        var duplicates = assemblies.SelectMany(assembly => assembly.GetExportedTypes()).GroupBy(type => type.FullName)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        check(duplicates.Length == 0, "No duplicate public types across engine, Logic, contracts and adapter: " + string.Join(", ", duplicates));
        check(surface.CrossAssemblyMemberNames().Length == 0, "No member name resolves in both engine and adapter surfaces");
        var toolNames = assemblies.Take(2).SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(type => type.GetMethods(All)).Select(method => new { Method = method, Tool = method.GetCustomAttribute<McpServerToolAttribute>() })
            .Where(entry => entry.Tool != null).GroupBy(entry => entry.Tool!.Name ?? entry.Method.Name, StringComparer.OrdinalIgnoreCase);
        check(toolNames.All(group => group.Select(entry => entry.Method.DeclaringType!.Assembly).Distinct().Count() == 1),
            "No MCP tool name resolves in both engine and adapter");
        var over = surface.Method("Over");
        check(over.DeclaringType!.Assembly == adapter, "EngineSurface resolves the typed PLC entry point in the adapter");
        var projectType = over.GetParameters().Single().ParameterType.GetGenericArguments().Single();
        check(projectType.FullName == "Siemens.Engineering.ProjectBase" && over.ReturnType == over.DeclaringType,
            "PlcServices.Over accepts Func<ProjectBase> and returns the borrowed PLC surfaces");
        typeof(AdapterIntegrationChecks).GetMethod(nameof(BorrowedProject), All)!.MakeGenericMethod(projectType)
            .Invoke(null, new object[] { over, check });
    }

    private static void BorrowedProject<T>(MethodInfo over, Action<bool, string> check) where T : class
    {
        int reads = 0;
        T? current = null;
        Func<T> provider = () => { reads++; return current!; };
        var services = over.Invoke(null, new object[] { provider })!;
        check(reads == 0, "Building PLC surfaces does not evaluate or attach the borrowed project");
        var facets = new[] { "PlcProgram", "PlcData" }.Select(name => services.GetType().GetProperty(name)!.GetValue(services)!).ToArray();
        foreach (var facet in facets)
        {
            var project = facet.GetType().GetProperty("CurrentProject", All)!;
            current = null;
            check(project.GetValue(facet) == null, "PLC surface reads the current empty binding");
            // Uninitialized managed reference only: never invoke a Siemens constructor/getter.
            var concrete = typeof(T).Assembly.GetType("Siemens.Engineering.Project", true)!;
            current = (T)FormatterServices.GetUninitializedObject(concrete);
            check(ReferenceEquals(project.GetValue(facet), current), "PLC surface observes the new kernel handle without caching or ownership transfer");
        }
        check(reads == 4, "Each PLC surface access evaluates the kernel provider exactly once");
        bool rejected = false;
        try { over.Invoke(null, new object?[] { null }); }
        catch (TargetInvocationException ex) { rejected = ex.InnerException is ArgumentNullException; }
        check(rejected, "Typed PLC entry point rejects a missing provider");
    }

    internal static void Diagnostics(Assembly engine, Assembly adapter, Action<bool, string> check)
    {
        int prepared = 0, open = 0;
        foreach (var method in adapter.GetTypes().Where(type => type.Name.StartsWith("__TiaMcpNativeCall_", StringComparison.Ordinal))
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
        {
            if (method.ContainsGenericParameters) { open++; continue; }
            RuntimeHelpers.PrepareMethod(method.MethodHandle); prepared++;
        }
        check(prepared > 1000, "Adapter native wrappers can be JIT prepared without executing native calls");
        Console.WriteLine("COMPLETE: " + prepared + " adapter native diagnostic wrappers JIT prepared; " + open + " open generic wrappers");
        var previous = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY");
        var scratch = Path.Combine(Path.GetTempPath(), "tia-adapter-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", scratch);
            var journal = adapter.GetType("TiaMcpServer.ModelContextProtocol.InvocationJournal", true)!;
            // A separate timestamp makes two independent file sinks detectably different.
            RuntimeHelpers.RunClassConstructor(journal.TypeHandle);
            Thread.Sleep(1100);
            var diagnostics = adapter.GetType("TiaMcpServer.ModelContextProtocol.NativeCallDiagnostics", true)!;
            var wrapper = engine.GetType("TiaMcpServer.ModelContextProtocol.SerializedCallTool", true)!;
            var tool = (McpServerTool)Activator.CreateInstance(wrapper, All, null, new object[] { new JournalTool(diagnostics) }, null)!;
            for (int i = 0; i < 2; i++) tool.InvokeAsync(null!, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var files = Directory.GetFiles(scratch, "calls-*.jsonl");
            check(files.Length == 1, "Engine tool and adapter native span share exactly one journal file");
            var rows = File.ReadAllLines(files.Single()).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
            check(rows.Length == 8, "Two engine calls each flush tool/native BEFORE and RETURNED rows");
            for (int i = 0; i < 2; i++)
            {
                var call = rows.Skip(i * 4).Take(4).ToArray();
                check(call.Select(row => row["id"]!.GetValue<string>()).Distinct().Count() == 1,
                    "Adapter native span retains the engine correlation id across an asynchronous tool call");
                check(call.Select(row => row["tool"]!.GetValue<string>() + ":" + row["phase"]!.GetValue<string>()).SequenceEqual(
                    new[] { "AdapterJournalFixture:BEFORE", "native:adapter-fixture:BEFORE", "native:adapter-fixture:RETURNED", "AdapterJournalFixture:RETURNED" })
                    && !string.IsNullOrEmpty(call[1]["nativeCallId"]?.GetValue<string>())
                    && call[1]["nativeCallId"]!.GetValue<string>() == call[2]["nativeCallId"]!.GetValue<string>(),
                    "Shared journal preserves the woven diagnostics span id and tool/native ordering");
            }
            check(rows[0]["id"]!.GetValue<string>() != rows[4]["id"]!.GetValue<string>(), "Separate engine calls retain separate correlation ids");
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY", previous); Directory.Delete(scratch, true); }
    }

    private sealed class JournalTool : McpServerTool
    {
        private readonly Type diagnostics;
        internal JournalTool(Type diagnostics) { this.diagnostics = diagnostics; }
        public override Tool ProtocolTool => new Tool { Name = "AdapterJournalFixture" };
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            // Exercise the adapter-side entry points used by the woven wrappers,
            // with a synthetic direct span and no Siemens operation between them.
            var span = diagnostics.GetMethod("Enter", All)!.Invoke(null,
                new object?[] { "adapter-fixture-site", "adapter-fixture", "direct", null, null, null });
            diagnostics.GetMethod("Returned", All)!.Invoke(null, new object?[] { span, null });
            return new CallToolResult();
        }
    }
}
