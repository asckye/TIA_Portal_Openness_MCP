namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    internal static string ReleaseCompanionPython(Options options)
    {
        var executable = options.Get("EcosystemPython") ?? options.Get("CompanionPython")
            ?? Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON");
        if (string.IsNullOrWhiteSpace(executable))
        {
            var roots = new[] { Root, options.Get("PublicApiRoot") is { } api ? Path.GetDirectoryName(Path.GetFullPath(api))! : Root };
            executable = roots.Select(root => Path.Combine(root, "TiaMcp_Output/ecosystem-python/Scripts/python.exe")).FirstOrDefault(File.Exists)
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMcp/ecosystem-python/Scripts/python.exe");
        }
        try { return ResolveApplication(executable); }
        catch (ReleaseException error)
        {
            throw new ReleaseException($"Ecosystem/companion Python unavailable at '{executable}': {error.Message}. " +
                "Prepare the Python 3.12+ environment with 'tia install-plc-tools', then pass its python.exe with -EcosystemPython / -CompanionPython " +
                "or set TIA_MCP_PLC_TOOLS_PYTHON; no build started.");
        }
    }
}
