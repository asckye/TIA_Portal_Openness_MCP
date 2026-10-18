using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace TiaMcp.ReleaseTool;

internal sealed record OpennessGroupRuntimeInventory(string EntryAssembly, string[] Sidecars, string[] References, string[] Resources)
{
    internal string[] Errors => Sidecars.Concat(References).Concat(Resources)
        .Where(name => name.Contains("Siemens.Collaboration.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ProjectGroupRuntime.", StringComparison.Ordinal))
        .Select(name => $"Openness group runtime: {EntryAssembly}: prohibited PlcWorker/host dependency or resource {name}").ToArray();

    internal void RequireClean()
    {
        if (Errors.Length != 0) throw new ReleaseException(string.Join(Environment.NewLine, Errors));
    }
}

internal static class OpennessGroupRuntime
{
    internal static readonly string[] CollaborationAssemblies =
    [
        "Siemens.Collaboration.Net.dll", "Siemens.Collaboration.Net.CoreExtensions.dll",
        "Siemens.Collaboration.Net.Logging.dll", "Siemens.Collaboration.Net.OperatingSystem.Windows.dll",
        "Siemens.Collaboration.Net.Windows.Authentication.dll", "Siemens.Collaboration.Net.TiaPortal.Openness.Resolver.dll"
    ];

    internal static IEnumerable<string> DeliveryPolicyErrors(Func<string, bool> delivered)
    {
        foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
            foreach (var assembly in CollaborationAssemblies)
            {
                var host = $"runtime/v{key}/{assembly}";
                if (delivered(host)) yield return "Delivery policy permits retired host group dependency: " + host;
                // V20/V21 worker directories still contain the retiring engine until P8-04l.
                var worker = $"runtime/v{key}/worker/{assembly}";
                if (key is not ("20" or "21") && delivered(worker))
                    yield return "Delivery policy permits retired PlcWorker group dependency: " + worker;
            }
    }

    internal static bool IsEntryAssembly(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("TiaMcp.FoundationHost.dll", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("TiaMcp.PlcWorker.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    internal static OpennessGroupRuntimeInventory Inspect(string entryAssembly)
    {
        // Read only our own entry assembly's metadata, never the Siemens.Collaboration binaries.
        if (!IsEntryAssembly(entryAssembly)) throw new ArgumentException("Expected a PlcWorker or FoundationHost entry assembly.", nameof(entryAssembly));
        var sidecars = Directory.EnumerateFiles(Path.GetDirectoryName(entryAssembly)!)
            .Select(Path.GetFileName).OfType<string>()
            .Where(name => name.StartsWith("Siemens.Collaboration.", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
        using var stream = File.OpenRead(entryAssembly);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return new(entryAssembly, sidecars,
            metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).Order(StringComparer.Ordinal).ToArray(),
            metadata.ManifestResources.Select(handle => metadata.GetString(metadata.GetManifestResource(handle).Name)).Order(StringComparer.Ordinal).ToArray());
    }
}
