using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

internal static class StartupNoTiaChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static MethodInfo ResolveWithoutInstallation = null!;

    internal static void Run(Assembly server, int major, Action<string> pass)
    {
        var engineering = server.GetType("TiaMcpServer.Siemens.Engineering", true)!;
        engineering.GetProperty("TiaMajorVersion", All)!.SetValue(null, major);
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
            var cli = server.GetType("TiaMcpServer.CliOptions", true)!;

            Build(server, cli, major, "stdio", false, pass);
            Build(server, cli, major, "http", false, pass);
            Build(server, cli, major, "stdio", true, pass);
            Build(server, cli, major, "http", true, pass);
        }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
    }

    private static Assembly? ResolveNoTia(object? sender, ResolveEventArgs args)
    {
        var requested = new AssemblyName(args.Name);
        if ((requested.Name ?? string.Empty).StartsWith("Siemens.Engineering", StringComparison.Ordinal))
            return (Assembly?)ResolveWithoutInstallation.Invoke(null, new object?[] { args, null });
        return null;
    }

    private static void Build(Assembly server, Type cli, int major, string transport, bool isolate, Action<string> pass)
    {
        string[] args = { "--transport", transport, "--profile", "full", "--tia-major-version", major.ToString(), "--logging", "0" };
        if (isolate) args = args.Concat(new[] { "--isolate-openness" }).ToArray();
        var options = cli.GetMethod("ParseArgs", All)!.Invoke(null, new object[] { args })!;
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
            var names = ToolNames(server);
            int expected = major == 20 ? 477 : 488;
            Check(names.Length == expected,
                "V" + major + " " + transport + (isolate ? " isolation parent" : " host")
                + " has " + names.Length + " tools; expected " + expected + ".");
            Check(names.Contains("InitializeEnvironment"), "InitializeEnvironment is absent from the no-TIA roster.");
            Check(!HasEngineeringAssembly(), "Host construction loaded a Siemens.Engineering assembly before a TIA call was admitted.");
            pass("V" + major + " " + transport + (isolate ? " isolation parent" : " host")
                + ": service provider and full " + expected + " tool pipeline built without TIA");
        }
        finally
        {
            (host as IDisposable)?.Dispose();
            requestStream?.Dispose();
            responseStream?.Dispose();
            if (isolate) isolation.GetMethod("Stop", All)!.Invoke(null, null);
        }
    }

    private static string[] ToolNames(Assembly engine)
    {
        var mcp = engine.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        var tools = (IEnumerable)mcp.GetMethod("GetAllTools", All)!.Invoke(null, null)!;
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
