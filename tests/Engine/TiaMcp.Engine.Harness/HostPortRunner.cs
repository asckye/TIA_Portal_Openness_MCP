using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

// Keep the historical harness scenarios against the net10 implementations.
internal static class HostPortRunner
{
    internal static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "CLAUDE.md"))) root = root.Parent;
        return root?.FullName ?? throw new DirectoryNotFoundException("Harness requires a repository root.");
    }
    internal static JsonObject Call(Assembly server, string name, JsonObject arguments)
        => Run(server, "CallTool", new JsonObject { ["name"] = name, ["arguments"] = arguments });
    private static readonly System.Collections.Generic.Dictionary<Assembly, JsonObject> metadata = new System.Collections.Generic.Dictionary<Assembly, JsonObject>();
    internal static JsonObject Metadata(Assembly server, string name)
    {
        if (!metadata.TryGetValue(server, out var value)) metadata.Add(server, value = Run(server, "ToolMetadata", new JsonObject()));
        return value[name]!.AsObject();
    }

    private static JsonObject Run(Assembly server, string command, JsonObject arguments)
    {
        string root = RepositoryRoot();
        string host = Environment.GetEnvironmentVariable("TIA_MCP_TEST_HOST_PATH")
            ?? Path.Combine(root, "src", "FoundationHost", "bin", "Release", "net10.0", "TiaMcp.FoundationHost.exe");
        string release = server.GetReferencedAssemblies().First(a => a.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)).Version!.Major.ToString();
        string catalog = Path.Combine(Path.GetDirectoryName(server.Location)!, "tool-catalog.json");
        // Always rewrite: a catalog left by an earlier build carries another worker hash and the host refuses it.
        server.GetType("TiaMcpServer.Cli.ToolCatalogExport", true)!.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { catalog });
        string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
        var start = new ProcessStartInfo(host, "--bundle-root " + Quote(root) + " --release-key " + release
            + " --engine-worker " + Quote(server.Location) + " --engine-catalog " + Quote(catalog) + " --host-command " + command) {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the host.");
        var errors = child.StandardError.ReadToEndAsync();
        child.StandardInput.WriteLine(arguments.ToJsonString()); child.StandardInput.Close();
        string output = child.StandardOutput.ReadToEnd(); child.WaitForExit();
        if (child.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
        return JsonNode.Parse(output)!.AsObject();
    }
}
