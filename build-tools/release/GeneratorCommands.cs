using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    internal static int GeneratorCommand(string command, Options options)
    {
        if (options.Has("SelfTest"))
        {
            var result = SuiteHang.RunProcess(Dotnet, AddOfflineNuGetConfig(Dotnet, ["test", "tests/Release/TiaMcp.ReleaseTool.Tests/TiaMcp.ReleaseTool.Tests.csproj", "-c", "Release", .. options.Has("NoBuild") ? new[] { "--no-build", "--no-restore" } : [], "-p:UseAppHost=false", "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false", "--filter", "Category=Generators"]), Root);
            Console.Write(result.StandardOutput); Console.Error.Write(result.StandardError); return result.ExitCode;
        }
        var root = Path.GetFullPath(options.Get("Root", Root)); Dictionary<string, string> outputs;
        switch (command)
        {
            case "generate-catalogs": outputs = CatalogGenerator.Generate(root, prepareSources: options.Has("Check") ? null : prepared => CatalogJson.Write(root, prepared, false)); break;
            case "generate-tool-counts": outputs = ToolCountGenerator.Generate(root); break;
            case "generate-tool-usage": outputs = new() { ["src/Shared/ToolUsageData.json"] = CatalogJson.Encode(ToolUsageGenerator.Generate(root)) }; break;
            case "generate-release-migration": outputs = new() { [ReleaseMigrationGenerator.Output] = ReleaseMigrationGenerator.Render(root) }; break;
            case "audit-version-tools":
                // Catalogs have one owner. Validate them before computing audit evidence.
                if (CatalogJson.Write(root, CatalogGenerator.VersionOutputs(new CatalogData(root)), true) != 0) return 1;
                var api = options.Get("PublicApiRoot") ?? throw new ReleaseException("-PublicApiRoot is required", 64);
                outputs = CatalogAudits.Version(root, Path.GetFullPath(api), options.Get("Output", "bin-build/multi-version/api-audit"), options.Get("InputsRoot")); break;
            case "audit-tool-usage": outputs = new() { ["manifest/tool-usage-coverage.json"] = CatalogJson.Encode(CatalogAudits.Usage(root, options.Get("InputsRoot"), options.Get("Records"))) }; break;
            case "check-tools-list": return ToolsListChecks.Run(root, options.Get("Manifest", "manifest/tools-list.json"));
            case "generate-prerelease-notes": outputs = PrereleaseNotes.Outputs(root, options.Get("Version") ?? throw new ReleaseException("-Version is required", 64)); break;
            default: throw new ReleaseException("Unknown generator command", 64);
        }
        var code = CatalogJson.Write(root, outputs, options.Has("Check"));
        Console.WriteLine($"{command}: {(code == 0 ? options.Has("Check") ? "checked" : "generated" : "stale")}; {outputs.Count} outputs; native TIA NOT RUN");
        return code;
    }
}
