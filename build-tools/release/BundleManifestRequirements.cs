namespace TiaMcp.ReleaseTool;

internal static class BundleManifestRequirements
{
    internal static readonly string[] BundleResourcePaths =
    [
        "manifest/package-manifest.json",
        "manifest/delivery.json",
        "reference/siemens-openness/skills",
        "reference/siemens-openness/UPSTREAM.json",
        "reference/v21-ecosystem.json",
        "scripts/ecosystem/plc_tools_bridge.py",
        "scripts/ecosystem/simaticml_decode_bridge.py",
        "runtime/tools/TiaMcp.WriteGuard.exe",
        "runtime/tools/TiaMcp.Updater.exe",
        "templates"
    ];

    private static readonly HashSet<string> GeneratedBundleResources = new(StringComparer.Ordinal)
    {
        "runtime/tools/TiaMcp.WriteGuard.exe",
        "runtime/tools/TiaMcp.Updater.exe"
    };

    internal static readonly string[] GuiRequiredPaths =
    [
        "src/Shared/BundleLayout.cs", "scripts/checks/Check-BundleLayout.py",
        "tests/Engine/TiaMcpServer.Tests/BundleLayoutTests.cs", "src/Adapters.Contracts/TiaMcp.Adapters.Contracts.csproj",
        "src/Adapters.Contracts/packages.lock.json", "src/Adapters/Native/Plc/PlcServices.cs",
        "src/Engine/ModelContextProtocol/InvocationJournal.Adapter.cs", "tests/Engine/TiaMcpServer.HttpTests/AdapterIntegrationChecks.cs",
        "TiaOpenness.exe", "docs/getting-started/configuration.md",
        "src/Studio/Launcher/Launcher.cs", "src/Studio/Gui/Themes/Primer.xaml",
        "src/Studio/Gui/Themes/Palette.Light.xaml", "src/Studio/Gui/Themes/Palette.Dark.xaml",
        "src/Studio/Gui/Controls/WorkbenchLogView.cs", "src/Studio/Gui/Controls/WorkbenchMessageBox.cs",
        "src/Studio/Gui/Controls/ResultPresentation.cs", "src/Studio/Gui/Controls/LogTailView.cs",
        "src/Studio/Gui/Fonts/JetBrainsMono-Regular.ttf",
        "src/Studio/Gui/Fonts/JetBrainsMono-Medium.ttf", "src/Studio/Gui/Fonts/JetBrainsMono-OFL.txt",
        "src/Studio/Gui/Fonts/NotoSansSC-Regular.otf", "src/Studio/Gui/Fonts/NotoSansSC-Bold.otf",
        "src/Studio/Gui/Fonts/NotoSansSC-OFL.txt", "src/Studio/Gui/Fonts/SOURCES.txt",
        "src/Studio/Gui/TiaOpenness.Gui.csproj", "src/Studio/Gui/Configuration/ConfigurationView.xaml",
        "src/Studio/Gui/Configuration/ConfigurationView.xaml.cs", "src/Studio/Gui/Configuration/ConfigCore.cs",
        "src/Studio/Gui/Configuration/ClientProfiles.cs", "src/Studio/Gui/Configuration/UpdateCheck.cs",
        "src/Studio/Gui/Configuration/ModernJson.cs", "src/Studio/Gui/Localization/Strings.cs",
        "src/Studio/Gui/Themes/Palette.Light.xaml", "src/Studio/Gui/Themes/Palette.Dark.xaml",
        "tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj",
        "tests/Studio/TiaOpenness.Configuration.Tests/Tests.cs"
    ];

    internal static IReadOnlyList<string> MissingBundleResources(string root, bool package, bool noBinaries) => BundleResourcePaths
        .Where(resource => !File.Exists(Path.Combine(root, resource.Replace('/', Path.DirectorySeparatorChar))) &&
                           !Directory.Exists(Path.Combine(root, resource.Replace('/', Path.DirectorySeparatorChar))) &&
                           !HasGeneratedBundleResource(root, resource, package, noBinaries))
        .ToArray();

    private static bool HasGeneratedBundleResource(string root, string resource, bool package, bool noBinaries)
    {
        if (!GeneratedBundleResources.Contains(resource) || package) return false;
        if (noBinaries) return true;
        var buildOutput = resource switch
        {
            "runtime/tools/TiaMcp.WriteGuard.exe" => "src/Tools/WriteGuard/bin/Release/net10.0/TiaMcp.WriteGuard.exe",
            "runtime/tools/TiaMcp.Updater.exe" => "bin-build/updater/TiaMcp.Updater.exe",
            _ => ""
        };
        return buildOutput.Length != 0 && File.Exists(Path.Combine(root, buildOutput.Replace('/', Path.DirectorySeparatorChar)));
    }
}
