using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

// The net48 harness cannot load the net10 host. Exercise its published process
// through the same transport client used by release validation instead.
internal static class FoundationHostChecks
{
    private static string Root => HostPortRunner.RepositoryRoot();
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    internal static int Run(Assembly engine, bool stdioOnly = false)
    {
        string release = engine.GetReferencedAssemblies().First(a => a.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)).Version!.Major.ToString();
        string output = Path.Combine(Root, "bin-build", "foundation-host-check-" + Guid.NewGuid().ToString("N"));
        string fixtures = Path.Combine(output, "workers"), directory = Path.Combine(fixtures, release);
        Directory.CreateDirectory(directory);
        foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(engine.Location)!))
            if (new[] { ".exe", ".dll", ".config" }.Contains(Path.GetExtension(path)) && !Path.GetFileName(path).StartsWith("Siemens.Engineering", StringComparison.Ordinal))
                File.Copy(path, Path.Combine(directory, Path.GetFileName(path)));
        engine.GetType("TiaMcpServer.Cli.ToolCatalogExport", true)!.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { Path.Combine(directory, "tool-catalog.json") });
        string host = Environment.GetEnvironmentVariable("TIA_MCP_TEST_HOST_PATH")
            ?? Path.Combine(Root, "src", "FoundationHost", "bin", "Release", "net10.0", "TiaMcp.FoundationHost.exe");
        return Process(Environment.GetEnvironmentVariable("TIA_MCP_TEST_PYTHON") ?? "python",
            Quote(Path.Combine(Root, "scripts", "checks", "Test-FoundationTransport.py")) + " --releases " + release
            + " --host-exe " + Quote(host) + " --sdk-fixture-root " + Quote(fixtures)
            + " --output " + Quote(output) + " --temp-root " + Quote(Path.Combine(output, "transport-temp"))
            + (stdioOnly ? " --stdio-only" : ""));
    }

    internal static int Suites(string suite) => Process("dotnet", "run --project " + Quote(Path.Combine(Root, "build-tools", "release"))
        + " -c Release --no-build -- test-suites -Suite " + suite
        + " -DotnetArg=--disable-build-servers -DotnetArg=-m:1 -DotnetArg=-nodeReuse:false"
        + " -DotnetArg=-p:UseSharedCompilation=false -DotnetArg=-p:NuGetAudit=false");

    private static int Process(string executable, string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new ProcessStartInfo(executable, arguments) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Root,
            RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start Foundation host checks.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Console.Write(output.GetAwaiter().GetResult());
        Console.Error.Write(errors.GetAwaiter().GetResult());
        return process.ExitCode;
    }
}
