extern alias enginehost;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;
using TiaMcp.FoundationHost;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Services = enginehost::TiaMcpServer.Siemens.Services;
using Xunit;

public sealed class VersionControlAdmissionTests
{
    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task XPath_rules_are_not_treated_as_file_paths_with_approval_settings_present(string release)
    {
        using var fixture = new FileToolFixture(release);
        using var settings = new HumanSettingsFile();
        File.WriteAllText(Path.Combine(fixture.Root, "rule.xml"), "<A><B /></A>");
        var result = await fixture.Call("AuditEngineeringExports", new { directoryPath = fixture.Root,
            rules = new[] { new { id = "has-b", xpath = "//A/B", minCount = 1 } } });
        Succeeded(result);
        TiaMcp.Logic.V4.CallerInputFiles.RefuseHumanSettingsTargets(new JsonObject {
            ["rules"] = new JsonArray(new JsonObject { ["xpath"] = "//A/approval.settings" }) });
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory]
    [InlineData(53)]
    [InlineData(67)]
    [InlineData(161)]
    public void Unavailable_network_and_invalid_path_identities_are_missing(int error)
    {
        var missing = typeof(TiaOpenness.Shared.ApprovalSettings).GetMethod("MissingIdentity", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True((bool)missing.Invoke(null, new object[] { error })!);
        Assert.False((bool)missing.Invoke(null, new object[] { 5 })!);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL")]
    public void Bare_device_names_are_not_human_settings_targets(string name)
    {
        using var settings = new HumanSettingsFile();
        foreach (string path in new[] { name, @"\\.\" + name })
        {
            Assert.False(TiaOpenness.Shared.ApprovalSettings.IsAdministrativeTarget(path));
            Assert.False(TiaOpenness.Shared.ApprovalSettings.IsNativeCrossReferenceTarget(path));
            foreach (string parameter in new[] { "blockPath", "groupPath" })
                TiaMcp.Logic.V4.CallerInputFiles.Validate("GetPlcBlockInfo", new JsonObject { [parameter] = path });
        }
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Missing_settings_file_cannot_be_created_as_a_parent_directory(string release)
    {
        using var fixture = new FileToolFixture(release);
        using var settings = new HumanSettingsFile(present: false);
        string target = Path.Combine(settings.Path, "x.scl");
        try
        {
            Refused(await fixture.Call("WritePlcSclSourceFile", new { outputPath = target,
                sclContent = "FUNCTION Test : Void\nEND_FUNCTION" }));
            Assert.False(Directory.Exists(settings.Path));
            Assert.False(File.Exists(target));
        }
        finally
        {
            // Only the pre-fix writer can create this exact file and its initially absent parent.
            if (File.Exists(target)) File.Delete(target);
            if (Directory.Exists(settings.Path)) Directory.Delete(settings.Path);
        }
    }

    [Fact]
    public void Existing_consent_directory_and_relative_exportDir_remain_human_only()
    {
        using var settings = new HumanSettingsFile();
        string sessions = settings.Path + ".sessions";
        bool existed = Directory.Exists(sessions);
        Directory.CreateDirectory(sessions);
        try
        {
            Assert.True(TiaOpenness.Shared.ApprovalSettings.IsNativeCrossReferenceTarget(sessions));
            Assert.True(TiaOpenness.Shared.ApprovalSettings.IsAdministrativeTarget(sessions));
            foreach (string path in new[] { sessions, "approval.settings", ".git/config" })
                Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
                    TiaMcp.Logic.V4.CallerInputFiles.RefuseHumanSettingsTargets(new JsonObject { ["exportDir"] = path }));
        }
        finally { if (!existed) Directory.Delete(sessions); }
    }

    [Fact]
    public void Delete_character_in_a_rooted_filename_does_not_bypass_settings_protection()
    {
        using var settings = new HumanSettingsFile();
        string target = Path.Combine(System.IO.Path.GetDirectoryName(settings.Path)!, "valid\u007f", "approval.settings");
        Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            TiaMcp.Logic.V4.CallerInputFiles.RefuseHumanSettingsTargets(new JsonObject { ["target"] = target }));
        TiaMcp.Logic.V4.CallerInputFiles.RefuseHumanSettingsTargets(new JsonObject { ["text"] = "// header\n/approval.settings" });
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Git_refuses_an_implicitly_discovered_bare_repository_inside_a_working_tree(string release)
    {
        using var approval = new GrantedApproval();
        using var fixture = new FileToolFixture(release, approvalWait: GrantedApproval.Wait);
        await Git(fixture.Root, "init");
        await Git(fixture.Root, "config", "user.name", "SEC-1 fixture");
        await Git(fixture.Root, "config", "user.email", "sec1@example.invalid");
        File.WriteAllText(Path.Combine(fixture.Root, "Test.scl"), "FUNCTION Test : Void\nEND_FUNCTION");
        Succeeded(await fixture.Call("ManagePlcGitRepository", new { repositoryPath = fixture.Root, action = "commit",
            files = new[] { "Test.scl" }, message = "Bare repository fixture", dryRun = false }));
        string bare = Path.Combine(fixture.Root, "evil");
        string metadata = Path.Combine(fixture.Root, ".git");
        Directory.CreateDirectory(bare);
        foreach (string directory in Directory.EnumerateDirectories(metadata, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(bare, Path.GetRelativePath(metadata, directory)));
        foreach (string file in Directory.EnumerateFiles(metadata, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(bare, Path.GetRelativePath(metadata, file)));
        await Git(fixture.Root, "--git-dir=" + bare, "config", "core.bare", "true");
        Assert.True(File.Exists(Path.Combine(bare, "HEAD")));
        Assert.True(File.Exists(Path.Combine(bare, "config")));
        Assert.True(Directory.Exists(Path.Combine(bare, "objects")));
        Assert.True(Directory.Exists(Path.Combine(bare, "refs")));
        Assert.Contains("Bare repository fixture", await Git(bare, "log", "-1", "--format=%s"));
        var result = await fixture.Call("ManagePlcGitRepository", new { repositoryPath = bare, action = "history" });
        Assert.True(result.IsError, result.StructuredContent?.ToJsonString());
        Assert.Contains("safe.bareRepository", result.StructuredContent!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Succeeded(await fixture.Call("ManagePlcGitRepository", new { repositoryPath = fixture.Root, action = "history" }));
        Assert.Equal(0, fixture.Worker.Calls);
    }

    private sealed class GrantedApproval : IDisposable
    {
        private readonly Func<TiaOpenness.Shared.PendingApproval, TiaOpenness.Shared.ApprovalSettings, CancellationToken,
            Task<TiaOpenness.Shared.ApprovalOutcome>>? previous = Host.McpServer.ApprovalWaitOverrideForTests;
        internal GrantedApproval() => Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        internal static Task<TiaOpenness.Shared.ApprovalOutcome> Wait(TiaOpenness.Shared.PendingApproval card,
            TiaOpenness.Shared.ApprovalSettings settings, CancellationToken token)
            => Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(card, false, null));
        public void Dispose() => Host.McpServer.ApprovalWaitOverrideForTests = previous;
    }

    private sealed class HumanSettingsFile : IDisposable
    {
        internal string Path => TiaOpenness.Shared.ApprovalSettings.SettingsPath;
        private readonly byte[]? previous;
        internal HumanSettingsFile(bool present = true)
        {
            Assert.False(Directory.Exists(Path));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            previous = File.Exists(Path) ? File.ReadAllBytes(Path) : null;
            if (present) File.WriteAllText(Path, "enabled=true\ntimeoutSeconds=120\n");
            else File.Delete(Path);
        }
        public void Dispose()
        {
            if (previous == null) File.Delete(Path); else File.WriteAllBytes(Path, previous);
        }
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task File_tools_refuse_approval_settings_including_aliases_and_nested_calls(string release)
    {
        using var fixture = new FileToolFixture(release);
        string settings = TiaOpenness.Shared.ApprovalSettings.SettingsPath;
        byte[]? previous = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        string? createdLink = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "enabled=true\ntimeoutSeconds=120\n");
            byte[] expected = File.ReadAllBytes(settings);
            var paths = new List<string> { settings, Path.GetRelativePath(Environment.CurrentDirectory, settings), settings + "." };
            if (OperatingSystem.IsWindows())
            {
                var shortName = new StringBuilder(32768);
                Assert.NotEqual(0u, GetShortPathName(settings, shortName, (uint)shortName.Capacity));
                paths.Add(shortName.ToString());
                Console.WriteLine("Approval settings short path: " + shortName);
                string link = Path.Combine(Path.GetDirectoryName(settings)!, "settings-link.scl");
                Assert.True(CreateHardLink(link, settings, IntPtr.Zero), "Hard link error: " + Marshal.GetLastWin32Error());
                createdLink = link;
                paths.Add(link);
            }
            foreach (string path in paths)
            {
                var args = new { sclContent = "enabled=false\ntimeoutSeconds=120\n", outputPath = path };
                Refused(await fixture.Call("WritePlcSclSourceFile", args));
                Refused(await fixture.Call("CallTool", new { name = "WritePlcSclSourceFile", arguments = args }));
                Assert.Equal(expected, File.ReadAllBytes(settings));
                Assert.True(TiaOpenness.Shared.ApprovalSettings.Load(settings).Enabled);
            }
            Refused(await fixture.Call("PreviewToolBatch", new { expectedProject = "Fixture", operations = new[] {
                new { name = "ExportPlcBlocks", arguments = new { softwarePath = "PLC", groupPath = "", exportPath = settings, dryRun = true } } } }));
            File.Delete(settings);
            Refused(await fixture.Call("WritePlcSclSourceFile", new { sclContent = "enabled=false\ntimeoutSeconds=120\n",
                outputPath = Path.Combine(settings, "x", "..") }));
            Assert.False(File.Exists(settings));
        }
        finally
        {
            if (createdLink != null) File.Delete(createdLink);
            if (previous == null) File.Delete(settings); else File.WriteAllBytes(settings, previous);
        }
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Git_metadata_components_are_refused_but_working_tree_names_remain_valid(string release)
    {
        using var fixture = new FileToolFixture(release);
        foreach (string component in new[] { ".git", ".GIT", ".git.", ".git ", ".git::$INDEX_ALLOCATION", "GIT~1", "git~123" })
        foreach (char separator in new[] { '/', '\\' })
        {
            string path = fixture.Root + separator + component + separator + "config";
            Refused(await fixture.Call("WritePlcSclSourceFile", new { sclContent = "FUNCTION Test : Void\nEND_FUNCTION", outputPath = path }));
            Refused(await fixture.Call("ManagePlcGitRepository", new { repositoryPath = path }));
        }
        await Git(fixture.Root, "init");
        Succeeded(await fixture.Call("ManagePlcGitRepository", new { repositoryPath = fixture.Root }));
        foreach (string name in new[] { "x.git", ".github" })
            Succeeded(await fixture.Call("WritePlcSclSourceFile", new { sclContent = "FUNCTION Test : Void\nEND_FUNCTION",
                outputPath = Path.Combine(fixture.Root, name, "Test.scl") }));
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Scl_writer_rejects_executable_extensions_streams_and_trimmed_names_before_creating_anything(string release)
    {
        using var fixture = new FileToolFixture(release);
        const string source = "// header\nFUNCTION Test : Void\nEND_FUNCTION";
        foreach (string name in new[] { "a.cmd", "a.txt", "a.cmd:x.scl", "a.cmd::$DATA", "a.scl.", "a.scl " })
        {
            string parent = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N"));
            var result = await fixture.Call("WritePlcSclSourceFile", new { sclContent = source, outputPath = Path.Combine(parent, name) });
            Assert.True(result.IsError, result.StructuredContent?.ToJsonString());
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.False(Directory.Exists(parent));
        }
        string settings = TiaOpenness.Shared.ApprovalSettings.SettingsPath;
        byte[]? previous = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, "enabled=true\ntimeoutSeconds=120\n");
            foreach (string path in new[] { Path.Combine(fixture.Root, "upper.SCL"), Path.Combine(fixture.Root, "extensionless"), fixture.Root })
            {
                Succeeded(await fixture.Call("WritePlcSclSourceFile", new { sclContent = source, outputPath = path }));
                string output = Directory.Exists(path) ? Path.Combine(path, "Test.scl") : Path.HasExtension(path) ? path : path + ".scl";
                Assert.Equal(source, File.ReadAllText(output));
            }
            // Exercise an actual directory argument while settings identities are present.
            File.WriteAllText(Path.Combine(fixture.Root, "Block.xml"),
                "<Document><SW.Blocks.FC><AttributeList><Name>Block</Name></AttributeList></SW.Blocks.FC></Document>");
            Succeeded(await fixture.Call("AnalyzePlcReferences", new { directory = fixture.Root }));
            Assert.False(TiaOpenness.Shared.ApprovalSettings.IsAdministrativeTarget(fixture.Root));
            Assert.False(TiaOpenness.Shared.ApprovalSettings.IsNativeCrossReferenceTarget(fixture.Root));
        }
        finally
        {
            if (previous == null) File.Delete(settings); else File.WriteAllBytes(settings, previous);
        }
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Real_git_ignores_fsmonitor_hooks_and_signing_for_status_stage_and_commit(string release)
    {
        using var approval = new GrantedApproval();
        using var fixture = new FileToolFixture(release, approvalWait: GrantedApproval.Wait);
        await Git(fixture.Root, "init");
        await Git(fixture.Root, "config", "user.name", "SEC-1 fixture");
        await Git(fixture.Root, "config", "user.email", "sec1@example.invalid");
        string hook = Path.Combine(fixture.Root, ".git", "hooks", "pre-commit");
        string monitor = Path.Combine(fixture.Root, ".git", "monitor");
        File.WriteAllText(hook, "#!/bin/sh\necho ran > hook-ran\nexit 1\n", new UTF8Encoding(false));
        File.WriteAllText(monitor, "#!/bin/sh\necho ran > monitor-ran\nexit 1\n", new UTF8Encoding(false));
        await Git(fixture.Root, "config", "core.fsmonitor", monitor.Replace('\\', '/'));
        await Git(fixture.Root, "config", "commit.gpgsign", "true");
        await Git(fixture.Root, "config", "gpg.program", "sec1-nonexistent-signing-program");
        File.WriteAllText(Path.Combine(fixture.Root, "Test.scl"), "FUNCTION Test : Void\nEND_FUNCTION");
        foreach (string action in new[] { "status", "stage", "commit" })
        {
            Succeeded(await fixture.Call("ManagePlcGitRepository", new { repositoryPath = fixture.Root, action,
                files = new[] { "Test.scl" }, message = "SEC-1 synthetic fixture", dryRun = false }));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "hook-ran")));
            Assert.False(File.Exists(Path.Combine(fixture.Root, "monitor-ran")));
        }
        Assert.Contains("SEC-1 synthetic fixture", await Git(fixture.Root, "-c", "core.fsmonitor=false", "log", "-1", "--format=%s"));
        Assert.Equal(0, fixture.Worker.Calls);
    }

    private static void Refused(CallToolResult result)
    {
        Assert.True(result.IsError, result.StructuredContent?.ToJsonString());
        Assert.Equal("PRECONDITION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Contains("Human-only", (string?)result.StructuredContent?["error"]?["message"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Existing_export_directory_passes_file_admission_with_human_settings_present(string release)
    {
        using var fixture = new FileToolFixture(release, previewExport: true);
        string settings = TiaOpenness.Shared.ApprovalSettings.SettingsPath;
        byte[]? previous = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        string consentDirectory = settings + ".sessions";
        bool directoryExisted = Directory.Exists(consentDirectory);
        Directory.CreateDirectory(consentDirectory);
        string consent = Path.Combine(consentDirectory, "native-cross-references-" + Guid.NewGuid().ToString("N") + ".settings");
        try
        {
            File.WriteAllText(settings, "enabled=true\ntimeoutSeconds=120\n");
            File.WriteAllText(consent, "fixture consent identity");
            var result = await fixture.Call("ExportPlcBlocks", new { softwarePath = "PLC", groupPath = "", exportPath = fixture.Root, dryRun = true });
            Succeeded(result);
            Assert.DoesNotContain("Human-only", result.StructuredContent!.ToJsonString());
            TiaMcp.Logic.V4.CallerInputFiles.Validate("ExportPlcBlocks", new JsonObject { ["exportDirectory"] = fixture.Root });
            Assert.False(TiaOpenness.Shared.ApprovalSettings.IsAdministrativeTarget(fixture.Root));
            Assert.False(TiaOpenness.Shared.ApprovalSettings.IsNativeCrossReferenceTarget(fixture.Root));
        }
        finally
        {
            File.Delete(consent);
            if (!directoryExisted) Directory.Delete(consentDirectory);
            if (previous == null) File.Delete(settings); else File.WriteAllBytes(settings, previous);
        }
    }

    private static void Succeeded(CallToolResult result)
        => Assert.False(result.IsError, result.StructuredContent?.ToJsonString());

    private static async Task<string> Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git.exe") { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    internal sealed class FileToolFixture : IDisposable
    {
        internal readonly HardwareAddressingAdmissionTests.UnboundWorker Worker = new();
        internal readonly string Root = Path.Combine(TiaOpenness.Shared.DataLocations.Current.OutputRoot,
            "sec1-fixtures", Guid.NewGuid().ToString("N"));
        private readonly IReadOnlyList<McpServerTool> tools;
        private readonly enginehost::TiaMcp.FoundationHost.EngineHostPipeline pipeline;
        private readonly string previousRelease;
        private readonly string? previousProfile;
        internal FileToolFixture(string release, bool previewExport = false,
            Func<TiaOpenness.Shared.PendingApproval, TiaOpenness.Shared.ApprovalSettings, CancellationToken,
                Task<TiaOpenness.Shared.ApprovalOutcome>>? approvalWait = null, IFoundationWorker? worker = null,
            Func<Host.IToolCatalogView, Host.IToolCatalogView>? catalogForTest = null, McpServerTool? replacementTool = null)
        {
            previousRelease = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
            previousProfile = catalogForTest == null ? null : Host.McpServer.ResolvedProfile();
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            using var registration = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
                new enginehost::TiaMcp.FoundationHost.EngineSessionContext(Worker));
            Directory.CreateDirectory(Root);
            IFoundationWorker target = worker ?? (previewExport ? new ExportPreviewWorker() : Worker);
            var registry = LegacyHostToolRegistry.Create(target, release, false, hostPorts: false).ToList();
            if (approvalWait != null)
            {
                foreach (var raw in FoundationTools.Create(target, release).Concat(OfflineXmlTools.Create())
                    .Concat(PortedToolContract.Create(target, release)))
                {
                    int index = registry.FindIndex(tool => tool.ProtocolTool.Name == raw.ProtocolTool.Name);
                    if (index >= 0) registry[index] = new FoundationV4Tool(raw, release, approvalWait: approvalWait);
                }
            }
            if (replacementTool != null)
                registry[registry.FindIndex(tool => tool.ProtocolTool.Name == replacementTool.ProtocolTool.Name)] = replacementTool;
            var engineWorker = target as enginehost::TiaMcp.FoundationHost.IEngineWorker ?? Worker;
            pipeline = catalogForTest == null ? new(release, engineWorker, registry)
                : new("", Path.Combine(Root, "worker.exe"), release, engineWorker, "full", registry, null, catalogForTest);
            tools = catalogForTest == null ? pipeline.AllTools : pipeline.Tools.ToArray();
        }
        internal IDisposable EnterSession() => pipeline.EnterSession();
        internal Task<CallToolResult> Call(string name, object args) => tools.Single(t => t.ProtocolTool.Name == name).InvokeAsync(
            new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new() { Name = name, Arguments = Arguments(JsonSerializer.Serialize(args)) } }).AsTask();
        public void Dispose()
        {
            pipeline.Dispose(); Worker.Dispose();
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previousRelease;
            if (previousProfile != null) Host.McpServer.SetProfileOverride(previousProfile);
            foreach (string path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }

    private sealed class ExportPreviewWorker : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Assert.Equal("ExportBlocks", operation);
            Assert.True((bool)arguments["dryRun"]!);
            var result = TiaMcp.Adapters.PlcBatchExportPolicy.Run("blocks", "C:/Fixture.ap19", "PLC", "", false,
                (string)arguments["exportPath"]!, 128, true, "", Array.Empty<TiaMcp.Adapters.PlcBatchExportSource>(),
                () => throw new InvalidOperationException("Preview must not write."));
            return Task.FromResult(JsonSerializer.SerializeToNode(result));
        }
        public void Dispose() { }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Git_usage_preserves_the_commit_message_parameter_and_needs_no_worker(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        var server = DispatchProxy.Create<IMcpServer, ServerProxy>();
        var usage = await tools.Single(t => t.ProtocolTool.Name == "GetToolUsage").InvokeAsync(new(server) {
            Params = new() { Name = "GetToolUsage", Arguments = Arguments("{\"toolName\":\"ManagePlcGitRepository\"}") } });
        var schema = usage.StructuredContent!["data"]!["inputSchema"]!;
        Assert.True(JsonNode.DeepEquals(schema, JsonNode.Parse(tools.Single(t => t.ProtocolTool.Name == "ManagePlcGitRepository").ProtocolTool.InputSchema.GetRawText())));
        Assert.NotNull(schema["properties"]!["message"]);
        Assert.Null(schema["properties"]!["summary"]);
        await tools.Single(t => t.ProtocolTool.Name == "ManagePlcGitRepository").InvokeAsync(new(server) {
            Params = new() { Name = "ManagePlcGitRepository", Arguments = Arguments("{\"repositoryPath\":\"\"}") } });
        Assert.Equal(0, worker.Calls);
    }

    private static Dictionary<string, JsonElement> Arguments(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    public class ServerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
            => method!.Name == "get_Services" ? new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider() : null;
    }

    [Theory]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void An_unbound_workspace_read_keeps_the_original_project_refusal_without_dispatch(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var service = new Services.VersionControlPortService((_, _) => throw new InvalidOperationException("No worker dispatch expected."), () => "");
        var result = new Host.VersionControlTools(service).ListVersionControlWorkspacesV4();
        var body = result.StructuredContent!;
        Assert.Equal("PROJECT_NOT_BOUND", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.False((bool)body["meta"]!["requiresSessionReset"]!);
        Assert.Empty(body["data"]!["items"]!.AsArray());
        Assert.Equal("PROJECT_NOT_BOUND", (string?)body["data"]?["evidence"]?["v4Rejection"]);
    }
}
