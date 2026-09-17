using System;
using System.Linq;
using System.Reflection;

// Shape checks for the 2.7.19 families: PLCSIM Advanced channel (late-bound; the API is not on the
// build machine, so the tool-level safety defaults and the reflection member names are checked),
// offline documentation/lint tools and the AML builder (pure file tools, taxonomy tags + defaults).
internal static class SimulationDocumentationShapeChecks
{
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var tools = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        MethodInfo Tool(string name) { var m = tools.GetMethod(name); check(m != null, name + " exposed"); return m!; }
        object? Default(MethodInfo m, string p) => m.GetParameters().Single(x => x.Name == p).DefaultValue;
        string Description(MethodInfo m) => m.GetCustomAttributes().Select(a => a.GetType().GetProperty("Description")?.GetValue(a) as string).FirstOrDefault(d => d != null) ?? "";

        // ---- PLCSIM Advanced: read tools have no dryRun, mutating tools default to preview + explicit confirm
        foreach (var name in new[] { "ReadPlcSimAdvancedInstances", "ReadPlcSimAdvancedTags" })
            check(Tool(name).GetParameters().All(p => p.Name != "dryRun") && Description(Tool(name)).StartsWith("[L2][Simulation][ONLINE]", StringComparison.Ordinal), name + " read-only, tagged [Simulation][ONLINE]");
        var manage = Tool("ManagePlcSimAdvancedInstance");
        check(Equals(Default(manage, "dryRun"), true) && Equals(Default(manage, "confirmInstanceChange"), false) && Description(manage).StartsWith("[L2][Simulation][ONLINE-WRITE]", StringComparison.Ordinal), "ManagePlcSimAdvancedInstance preview + confirmInstanceChange, ONLINE-WRITE");
        var write = Tool("WritePlcSimAdvancedTags");
        check(Equals(Default(write, "dryRun"), true) && Equals(Default(write, "confirmWrite"), false) && Description(write).StartsWith("[L2][Simulation][ONLINE-WRITE]", StringComparison.Ordinal), "WritePlcSimAdvancedTags preview + confirmWrite, ONLINE-WRITE");
        var scenario = Tool("RunPlcSimAdvancedTestScenario");
        check(Equals(Default(scenario, "dryRun"), true) && Equals(Default(scenario, "confirmRun"), false) && Description(scenario).StartsWith("[L2][Simulation][EXECUTE]", StringComparison.Ordinal), "RunPlcSimAdvancedTestScenario preview + confirmRun, EXECUTE");
        foreach (var name in new[] { "ReadPlcSimAdvancedInstances", "ManagePlcSimAdvancedInstance", "ReadPlcSimAdvancedTags", "WritePlcSimAdvancedTags", "RunPlcSimAdvancedTestScenario" })
            check(Equals(Default(Tool(name), "apiPath"), ""), name + " auto-detects the API by default");

        var channel = server.GetType("TiaMcpServer.Runtime.PlcSimAdvancedChannel", true)!;
        foreach (var member in new[] { "Load", "ProbePaths", "RegisteredInstances", "OpenInterface", "Register", "Lifecycle", "SetOperatingMode", "RunToNextSyncPoint", "UpdateTagList", "Tags", "Read", "Write", "Dispose" })
            check(channel.GetMethod(member) != null, "PlcSimAdvancedChannel." + member);
        var logic = server.GetType("TiaMcpServer.Runtime.PlcSimAdvancedLogic", true)!;
        check((string?)logic.GetField("ApiFileName")?.GetRawConstantValue() == "Siemens.Simatic.Simulation.Runtime.Api.x64.dll", "API file name is the official x64 assembly");
        check(server.GetReferencedAssemblies().All(a => !a.Name.StartsWith("Siemens.Simatic.Simulation", StringComparison.OrdinalIgnoreCase)), "server does not reference the PLCSIM Advanced assembly at compile time (late binding only)");

        // ---- offline documentation / lint / AML: no dryRun (pure file tools), outputs are NEW files, taxonomy tags present
        var render = Tool("RenderPlcBlockDocument");
        check(Equals(Default(render, "mermaidDirection"), "LR") && Equals(Default(render, "outputPath"), "") && Description(render).StartsWith("[L2][Validation][OFFLINE]", StringComparison.Ordinal), "RenderPlcBlockDocument defaults + tag");
        var handbook = Tool("GeneratePlcDocumentation");
        check(handbook.GetParameters().Any(p => p.Name == "outputPath" && !p.HasDefaultValue) && Equals(Default(handbook, "recursive"), true) && Description(handbook).StartsWith("[L2][Validation][FILE]", StringComparison.Ordinal), "GeneratePlcDocumentation requires outputPath, recursive by default, FILE");
        var lint = Tool("LintPlcSclSource");
        check(Equals(Default(lint, "limit"), 500) && Description(lint).StartsWith("[L2][Validation][OFFLINE]", StringComparison.Ordinal), "LintPlcSclSource defaults + tag");
        var aml = Tool("BuildDeviceAmlDocument");
        check(aml.GetParameters().Any(p => p.Name == "outputPath" && !p.HasDefaultValue) && Equals(Default(aml, "referenceAmlPath"), "") && Description(aml).StartsWith("[L2][Hardware][OFFLINE]", StringComparison.Ordinal), "BuildDeviceAmlDocument requires outputPath, optional reference, OFFLINE");
        foreach (var name in new[] { "RenderPlcBlockDocument", "GeneratePlcDocumentation", "LintPlcSclSource", "BuildDeviceAmlDocument" })
            check(Tool(name).GetParameters().All(p => p.Name != "dryRun"), name + " has no dryRun (pure offline tool)");
        var docLogic = server.GetType("TiaMcpServer.ModelContextProtocol.PlcDocumentationLogic", true)!;
        check(docLogic.GetMethod("RuleCatalog") != null && docLogic.GetMethod("ParseFlgNet") != null && docLogic.GetMethod("Mermaid") != null, "PlcDocumentationLogic rendering/lint entry points");
        check(server.GetType("TiaMcpServer.Siemens.HardwareAmlLogic", true)!.GetMethod("BuiltinSkeleton") != null, "HardwareAmlLogic.BuiltinSkeleton");
    }
}
