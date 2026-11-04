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
    public async Task File_tools_refuse_approval_settings_including_aliases_and_nested_calls(string release)
    {
        using var fixture = new FileToolFixture(release);
        string settings = TiaOpenness.Shared.ApprovalSettings.SettingsPath;
        byte[]? previous = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
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
                string link = Path.Combine(fixture.Root, "settings-link.scl");
                Assert.True(CreateHardLink(link, settings, IntPtr.Zero), "Hard link error: " + Marshal.GetLastWin32Error());
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
        using var fixture = new FileToolFixture(release);
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

    private sealed class FileToolFixture : IDisposable
    {
        internal readonly HardwareAddressingAdmissionTests.UnboundWorker Worker = new();
        internal readonly string Root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "sec1-fixtures", Guid.NewGuid().ToString("N"));
        private readonly IReadOnlyList<McpServerTool> tools;
        private readonly enginehost::TiaMcp.FoundationHost.EngineHostPipeline pipeline;
        internal FileToolFixture(string release, bool previewExport = false)
        {
            Directory.CreateDirectory(Root);
            IFoundationWorker target = previewExport ? new ExportPreviewWorker() : Worker;
            pipeline = new(release, Worker, LegacyHostToolRegistry.Create(target, release, false, hostPorts: false));
            tools = pipeline.AllTools;
        }
        internal Task<CallToolResult> Call(string name, object args) => tools.Single(t => t.ProtocolTool.Name == name).InvokeAsync(
            new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new() { Name = name, Arguments = Arguments(JsonSerializer.Serialize(args)) } }).AsTask();
        public void Dispose()
        {
            pipeline.Dispose(); Worker.Dispose();
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
