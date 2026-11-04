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
    // FoundationHost honors the retired engine options only through the repository's
    // private cross-check route (src/FoundationHost/HostOptions.cs) until P8-04l.
    internal const string EngineCrossCheckVariable = "TIA_MCP_ENGINE_CROSS_CHECK_DIRECTORY";
    internal static string CompatibilityRoot(string root) => Path.Combine(root, "bin-build", "engine-compat");
    private static readonly System.Collections.Generic.Dictionary<string, string> privateWorkers =
        new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Generic.Dictionary<string, string> privateHosts =
        new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // The private route accepts the development host only from the checkout's source build or the private output.
    // A pipeline's own copy of the development host (TIA_MCP_TEST_HOST_PATH, under the release TEMP; see
    // BuildReleasePipeline.CopyDevelopmentHost) is copied unchanged into the private output, once per process.
    internal static string PrivateHost(string host)
    {
        string root = RepositoryRoot(), compatibility = CompatibilityRoot(root), full = Path.GetFullPath(host);
        string directory = Path.GetDirectoryName(full)!;
        if (full.StartsWith(compatibility + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            new[] { "Release", "Debug" }.Any(configuration => string.Equals(directory,
                Path.Combine(root, "src", "FoundationHost", "bin", configuration, "net10.0"), StringComparison.OrdinalIgnoreCase)))
            return full;
        lock (privateHosts)
        {
            if (privateHosts.TryGetValue(full, out var staged)) return staged;
            // Keyed by the copy's location and its host assembly, so two builds never share a directory.
            string assembly = Path.Combine(directory, Path.GetFileNameWithoutExtension(full) + ".dll");
            string key = Sha256Text(directory.ToUpperInvariant() + "|" + (File.Exists(assembly) ? Sha256(assembly) : "")).Substring(0, 16);
            string target = Path.Combine(compatibility, "harness-host", key);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                string copy = Path.Combine(target, file.Substring(directory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                if (!File.Exists(copy) || new FileInfo(copy).Length != new FileInfo(file).Length || Sha256(copy) != Sha256(file))
                    File.Copy(file, copy, true);
            }
            staged = Path.Combine(target, Path.GetFileName(full));
            if (Sha256(staged) != Sha256(full) || (File.Exists(assembly) && Sha256(Path.Combine(target, Path.GetFileName(assembly))) != Sha256(assembly)))
                throw new InvalidOperationException("Private host copy differs from its source: " + staged);
            privateHosts.Add(full, staged);
            return staged;
        }
    }

    private static string Sha256Text(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }

    // The release, branch and check-all jobs already run engines from the private output.
    // Any other engine build is copied there unchanged, once per process.
    internal static string PrivateWorker(Assembly server)
    {
        string compatibility = CompatibilityRoot(RepositoryRoot()), worker = Path.GetFullPath(server.Location);
        if (worker.StartsWith(compatibility + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return worker;
        lock (privateWorkers)
        {
            if (privateWorkers.TryGetValue(worker, out var staged)) return staged;
            string release = server.GetReferencedAssemblies().First(a => a.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)).Version!.Major.ToString();
            // A short hash prefix keeps the copied dependency paths well inside MAX_PATH.
            string directory = Path.Combine(compatibility, "harness", "v" + release, Sha256(worker).Substring(0, 16));
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(worker)!))
            {
                string target = Path.Combine(directory, Path.GetFileName(file));
                if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(file).Length || Sha256(target) != Sha256(file))
                    File.Copy(file, target, true);
            }
            staged = Path.Combine(directory, Path.GetFileName(worker));
            if (Sha256(staged) != Sha256(worker)) throw new InvalidOperationException("Private engine copy differs from its build: " + staged);
            privateWorkers.Add(worker, staged);
            return staged;
        }
    }

    private static string Sha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
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
        if (PlcHarnessChecks.Active) return PlcHarnessChecks.HostCall(command, arguments);
        string root = RepositoryRoot();
        string host = PrivateHost(Environment.GetEnvironmentVariable("TIA_MCP_TEST_HOST_PATH")
            ?? Path.Combine(root, "src", "FoundationHost", "bin", "Release", "net10.0", "TiaMcp.FoundationHost.exe"));
        string release = server.GetReferencedAssemblies().First(a => a.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)).Version!.Major.ToString();
        string worker = PrivateWorker(server);
        string catalog = Path.Combine(Path.GetDirectoryName(worker)!, "tool-catalog.json");
        // Always rewrite: a catalog left by an earlier build carries another worker hash and the host refuses it.
        server.GetType("TiaMcpServer.Cli.ToolCatalogExport", true)!.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { catalog });
        string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
        var start = new ProcessStartInfo(host, "--bundle-root " + Quote(root) + " --release-key " + release
            + " --engine-worker " + Quote(worker) + " --engine-catalog " + Quote(catalog) + " --host-command " + command) {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.EnvironmentVariables[EngineCrossCheckVariable] = CompatibilityRoot(root);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the host.");
        var errors = child.StandardError.ReadToEndAsync();
        child.StandardInput.WriteLine(arguments.ToJsonString()); child.StandardInput.Close();
        string output = child.StandardOutput.ReadToEnd(); child.WaitForExit();
        if (child.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
        return JsonNode.Parse(output)!.AsObject();
    }
}
