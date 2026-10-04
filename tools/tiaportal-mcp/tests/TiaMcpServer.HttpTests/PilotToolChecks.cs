using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

internal static class PilotToolChecks
{
    internal static void Run(Assembly server, Action<bool, string> check)
    {
        var surface = EngineSurface.For(server);
        var domains = new Dictionary<string, string[]>
        {
            ["EcosystemTools"] = new[] { "RenderPlcVisualDiff", "ReadOpennessGuidance", "RunPlcCompanionTool" },
            ["V21EcosystemTools"] = new[] { "ReadV21EcosystemCatalog", "ValidatePlcXmlSchemas", "ManageUnifiedCwcPackage", "DecodePlcSimaticMl" },
            ["GitWorkflowTools"] = new[] { "ManagePlcGitRepository" },
            ["TemplateTools"] = new[] { "ComposePlcAliasAlarmLad", "InstantiatePlcXmlTemplates" },
            ["QualityAuditTools"] = new[] { "AuditEngineeringExports" },
            ["ImportOrderTools"] = new[] { "PlanArtifactImportOrder" },
            ["GuideTools"] = new[] { "GetAuthoringGuide" },
            ["ToolUsageTools"] = new[] { "GetToolUsage" },
            ["OfflineSuiteTools"] = new[]
            {
                "BuildClassicHmiTagTableXml", "BuildClassicHmiMinimalPackage", "WriteClassicHmiMinimalPackageFiles",
                "ValidateClassicHmiMinimalPackageFiles", "ValidateClassicHmiMinimalPackagePlcSync", "BuildPlcSymbolManifestFromXmlPath",
                "RunClassicHmiOfflineValidationSuite", "RunOfflineReleaseValidationSuite", "BuildReleaseDiagnosticReport",
                "BuildReleaseRunbook", "BuildReleaseManifest", "RebuildReleaseHandoffArtifacts", "RunClassicHmiTemporaryImportPreflight",
                "RunHmiTemplatePlcSyncPrecheckSuite", "BuildUnifiedHmiTemplateApplyDesignJson", "BuildUnifiedHmiTemplateApplyDesignManifest"
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
        check(resolved.GetType().GetProperty("Meta")!.GetValue(resolved)!.ToString()
            == direct.GetType().GetProperty("Meta")!.GetValue(direct)!.ToString(),
            "EngineServices and EngineSurface return the same tool usage result");
        var guide = surface.Invoke(surface.Tool("GetAuthoringGuide"), new object[] { "errors" })!;
        check(guide.GetType().GetProperty("Meta")!.GetValue(guide)!.ToString()
            == direct.GetType().GetProperty("Meta")!.GetValue(direct)!.ToString(),
            "Guide instance reaches its injected tool usage singleton");
    }
}
