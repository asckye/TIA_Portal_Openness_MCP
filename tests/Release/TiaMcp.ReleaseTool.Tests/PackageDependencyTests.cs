using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class PackageDependencyTests
{
    [Theory]
    [InlineData("1.0.0.0", "neutral", true)]
    [InlineData("2.0.0.0", "neutral", false)]
    [InlineData("1.0.0.0", "de", false)]
    public void GacExemptionRequiresTheReferencedVersionAndCulture(string version, string culture, bool expected)
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "External, Version=1.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"
        };
        var reference = new AssemblyName($"External, Version={version}, Culture={culture}, PublicKeyToken=b77a5c561934e089");
        Assert.Equal(expected, PackageDependencies.InstalledAssembly(reference, installed));
    }

    [Theory]
    [InlineData("Siemens.Engineering", "d29ec89bac048f84", true)]
    [InlineData("Siemens.Engineering.WinCC", "29bfe5fdf4ba5d3b", true)]
    [InlineData("Siemens.Engineering.Unrecognized", "29bfe5fdf4ba5d3b", false)]
    [InlineData("Siemens.Engineering.WinCC", "b77a5c561934e089", false)]
    public void OnlyDeclaredPublicApiIdentitiesAreInstallationPrerequisites(string name, string token, bool expected)
    {
        var reference = new AssemblyName($"{name}, Version=21.0.0.0, Culture=neutral, PublicKeyToken={token}");
        Assert.Equal(expected, PackageDependencies.InstalledAssembly(reference, new HashSet<string>()));
    }

    [Fact]
    public void EmbeddedWorkerDependenciesAreTraversedAndTheirMissingReferencesFail()
    {
        using var fixture = new OfflineFixtures();
        var dependency = AssemblyBytes("Embedded.dll", ["Transitive"], null);
        var program = "runtime/v19/worker/worker.exe";
        var path = Path.Combine(fixture.DirectoryPath, program);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, AssemblyBytes(program, ["Embedded"],
            new Dictionary<string, byte[]> { ["ProjectGroupRuntime.Embedded.dll"] = dependency }));
        Assert.Contains(PackageDependencies.Check(fixture.DirectoryPath, _ => true),
            error => error.Contains("Transitive", StringComparison.Ordinal));
        Assembly(fixture.DirectoryPath, "runtime/v19/worker/Transitive.dll");
        Assert.Empty(PackageDependencies.Check(fixture.DirectoryPath, _ => true));
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void CurrentRulesDeliverHostRuntimeAndPlantedExclusionFails(string key)
    {
        using var fixture = new OfflineFixtures();
        var host = $"runtime/v{key}/TiaMcp.FoundationHost.exe";
        var runtime = $"runtime/v{key}/TiaMcp.Runtime.dll";
        Assembly(fixture.DirectoryPath, host, "TiaMcp.Runtime");
        Assembly(fixture.DirectoryPath, runtime);
        var root = Repository.FindRoot(AppContext.BaseDirectory);
        var rules = DeliveryRules.Load(root);
        bool Delivered(string path) => rules.Delivered(path);
        Assert.True(Delivered(runtime));
        Assert.Empty(PackageDependencies.Check(fixture.DirectoryPath, Delivered));
        var errors = PackageDependencies.Check(fixture.DirectoryPath, path => path != runtime && Delivered(path));
        Assert.Contains(errors, error => error.Contains(host, StringComparison.Ordinal) && error.Contains("TiaMcp.Runtime", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("runtime/v14sp1/worker/TiaMcp.PlcWorker.14sp1.exe")]
    [InlineData("runtime/v20/worker/TiaMcp.Engine.V20.exe")]
    [InlineData("runtime/studio/TiaOpenness.exe")]
    [InlineData("runtime/studio/bridge/TiaOpenness.Bridge.exe")]
    [InlineData("runtime/tools/TiaMcp.Updater.exe")]
    [InlineData("runtime/tools/TiaMcp.WriteGuard.exe")]
    [InlineData("runtime/tools/TiaMcp.PluginLauncher.exe")]
    [InlineData("TiaOpenness.exe")]
    public void EveryProgramChecksTransitiveReferencesAtItsOwnProbePath(string program)
    {
        using var fixture = new OfflineFixtures();
        var directory = Path.GetDirectoryName(program)!;
        Assembly(fixture.DirectoryPath, program, "First");
        Assembly(fixture.DirectoryPath, Path.Combine(directory, "First.dll"), "Missing");
        Assembly(fixture.DirectoryPath, "runtime/unrelated/Missing.dll");
        Assert.Contains(PackageDependencies.Check(fixture.DirectoryPath, _ => true),
            error => error.Contains(program, StringComparison.Ordinal) && error.Contains("Missing", StringComparison.Ordinal));
        Assembly(fixture.DirectoryPath, Path.Combine(directory, "Missing.dll"));
        Assert.Empty(PackageDependencies.Check(fixture.DirectoryPath, _ => true));
    }

    [Fact]
    public void FrameworkReferenceIsNotRequiredButSystemPrefixAloneIsNotAnExemption()
    {
        using var fixture = new OfflineFixtures();
        Assembly(fixture.DirectoryPath, "runtime/tools/tool.exe", "mscorlib/b77a5c561934e089");
        Assert.Empty(PackageDependencies.Check(fixture.DirectoryPath, _ => true));
        Assembly(fixture.DirectoryPath, "runtime/tools/tool.exe", "System.NotAFrameworkAssembly/b77a5c561934e089");
        Assert.Contains(PackageDependencies.Check(fixture.DirectoryPath, _ => true),
            error => error.Contains("System.NotAFrameworkAssembly", StringComparison.Ordinal));
    }

    [Fact]
    public void DepsAssetsAndBundledFrameworkAreChecked()
    {
        using var fixture = new OfflineFixtures();
        Assembly(fixture.DirectoryPath, "runtime/v21/host.exe", "System.Runtime");
        Assembly(fixture.DirectoryPath, "runtime/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Runtime.dll", "OptionalFrameworkForwarder");
        var directory = Path.Combine(fixture.DirectoryPath, "runtime/v21");
        File.WriteAllText(Path.Combine(directory, "host.runtimeconfig.json"),
            """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
        File.WriteAllText(Path.Combine(directory, "host.deps.json"),
            """{"runtimeTarget":{"name":"net10"},"targets":{"net10":{"host/1":{"runtime":{"lib/net10/TiaMcp.Runtime.dll":{}}}}}}""");
        var errors = PackageDependencies.Check(fixture.DirectoryPath, _ => true);
        Assert.Contains(errors, error => error.Contains("TiaMcp.Runtime", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("missing assembly System.Runtime", StringComparison.Ordinal));
        Assembly(fixture.DirectoryPath, "runtime/v21/TiaMcp.Runtime.dll");
        Assert.Empty(PackageDependencies.Check(fixture.DirectoryPath, _ => true));
        Assert.Contains(PackageDependencies.Check(fixture.DirectoryPath, path => !path.StartsWith("runtime/dotnet/", StringComparison.Ordinal)),
            error => error.Contains("missing shared framework", StringComparison.Ordinal));
    }

    [Fact]
    public void ModernLoaderDoesNotUseFrameworkNameSubdirectoryProbing()
    {
        using var fixture = new OfflineFixtures();
        Assembly(fixture.DirectoryPath, "runtime/v21/host.exe", "Dependency");
        Assembly(fixture.DirectoryPath, "runtime/v21/Dependency/Dependency.dll");
        var directory = Path.Combine(fixture.DirectoryPath, "runtime/v21");
        File.WriteAllText(Path.Combine(directory, "host.runtimeconfig.json"), """{"runtimeOptions":{}}""");
        File.WriteAllText(Path.Combine(directory, "host.deps.json"),
            """{"runtimeTarget":{"name":"net10"},"targets":{"net10":{}}}""");
        Assert.Contains(PackageDependencies.Check(fixture.DirectoryPath, _ => true),
            error => error.Contains("missing assembly Dependency", StringComparison.Ordinal));
        Assembly(fixture.DirectoryPath, "runtime/v21/Dependency.dll");
        Assert.Empty(PackageDependencies.Check(fixture.DirectoryPath, _ => true));
    }

    [Fact]
    public void FrameworkPrivateProbingPathIsHonored()
    {
        using var fixture = new OfflineFixtures();
        Assembly(fixture.DirectoryPath, "runtime/tools/tool.exe", "Private");
        Assembly(fixture.DirectoryPath, "runtime/tools/lib/Private.dll");
        Assert.NotEmpty(PackageDependencies.Check(fixture.DirectoryPath, _ => true));
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "runtime/tools/tool.exe.config"),
            """<configuration><runtime><assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1"><probing privatePath="lib" /></assemblyBinding></runtime></configuration>""");
        Assert.Empty(PackageDependencies.Check(fixture.DirectoryPath, _ => true));
    }

    [Fact]
    public void DepsRuntimeTargetsAndSatelliteAssembliesUseTheirPublishedPaths()
    {
        using var fixture = new OfflineFixtures();
        Assembly(fixture.DirectoryPath, "runtime/v21/host.exe", "Platform");
        Assembly(fixture.DirectoryPath, "runtime/v21/runtimes/win-x64/lib/net10/Platform.dll");
        var deps = Path.Combine(fixture.DirectoryPath, "runtime/v21/host.deps.json");
        File.WriteAllText(deps,
            """{"runtimeTarget":{"name":"net10"},"targets":{"net10":{"host/1":{"runtimeTargets":{"runtimes/win-x64/lib/net10/Platform.dll":{"assetType":"runtime","rid":"win-x64"}},"resources":{"lib/net10/de/Host.resources.dll":{"locale":"de"}}}}}}""");
        Assert.Contains(PackageDependencies.Check(fixture.DirectoryPath, _ => true),
            error => error.Contains("Host.resources", StringComparison.Ordinal));
        Assembly(fixture.DirectoryPath, "runtime/v21/de/Host.resources.dll");
        Assert.Empty(PackageDependencies.Check(fixture.DirectoryPath, _ => true));
        Assert.NotEmpty(PackageDependencies.Check(fixture.DirectoryPath, path => !path.EndsWith("Platform.dll", StringComparison.Ordinal)));
    }

    [Fact]
    public void PluginLauncherDeliveryIncludesModernEntryAssemblyAndDependencyMetadata()
    {
        using var fixture = new OfflineFixtures();
        const string program = "runtime/tools/TiaMcp.PluginLauncher.exe";
        const string assembly = "runtime/tools/TiaMcp.PluginLauncher.dll";
        var rules = DeliveryRules.Load(Repository.FindRoot(AppContext.BaseDirectory));
        foreach (string suffix in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            string file = "runtime/tools/TiaMcp.PluginLauncher" + suffix;
            Assert.True(rules.Delivered(file));
            Assert.Contains(file, DeliveryRules.Strings(rules.Data["requiredFiles"]));
        }
        string directory = Path.Combine(fixture.DirectoryPath, "runtime/tools");
        Directory.CreateDirectory(directory);
        byte[] native = AssemblyBytes(program, [], null);
        using (var pe = new PEReader(new MemoryStream(native, writable: false)))
        {
            int directories = pe.PEHeaders.PEHeaderStartOffset + (pe.PEHeaders.PEHeader!.Magic == PEMagic.PE32Plus ? 112 : 96);
            Array.Clear(native, directories + 14 * 8, 8);
        }
        using (var pe = new PEReader(new MemoryStream(native, writable: false))) Assert.False(pe.HasMetadata);
        File.WriteAllBytes(Path.Combine(fixture.DirectoryPath, program), native);
        File.WriteAllText(Path.Combine(directory, "TiaMcp.PluginLauncher.runtimeconfig.json"), """{"runtimeOptions":{}}""");
        File.WriteAllText(Path.Combine(directory, "TiaMcp.PluginLauncher.deps.json"),
            """{"runtimeTarget":{"name":"net10"},"targets":{"net10":{}}}""");
        Assert.Contains(PackageDependencies.Check(fixture.DirectoryPath, rules.Delivered), error => error.Contains("missing entry assembly " + assembly, StringComparison.Ordinal));
        Assembly(fixture.DirectoryPath, assembly, "RequiredDependency");
        Assert.Contains(PackageDependencies.Check(fixture.DirectoryPath, rules.Delivered), error => error.Contains("RequiredDependency", StringComparison.Ordinal));
        Assembly(fixture.DirectoryPath, "runtime/tools/RequiredDependency.dll");
        Assert.Empty(PackageDependencies.Check(fixture.DirectoryPath, rules.Delivered));
    }

    private static void Assembly(string root, string relative, params string[] references)
    {
        var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, AssemblyBytes(relative, references, null));
    }

    private static byte[] AssemblyBytes(string relative, string[] references, Dictionary<string, byte[]>? embedded)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(Path.GetFileName(relative)), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(Path.GetFileNameWithoutExtension(relative)), new Version(1, 0, 0, 0),
            default, default, default, AssemblyHashAlgorithm.None);
        foreach (var reference in references)
        {
            var parts = reference.Split('/');
            metadata.AddAssemblyReference(metadata.GetOrAddString(parts[0]), new Version(4, 0, 0, 0), default,
                parts.Length > 1 ? metadata.GetOrAddBlob(Convert.FromHexString(parts[1])) : default, default, default);
        }
        var resources = new BlobBuilder();
        foreach (var (name, data) in embedded ?? [])
        {
            metadata.AddManifestResource(ManifestResourceAttributes.Private, metadata.GetOrAddString(name), default, (uint)resources.Count);
            resources.WriteInt32(data.Length); resources.WriteBytes(data);
        }
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(), new MetadataRootBuilder(metadata), new BlobBuilder(), managedResources: resources);
        var bytes = new BlobBuilder(); pe.Serialize(bytes);
        return bytes.ToArray();
    }
}
