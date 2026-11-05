using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.WorkerChannel;

namespace TiaMcp.ReleaseTool;

internal sealed class PrepareBuild
{
    private readonly PrepareContext context;
    private readonly IDictionary<string, string?> environment;
    private readonly string[] properties;
    private readonly string dotnet = Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet";

    internal PrepareBuild(PrepareContext context, IDictionary<string, string?> environment)
    {
        this.context = context;
        this.environment = environment;
        var revision = ProcessRunner.Run("git", ["rev-parse", "HEAD"], context.Root);
        ProcessRunner.RequireSuccess(revision, "Read prepared source revision");
        var nuget = context.NuGetConfig ?? Path.Combine(context.OutputDirectory, "offline.nuget.config");
        properties = ["-p:TiaSecurityAnalysisProfile=release", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:RestoreLockedMode=true",
            "-p:TiaSharedAdapterPaths=false", "-p:TiaPublicApiRoot=" + context.PublicApiRoot,
            "-p:SourceRevisionId=" + revision.StandardOutput.Trim(),
            "-p:Version=" + context.Version, "-p:FileVersion=" + ReleaseVersions.FileVersionOf(context.Version),
            "-p:RestoreConfigFile=" + nuget];
    }

    internal void Run()
    {
        foreach (var name in new[] { "logs", "analyzers" }) Directory.CreateDirectory(Path.Combine(context.OutputDirectory, name));
        if (context.NuGetConfig == null)
            File.WriteAllText(environment["RestoreConfigFile"]!, "<configuration><packageSources><clear /></packageSources></configuration>\n", new UTF8Encoding(false));
        Prepare.Timed("runtime-clean", () => CleanRuntime(context.Root));
        Prepare.Timed("decoder-python", () => ReleaseCommands.PrepareDecoderPython(
            Options.Parse(["-Python", context.Python], new HashSet<string>()), context.Root));
        Prepare.Timed("shared-projects", () =>
        {
            foreach (var project in SharedProjects)
                Build(project, "shared-" + Path.GetFileNameWithoutExtension(project));
            CopyTree(Path.Combine(context.Root, "build-tools/native-call-weaver/bin/Release/net10.0"), Path.Combine(context.Root, "runtime/verification"));
        });
        Prepare.Timed("bundled-dotnet", BundledDotnet);
        Prepare.Timed("eight-workers-and-adapters", () =>
        {
            var table = VersionProjects(context).ToArray();
            // Restore is serial: TiaReleaseKey flows through references which share
            // obj directories. Compile shared projects once above, then never write
            // them from a per-version build. Adapters precede their worker consumers.
            foreach (var project in table) Restore(project);
            BuildTable("adapters", table.Where(row => row.Kind == "Adapter"));
            BuildTable("workers", table.Where(row => row.Kind == "Worker"));
            foreach (var key in PlcWorkerFixtures.Keys)
                VerifyAdapter(Path.Combine(context.Root, $"src/PlcWorker/bin/{key}/Release/net48/TiaMcp.Adapter.{key}.dll"), "copied-adapter-" + key);
            BuildTable("studio-adapters", table.Where(row => row.Kind == "Studio"));
        });
        Prepare.Timed("host", Host);
        Prepare.Timed("studio", Studio);
        Prepare.Timed("tools-and-updater", () =>
        {
            var output = Path.Combine(context.Root, "runtime/tools");
            Publish("src/Tools/WriteGuard/TiaMcp.WriteGuard.csproj", output, "write-guard", "-p:PublishAot=false", "-p:UseAppHost=true");
            Publish("src/Tools/PluginLauncher/TiaMcp.PluginLauncher.csproj", output, "plugin-launcher", "-p:UseAppHost=true");
            Build("src/Updater/TiaMcp.Updater.csproj", "updater");
            foreach (var (relative, source) in PrepareBuild.UpdaterOutputs)
                File.Copy(Path.Combine(context.Root, source), Path.Combine(context.Root, relative), true);
            Launcher();
        });
        Prepare.Timed("test-producers", TestProducers);
        Prepare.Timed("runtime-public-api", () => RequireNoPublicApi(context.Root));
        Prepare.Timed("analyzer-ratchet", () =>
        {
            var result = AnalyzerWarnings.Run(context.Root, Options.Parse(["-Check", "-DiagnosticsDirectory",
                environment["TIA_MCP_ANALYZER_REPORT_DIRECTORY"]!], new HashSet<string> { "Check" }));
            if (result != 0) throw new ReleaseException("Prepared build has analyzer ratchet violations");
        });
    }

    internal static readonly string[] SharedProjects =
    [
        "build-tools/native-call-weaver/NativeCallWeaver.csproj",
        "src/Adapters.Contracts/TiaMcp.Adapters.Contracts.csproj",
        "src/WorkerChannel/TiaMcp.WorkerChannel.csproj",
        "src/OnlineChannels/Simulation/TiaMcp.PlcSimAdvanced.csproj",
        "src/Studio/Core/TiaOpenness.Core.csproj"
    ];

    internal static string AdapterFolder(string key) => key switch { "14sp1" => "V14Sp1", "15.1" => "V15_1", _ => "V" + key };
    internal sealed record Project(string Kind, string Path, string Properties);

    internal static IEnumerable<Project> VersionProjects(PrepareContext context)
    {
        foreach (var key in PlcWorkerFixtures.Keys)
        {
            var folder = AdapterFolder(key);
            var properties = $"TiaReleaseKey={key};SiemensEngineeringDirectory={Path.Combine(context.PublicApiRoot, ReleaseCommands.PlcWorkerApi(key).Directory)}";
            yield return new("Adapter", Path.Combine(context.Root, $"src/Adapters/{folder}/Adapter.{key}.csproj"), properties);
            yield return new("Worker", Path.Combine(context.Root, "src/PlcWorker/TiaMcp.PlcWorker.csproj"), properties);
            yield return new("Studio", Path.Combine(context.Root, $"src/Studio/Openness/{folder}/StudioOpenness.{folder}.csproj"), properties);
        }
    }

    // Generated under bin-build: MSBuild itself schedules these independent nodes.
    // The only per-item global properties are the exact release and its SDK path.
    internal static XDocument BuildTableDocument(IEnumerable<Project> projects)
    {
        return new(new XElement("Project",
            new XElement("ItemGroup", projects.Select(project => new XElement("PrepareProject", new XAttribute("Include", project.Path),
                new XElement("AdditionalProperties", project.Properties)))),
            new XElement("Target", new XAttribute("Name", "Build"),
                new XElement("MSBuild", new XAttribute("Projects", "@(PrepareProject)"), new XAttribute("Targets", "Rebuild"),
                    new XAttribute("BuildInParallel", "true"), new XAttribute("StopOnFirstFailure", "true"),
                    new XAttribute("Properties", "Configuration=Release;BuildProjectReferences=false")))));
    }

    private void BuildTable(string name, IEnumerable<Project> projects)
    {
        var path = Path.Combine(context.OutputDirectory, name + ".proj");
        File.WriteAllText(path, BuildTableDocument(projects).ToString() + "\n", new UTF8Encoding(false));
        RunDotnet(name, ["msbuild", path, "-t:Build", "-m:" + context.MaxParallelism.ToString(CultureInfo.InvariantCulture),
            "-nodeReuse:false", "-nologo", "-v:minimal", .. properties]);
    }

    private void Restore(Project project) => RunDotnet("restore-" + project.Kind + "-" + Path.GetFileNameWithoutExtension(project.Path) +
        "-" + project.Properties.Split(';')[0].Split('=')[1],
        ["restore", project.Path, "-m:1", "-nodeReuse:false", .. properties, .. project.Properties.Split(';').Select(p => "-p:" + p)]);

    private void Build(string project, string name, params string[] extra) => RunDotnet(name,
        ["build", Path.Combine(context.Root, project), "-c", "Release", "--no-incremental", "-m:" + context.MaxParallelism.ToString(CultureInfo.InvariantCulture),
         "-nodeReuse:false", "--nologo", .. properties, .. extra]);

    private void Publish(string project, string output, string name, params string[] extra) => RunDotnet(name,
        ["publish", Path.Combine(context.Root, project), "-c", "Release", "-o", output, "-m:" + context.MaxParallelism.ToString(CultureInfo.InvariantCulture),
         "-nodeReuse:false", "--nologo", .. properties, .. extra]);

    private CommandResult RunDotnet(string name, IEnumerable<string> arguments)
    {
        var result = ProcessRunner.Run(dotnet, arguments, context.Root, environment, Path.Combine(context.Logs, name + ".log"));
        ProcessRunner.RequireSuccess(result, name + " failed; see " + Path.Combine(context.Logs, name + ".log"));
        return result;
    }

    private void Host()
    {
        var publish = Path.Combine(context.OutputDirectory, "host");
        Publish("src/FoundationHost/TiaMcp.FoundationHost.csproj", publish, "host", "-p:AppHostRelativeDotNet=../dotnet");
        foreach (var key in PlcWorkerFixtures.Keys)
        {
            var target = Path.Combine(context.Root, "runtime/v" + key);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(publish).Where(file => Path.GetExtension(file) is ".exe" or ".dll" or ".config" or ".json"))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(target, "release-key.txt"), key, new UTF8Encoding(false));
            ReleaseCommands.InstallReleasePlcWorker(context.Root, key);
            var worker = Path.Combine(target, "worker", $"TiaMcp.PlcWorker.{key}.exe");
            RequireWorkerMetadata(WorkerAssemblyMetadata.Read(worker), key, fixture: false, policy: null, worker);
        }
    }

    private void Studio()
    {
        var output = Path.Combine(context.Root, "runtime/studio");
        Publish("src/Studio/Gui/TiaOpenness.Gui.csproj", output, "studio");
        // The GUI's build copies the bridge but its publish target does not publish
        // those loose files. Install that runtime and each exact native adapter.
        var bridge = Path.Combine(output, "bridge");
        Directory.CreateDirectory(bridge);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(context.Root, "src/Studio/Gui/bin/Release/net10.0-windows/bridge"))
                     .Where(file => Path.GetExtension(file) is ".exe" or ".dll" or ".config"))
            File.Copy(file, Path.Combine(bridge, Path.GetFileName(file)), true);
        foreach (var key in PlcWorkerFixtures.Keys)
        {
            var source = Path.Combine(context.Root, $"src/Studio/Openness/{AdapterFolder(key)}/bin/Release/net48/TiaOpenness.Openness.dll");
            var target = Path.Combine(output, "bridge/adapters/v" + key);
            Directory.CreateDirectory(target);
            File.Copy(source, Path.Combine(target, Path.GetFileName(source)), true);
        }
    }

    private void BundledDotnet()
    {
        var pin = JsonNode.Parse(File.ReadAllText(Path.Combine(context.Root, "build-tools/bundled-dotnet.json")))!;
        var folder = "dotnet-" + (string)pin["version"]!;
        var common = ProcessRunner.Run("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"], context.Root);
        ProcessRunner.RequireSuccess(common, "Locate cached .NET archives");
        var caches = new[] { Path.Combine(context.Root, "bin-build/cache", folder),
            Path.Combine(Directory.GetParent(common.StandardOutput.Trim())!.FullName, "bin-build/cache", folder) };
        var archives = pin["archives"]!.AsArray().Select(row =>
        {
            var name = (string)row!["name"]!;
            var path = caches.Select(cache => Path.Combine(cache, name)).FirstOrDefault(File.Exists)
                ?? throw new ReleaseException("Bundled .NET archive missing (offline): " + name);
            return new XElement("RuntimeArchive", new XAttribute("Include", path), new XElement("Sha512", (string)row["sha512"]!));
        });
        var target = Path.Combine(context.Root, "runtime/dotnet");
        var project = new XDocument(new XElement("Project", new XElement("ItemGroup", archives),
            new XElement("Target", new XAttribute("Name", "Build"),
                new XElement("VerifyFileHash", new XAttribute("File", "%(RuntimeArchive.Identity)"),
                    new XAttribute("Hash", "%(RuntimeArchive.Sha512)"), new XAttribute("Algorithm", "SHA512")),
                new XElement("Unzip", new XAttribute("SourceFiles", "@(RuntimeArchive)"), new XAttribute("DestinationFolder", target)))));
        var path = Path.Combine(context.OutputDirectory, "bundled-dotnet.proj");
        File.WriteAllText(path, project.ToString() + "\n", new UTF8Encoding(false));
        RunDotnet("bundled-dotnet", ["msbuild", path, "-t:Build", "-nologo", "-m:1", "-nodeReuse:false"]);
        var errors = ReleaseCommands.BundledRuntimeProblems(context.Root);
        if (errors.Count != 0) throw new ReleaseException(string.Join("\n", errors));
    }

    private void Launcher()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var framework = Path.Combine(windows, "Microsoft.NET/Framework64/v4.0.30319");
        var wpf = Path.Combine(framework, "WPF");
        var sdk = RunDotnet("compiler-path", ["msbuild", Path.Combine(context.Root, "build-tools/release/TiaMcp.ReleaseTool.csproj"),
            "-nologo", "-getProperty:MSBuildToolsPath"]).StandardOutput.Trim();
        var version = ReleaseVersions.Require(context.Version, "Invalid version");
        var metadata = Path.Combine(context.OutputDirectory, "DesktopVersion.cs");
        File.WriteAllText(metadata, "using System.Reflection;\n[assembly: AssemblyTitle(\"TIA Portal Workbench\")]\n" +
            "[assembly: AssemblyDescription(\"Unified TIA Portal engineering, MCP service and AI client desktop\")]\n" +
            $"[assembly: AssemblyVersion(\"{version.Core}\")]\n[assembly: AssemblyFileVersion(\"{version.FileVersion}\")]\n" +
            $"[assembly: AssemblyInformationalVersion(\"{version.Label}\")]\n[assembly: AssemblyMetadata(\"TiaMcpRelease\", \"{version.Label}\")]\n", new UTF8Encoding(false));
        var references = new[] { "mscorlib.dll", "System.dll", "System.Windows.Forms.dll", "System.Web.Extensions.dll", "System.Security.dll", "System.Core.dll", "System.Xaml.dll" }
            .Select(name => "/r:" + Path.Combine(framework, name)).Concat(new[] { "WindowsBase.dll", "PresentationFramework.dll", "PresentationCore.dll" }.Select(name => "/r:" + Path.Combine(wpf, name)));
        RunDotnet("launcher", [Path.Combine(sdk, "Roslyn/bincore/csc.dll"), "/nologo", "/noconfig", "/nostdlib+", "/langversion:5",
            "/deterministic+", "/pathmap:" + context.Root + "=/_/repo", "/target:winexe", "/optimize+", "/utf8output",
            "/out:" + Path.Combine(context.Root, "TiaOpenness.exe"), .. references,
            Path.Combine(context.Root, "src/Studio/Launcher/Launcher.cs"), Path.Combine(context.Root, "src/Shared/ProcessArguments.cs"), metadata]);
    }

    private void TestProducers()
    {
        Build("tests/FoundationHost/TiaMcp.Engine.Harness/TiaMcp.Engine.Harness.csproj", "harness");
        var fixtures = new List<Project>();
        foreach (var unit in PlcWorkerFixtures.Builds(["20", "21"]))
        {
            var props = string.Join(';', new[] { "TiaReleaseKey=" + unit.Release,
                "SiemensEngineeringDirectory=" + Path.Combine(context.PublicApiRoot, ReleaseCommands.PlcWorkerApi(unit.Release).Directory) }
                .Concat(unit.Properties(Path.Combine(context.Logs, "plc-fixtures")).Select(p => p[3..])));
            var project = new Project("Fixture", Path.Combine(context.Root, "src/PlcWorker/TiaMcp.PlcWorker.csproj"), props);
            Restore(project);
            fixtures.Add(project);
        }
        BuildTable("plc-fixtures", fixtures);
        foreach (var unit in PlcWorkerFixtures.Builds(["20", "21"]))
        {
            var worker = unit.Worker(Path.Combine(context.Logs, "plc-fixtures"));
            RequireWorkerMetadata(WorkerAssemblyMetadata.Read(worker), unit.Release, fixture: true, policy: null, worker);
            VerifyAdapter(Path.Combine(Path.GetDirectoryName(worker)!, $"TiaMcp.Adapter.{unit.Release}.dll"),
                "fixture-adapter-" + unit.Release + "-" + unit.Variant);
        }
    }

    internal static void RequireWorkerMetadata(IReadOnlyDictionary<string, string?> metadata, string key, bool fixture, string? policy, string worker)
    {
        if (metadata.GetValueOrDefault("TiaReleaseKey") != key
            || (metadata.GetValueOrDefault("TiaMcpPlcWorkerSdkFixture") == "true") != fixture
            || metadata.GetValueOrDefault("TiaMcpTestPolicy") != policy)
            throw new ReleaseException("PlcWorker metadata differs from its build unit: " + worker);
    }

    private void VerifyAdapter(string adapter, string log) => RunDotnet(log,
        [Path.Combine(context.Root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll"), "verify", adapter]);

    internal static void RequireNoPublicApi(string root)
    {
        var runtime = Path.Combine(root, "runtime");
        if (Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories)
            .Any(path => Path.GetFileName(path).StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
            throw new ReleaseException("Siemens PublicAPI redistribution is forbidden");
    }

    internal static void CleanRuntime(string root)
    {
        // Start from source on every prepare. Old dependency closures in bin/obj
        // must not leak into the worker payload; the running release tool stays.
        var outputs = new List<string>();
        foreach (var relative in new[] { "src", "tests", "third_party", "build-tools/native-call-weaver" })
        {
            var parent = Path.GetFullPath(Path.Combine(root, relative));
            RequireOwnedTree(root, parent);
            if (!Directory.Exists(parent)) continue;
            var pending = new Stack<string>();
            pending.Push(parent);
            while (pending.TryPop(out var current))
                foreach (var directory in Directory.EnumerateDirectories(current))
                    if (Path.GetFileName(directory).Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(directory).Equals("obj", StringComparison.OrdinalIgnoreCase)) outputs.Add(directory);
                    else pending.Push(directory);
        }
        foreach (var directory in outputs) Directory.Delete(directory, true);
        var runtime = Path.GetFullPath(Path.Combine(root, "runtime"));
        RequireOwnedTree(root, runtime);
        if (!Directory.Exists(runtime)) { Directory.CreateDirectory(runtime); return; }
        foreach (var file in Directory.EnumerateFiles(runtime))
            File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(runtime)) Directory.Delete(directory, true);
    }

    internal static void RequireOwnedTree(string root, string directory)
    {
        var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Output leaves this worktree: " + full);
        for (var parent = new DirectoryInfo(full); parent != null && parent.FullName.Length >= prefix.Length; parent = parent.Parent)
            if (parent.Exists && parent.LinkTarget != null) throw new ReleaseException("Output contains a directory link: " + parent.FullName);
        if (!Directory.Exists(full)) return;
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.TryPop(out var current))
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new ReleaseException("Output contains a link: " + child);
                if (Directory.Exists(child)) pending.Push(child);
            }
    }

    private static void CopyTree(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    internal static readonly IReadOnlyDictionary<string, string> UpdaterOutputs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["runtime/tools/TiaMcp.Updater.exe"] = "bin-build/updater/TiaMcp.Updater.exe",
        ["runtime/tools/TiaMcp.Updater.exe.config"] = "bin-build/updater/TiaMcp.Updater.exe.config"
    };
}
