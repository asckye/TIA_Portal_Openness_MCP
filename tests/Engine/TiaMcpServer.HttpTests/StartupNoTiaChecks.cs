using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

internal static class StartupNoTiaChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static MethodInfo ResolveWithoutInstallation = null!;

    internal static void Run(Assembly server, int major, Action<string> pass)
    {
        var engineering = Program.FindReferencedType(server, "TiaMcpServer.Siemens.Engineering");
        engineering.GetProperty("TiaMajorVersion", All)!.SetValue(null, major);
        server.GetType("TiaMcpServer.Runtime.OpennessReadiness", true)!.GetMethod("MarkUnavailable", All)!
            .Invoke(null, new object?[] { "No TIA installation for startup policy test.", "Install TIA with Openness.", "Install TIA with Openness.", null });
        ResolveWithoutInstallation = engineering.GetMethod("ResolveFromInstallPath", All)!;
        var noTiaArgs = new ResolveEventArgs("Siemens.Engineering, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        Check(ResolveWithoutInstallation.Invoke(null, new object?[] { noTiaArgs, null }) == null,
            "The installation resolver did not report an absent TIA installation as unavailable.");
        ResolveEventHandler resolver = ResolveNoTia;
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        try
        {
            var mcp = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
            mcp.GetMethod("SetProfileOverride", All)!.Invoke(null, new object[] { "full" });
            VerifyReadinessPolicy(server, mcp);
            var cli = Program.FindReferencedType(server, "TiaMcpServer.CliOptions");
            foreach (string profile in new[] { "full", "lite" })
            {
                Build(server, cli, major, profile, "stdio", false, pass);
                Build(server, cli, major, profile, "http", false, pass);
                Build(server, cli, major, profile, "stdio", true, pass);
                Build(server, cli, major, profile, "http", true, pass);
            }
        }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
    }

    private static void VerifyReadinessPolicy(Assembly server, Type mcp)
    {
        var guard = server.GetType("TiaMcpServer.Isolation.OpennessReadinessGuard", true)!;
        var safeMethod = guard.GetMethod("IsSafeWithoutTia", All)!;
        bool Safe(string name) => (bool)safeMethod.Invoke(null, new object[] { name })!;
        var explicitSafe = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "InitializeEnvironment", "GetEnvironmentDiagnostics", "GetOpennessWorkerStatus", "GetNativeInvocationLog",
            "GetOpennessCompatibility", "InspectSimaticSdCompatibility", "FindTools",
            "ListToolCategories", "GetToolUsage", "PreviewToolCall", "GetOpennessGuidance", "GetV21EcosystemCatalog",
            "PlanArtifactImportOrder", "BuildClassicHmiMinimalPackage", "BuildClassicHmiScreen",
            "BuildClassicHmiTagTable", "BuildFlgNetCall", "BuildPlcAliasAlarmLad",
            "BuildPlcFbBlock", "BuildPlcFcBlock", "BuildPlcGlobalDb", "BuildPlcLadFcBlock", "BuildPlcSymbolManifestFromPath",
            "BuildPlcTagTable", "BuildPlcUdt", "BuildReleaseDiagnosticReport", "BuildReleaseManifest", "BuildReleaseRunbook",
            "BuildStructuredText", "DecodePlcSimaticMl", "ValidatePlcDocumentSchemas",
            "StageImportFiles", "ListStagedImportFiles", "CleanupStagedImportFiles",
            "RenderPlcBlock", "RenderPlcProgramAtlas", "RenderPlcVisualDiff",
            "ScanPlcSourceAnnotations", "GetExportContent", "ListExportHandles",
            "SaveExportContent", "DeleteExportHandle", "ClearExportHandles"
        };
        foreach (string profile in new[] { "full", "lite" })
        {
            mcp.GetMethod("SetProfileOverride", All)!.Invoke(null, new object[] { profile });
            var names = ProfileToolNames(mcp, profile);
            var roster = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var seenSafe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                bool expected = explicitSafe.Contains(name);
                Check(Safe(name) == expected,
                    "Unexpected no-TIA allowlist decision for " + name + " in the " + profile + " profile.");
                if (expected) seenSafe.Add(name);
            }
            var expectedSafe = new HashSet<string>(explicitSafe.Where(roster.Contains), StringComparer.OrdinalIgnoreCase);
            Check(seenSafe.SetEquals(expectedSafe), "The no-TIA allowlist does not match the registered " + profile
                + " roster; missing safe tools: " + string.Join(", ", expectedSafe.Except(seenSafe).OrderBy(name => name)) + ".");
            if (profile == "full")
            {
                var missingSafe = explicitSafe.Except(roster, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
                Check(missingSafe.Length == 0, "The no-TIA allowlist contains unregistered full-profile tools: "
                    + string.Join(", ", missingSafe) + ".");
            }
        }
        foreach (string name in new[] { "GetSessionState", "EnsureOpennessUserGroup", "RunCapabilitySelfTest",
            "RunOnlineMonitoringSafetySelfTest", "RestartOpennessWorker", "SaveProject", "ConnectPortal", "unknown-tool" })
            Check(!Safe(name), name + " must be refused before dispatch while Openness is unavailable.");
        mcp.GetMethod("SetProfileOverride", All)!.Invoke(null, new object[] { "full" });
    }

    private static Assembly? ResolveNoTia(object? sender, ResolveEventArgs args)
    {
        var requested = new AssemblyName(args.Name);
        if ((requested.Name ?? string.Empty).StartsWith("Siemens.Engineering", StringComparison.Ordinal))
            return (Assembly?)ResolveWithoutInstallation.Invoke(null, new object?[] { args, null });
        return null;
    }

    private static void Build(Assembly server, Type cli, int major, string profile, string transport, bool isolate, Action<string> pass)
    {
        string[] args = { "--transport", transport, "--profile", profile, "--tia-major-version", major.ToString(), "--logging", "0" };
        if (isolate) args = args.Concat(new[] { "--isolate-openness" }).ToArray();
        var options = cli.GetMethod("ParseArgs", All)!.Invoke(null, new object[] { args })!;
        var mcp = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        mcp.GetMethod("SetProfileOverride", All)!.Invoke(null, new object[] { profile });
        var isolation = server.GetType("TiaMcpServer.Isolation.IsolatedWorkerHost", true)!;
        if (isolate)
        {
            Func<ProcessStartInfo> neverStarted = () => new ProcessStartInfo("no-tia-test-worker.exe");
            isolation.GetMethod("Configure", All)!.Invoke(null, new object?[] { options, neverStarted });
        }

        object? host = null;
        IDisposable? requestStream = null;
        IDisposable? responseStream = null;
        try
        {
            var program = server.GetType("TiaMcpServer.Program", true)!;
            if (transport == "stdio")
                host = program.GetMethod("BuildStdioHost", All)!.Invoke(null, new[] { options });
            else
            {
                var streamType = server.GetType("TiaMcpServer.McpBlockingStream", true)!;
                requestStream = (IDisposable)Activator.CreateInstance(streamType, true)!;
                responseStream = (IDisposable)Activator.CreateInstance(streamType, true)!;
                host = program.GetMethod("BuildHttpMcpHost", All)!.Invoke(null,
                    new object?[] { options, requestStream, responseStream });
                var health = server.GetType("TiaMcpServer.HttpMcpServer", true)!
                    .GetMethod("BuildHealthJson", All)!.Invoke(null, null)!.ToString()!;
                Check(health.Contains("\"transport\":\"http\""), "HTTP health endpoint pipeline did not build.");
            }

            Check(host != null && host.GetType().GetProperty("Services")!.GetValue(host) != null,
                transport + " host did not build its service provider.");
            if (profile == "full" && transport == "stdio" && !isolate)
            {
                var serviceType = server.GetType("TiaMcpServer.ModelContextProtocol.SessionTools", true)!;
                var serviceProvider = (IServiceProvider)host!.GetType().GetProperty("Services")!.GetValue(host)!;
                var sessionTools = serviceProvider.GetService(serviceType);
                Check(sessionTools != null, "The no-TIA SessionTools registration factory did not resolve its session-contract target.");
                var bootstrapTask = (Task)serviceType.GetMethod("InitializeEnvironmentV4", All)!.Invoke(sessionTools, null)!;
                bootstrapTask.GetAwaiter().GetResult();
                var bootstrap = bootstrapTask.GetType().GetProperty("Result")!.GetValue(bootstrapTask)!;
                Check(bootstrap.GetType().GetProperty("StructuredContent")!.GetValue(bootstrap) != null,
                    "The no-TIA SessionTools factory did not produce a structured bootstrap result.");
                pass("V" + major + " " + profile + " no-TIA SessionTools factory resolved and returned bootstrap data");
            }
            var names = ProfileToolNames(server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!, profile);
            int fullExpected = Program.ExpectedFullToolCount(major);
            Check(profile == "full" ? names.Length == fullExpected : names.Length > 0 && names.Length < fullExpected,
                "V" + major + " " + transport + (isolate ? " isolation parent" : " host")
                + " has " + names.Length + " " + profile + " tools.");
            Check(names.Contains("InitializeEnvironment"), "InitializeEnvironment is absent from the no-TIA roster.");
            Check(!HasEngineeringAssembly(), "Host construction loaded a Siemens.Engineering assembly before a TIA call was admitted.");
            pass("V" + major + " " + transport + (isolate ? " isolation parent" : " host")
                + ": service provider and " + profile + " " + names.Length + "-tool pipeline built without TIA");
        }
        finally
        {
            (host as IDisposable)?.Dispose();
            requestStream?.Dispose();
            responseStream?.Dispose();
            if (isolate) isolation.GetMethod("Stop", All)!.Invoke(null, null);
        }
    }

    private static string[] ProfileToolNames(Type mcp, string profile)
    {
        string method = profile == "lite" ? "GetLiteTools" : "GetAllTools";
        var tools = (IEnumerable)mcp.GetMethod(method, All)!.Invoke(null, null)!;
        return tools.Cast<object>().Select(tool =>
        {
            var protocolTool = tool.GetType().GetProperty("ProtocolTool")!.GetValue(tool)!;
            return (string)protocolTool.GetType().GetProperty("Name")!.GetValue(protocolTool)!;
        }).ToArray();
    }

    private static bool HasEngineeringAssembly()
        => AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            (assembly.GetName().Name ?? string.Empty).StartsWith("Siemens.Engineering", StringComparison.Ordinal));

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
