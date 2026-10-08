using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class PilotToolChecks
{
    // The served example resolves the catalog's format tokens ({release}, {major}, {extension}) for this release.
    private static bool SameExample(JsonNode? generated, JsonNode? served, string release)
    {
        if (generated is JsonValue g && served is JsonValue s && g.TryGetValue<string>(out var text) && s.TryGetValue<string>(out var actual))
        {
            string major = release == "14sp1" ? "14" : release == "15.1" ? "15" : release;
            string pattern = Regex.Escape(text).Replace(Regex.Escape("{release}"), Regex.Escape(release))
                .Replace(Regex.Escape("{major}"), Regex.Escape(major)).Replace(Regex.Escape("{extension}"), "[A-Za-z0-9]+");
            return Regex.IsMatch(actual, "^" + pattern + "$");
        }
        if (generated is JsonObject go && served is JsonObject so)
            return go.Count == so.Count && go.All(p => so.ContainsKey(p.Key) && SameExample(p.Value, so[p.Key], release));
        if (generated is JsonArray ga && served is JsonArray sa)
            return ga.Count == sa.Count && ga.Zip(sa, (a, b) => SameExample(a, b, release)).All(same => same);
        return JsonNode.DeepEquals(generated, served);
    }

    internal static void UsageServesEveryMigratedExampleAgainstItsActualSchema(Assembly server, Action<bool, string> check)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var surface = EngineSurface.For(server);
        var facade = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        string release = (string)facade.GetProperty("ReleaseKey", all)!.GetValue(null)!;
        var catalog = Program.FindServerType(server, "TiaOpenness.Shared.ToolUsageCatalog");
        // The engine serves its own source catalog (McpServer.RuntimeProfileEntries); reflection passes every optional argument.
        var entries = ((JsonArray)catalog.GetMethod("ProfileEntries", all)!.Invoke(null, new object[] { release, 4, true })!)
            .Where(row => (int?)row!["envelopeVersion"] == 4).ToArray();
        check(entries.Length > 0, "Generated runtime data contains V4 entries");
        var usage = surface.Tool("GetToolUsage");
        var schemaType = Program.FindServerType(server, "TiaMcp.Logic.V4.Inputs.InputSchema");
        foreach (var entry in entries)
        {
            string name = (string)entry!["currentName"]!;
            var method = surface.Tool(name);
            var selected = ((IReadOnlyDictionary<string, MethodInfo>)facade.GetMethod("AllToolMethods", all)!.Invoke(null, new object[] { true })!)[name];
            var selectedPolicy = selected.GetCustomAttributes().SingleOrDefault(a => a.GetType().Name == "BehaviorCandidateAttribute");
            if (selectedPolicy != null && (string?)selectedPolicy.GetType().GetProperty("Family")!.GetValue(selectedPolicy) == "P6-FALLBACK") method = selected;
            var tool = (McpServerTool)facade.GetMethod("CreateTool", all)!.Invoke(null, new object[] { name, method })!;
            var arguments = usage.GetParameters().Select(p => p.Name == "toolName" ? name : p.DefaultValue).ToArray();
            var result = (CallToolResult)surface.Invoke(usage, arguments)!;
            var body = JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!;
            check((bool?)body["ok"] == true, name + " usage retrieval succeeds");
            var data = body["data"]!;
            var actual = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
            check(JsonNode.DeepEquals(actual, data["inputSchema"]), name + " usage serves its actual registered schema");
            var example = data["example"]!;
            check((string?)example["kind"] == "parameterized-call-example", name + " serves a parameterized example");
            var served = example["request"]!["params"]!["arguments"]!.AsObject();
            var expected = entry["arguments"]!.AsObject();
            var candidate = method.GetCustomAttributes().SingleOrDefault(a => a.GetType().Name == "BehaviorCandidateAttribute");
            if (candidate != null && (string?)candidate.GetType().GetProperty("Family")!.GetValue(candidate) == "P6-FALLBACK")
                expected = (JsonObject)Program.FindServerType(server, "TiaMcp.Logic.V4.BehaviorCapabilities").GetMethod("CandidateExample", all)!.Invoke(null, new object[] { release, name })!;
            foreach (var value in expected)
                check(SameExample(value.Value, served[value.Key], release), name + "." + value.Key + " preserves the generated example");
            foreach (var value in served.Where(p => !expected.ContainsKey(p.Key)))
                check(JsonNode.DeepEquals(actual["properties"]![value.Key]!["default"], value.Value), name + "." + value.Key + " uses its declared default");
            var schema = Activator.CreateInstance(schemaType, new object[] { tool.ProtocolTool.InputSchema })!;
            foreach (var sample in new JsonNode[] { served })
                check(schemaType.GetMethod("Validate")!.Invoke(schema, new object[] { JsonSerializer.SerializeToElement(sample), "arguments" }) == null,
                    name + " example satisfies its actual schema");
        }
        Console.WriteLine("Checked every generated V4 entry: " + entries.Length);
    }

    internal static void Run(Assembly server, Action<bool, string> check)
    {
        UsageServesEveryMigratedExampleAgainstItsActualSchema(server, check);
        var surface = EngineSurface.For(server);
        var domains = new Dictionary<string, string[]>
        {
            ["EcosystemTools"] = new[] { "RenderPlcVisualDiff", "GetOpennessGuidance", "RunPlcCompanionTool" },
            ["V21EcosystemTools"] = new[] { "GetV21EcosystemCatalog", "ValidatePlcDocumentSchemas", "ManageUnifiedCwcPackage", "DecodePlcSimaticMl" },
            ["GitWorkflowTools"] = new[] { "ManagePlcGitRepository" },
            ["TemplateTools"] = new[] { "BuildPlcAliasAlarmLad", "InstantiatePlcTemplates" },
            ["QualityAuditTools"] = new[] { "AuditEngineeringExports" },
            ["ImportOrderTools"] = new[] { "PlanArtifactImportOrder" },
            ["ToolUsageTools"] = new[] { "GetToolUsage" },
            ["OfflineSuiteTools"] = new[]
            {
                "BuildClassicHmiTagTable", "BuildClassicHmiMinimalPackage", "WriteClassicHmiMinimalPackageFiles",
                "ValidateClassicHmiMinimalPackageFiles", "ValidateClassicHmiMinimalPackagePlcSync", "BuildPlcSymbolManifestFromPath",
                "RunClassicHmiOfflineValidationSuite", "RunOfflineReleaseValidationSuite", "BuildReleaseDiagnosticReport",
                "BuildReleaseRunbook", "BuildReleaseManifest", "BuildReleaseHandoffArtifacts", "RunClassicHmiTemporaryImportPreflight",
                "RunHmiTemplatePlcSyncPrecheckSuite", "BuildUnifiedHmiTemplateApplyDesign", "BuildUnifiedHmiTemplateApplyDesignManifest"
            }
        };
        foreach (var domain in domains)
        {
            var type = server.GetType("TiaMcpServer.ModelContextProtocol." + domain.Key, true)!;
            check(type.IsSealed && type.GetCustomAttribute<McpServerToolTypeAttribute>() != null
                && !type.GetInterfaces().Any(item => item.Name == "IDisposable" || item.Name == "IAsyncDisposable"),
                "Pilot tool type is a non-disposable instance class: " + domain.Key);
            object? singleton = null;
            foreach (var name in domain.Value)
            {
                var method = surface.Tool(name);
                var target = surface.Target(method);
                singleton = singleton ?? target;
                check(!method.IsStatic && method.DeclaringType == type && target != null
                    && ReferenceEquals(target, singleton) && ReferenceEquals(target, surface.Target(method)),
                    "Pilot catalog and root singleton agree: " + name);
            }
        }

        var facade = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!.GetMethod("GetToolUsage")!;
        var usage = surface.Tool("GetToolUsage");
        check(facade == null && !usage.IsStatic,
            "Tool usage resolves to the instance without a duplicate static entry");
        var arguments = usage.GetParameters().Select(parameter => parameter.DefaultValue).ToArray();
        var provider = (IServiceProvider)server.GetType("TiaMcpServer.EngineServices", true)!
            .GetProperty("Provider", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var resolved = usage.Invoke(provider.GetService(usage.DeclaringType!), arguments)!;
        var direct = surface.Invoke(usage, arguments)!;
        string Data(object result) => JsonDocument.Parse(((TextContentBlock)((CallToolResult)result).Content.Single()).Text).RootElement.GetProperty("data").GetRawText();
        check(Data(resolved) == Data(direct),
            "EngineServices and EngineSurface return the same tool usage result");
        check(server.GetType("TiaMcpServer.ModelContextProtocol.GuideTools") == null,
            "Merged guide has no unused registration type");
    }
}
