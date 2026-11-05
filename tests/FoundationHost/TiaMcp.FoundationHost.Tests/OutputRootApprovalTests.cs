extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;

public sealed class OutputRootApprovalTests
{
    public static IEnumerable<object[]> WhitespaceTargets()
    {
        foreach (var pair in ExpectedOutputs)
            foreach (string path in new[] { @" ..\..\Startup\evil.cmd", @"D:\outside\target ", " " })
                yield return new object[] { pair.Key, pair.Value, path };
        foreach (var pair in AdditionalWriteTargets())
            foreach (string path in new[] { @" ..\..\Startup\evil.cmd", @"D:\outside\target ", " " })
                yield return new object[] { pair[0], pair[1], path };
    }

    public static IEnumerable<object[]> AdditionalWriteTargets()
    {
        yield return new object[] { "ImportDeviceAml", "logFilePath" };
        yield return new object[] { "ImportPlcBlockVerified", "evidenceDirectory" };
        yield return new object[] { "ManageGlobalLibrary", "destinationDirectory" };
        yield return new object[] { "RetrieveProjectArchive", "destinationDirectory" };
        yield return new object[] { "ManageLibraryMasterCopy", "destinationPath" };
        yield return new object[] { "SaveProjectCopy", "newProjectPath" };
    }

    [Theory, MemberData(nameof(WhitespaceTargets))]
    public void Every_caller_target_refuses_surrounding_whitespace(string tool, string key, string path)
    {
        var error = Assert.Throws<ArgumentException>(() => PendingApproval.Create("fixture", "21", tool,
            new JsonObject { [key] = path, ["action"] = "export" }.ToJsonString(), null, 30));
        Assert.Equal(key, error.ParamName);
    }

    [Theory, MemberData(nameof(AdditionalWriteTargets))]
    public void Write_and_session_targets_show_full_paths_and_bind_distinct_digests(string tool, string key)
    {
        PendingApproval Card(string path) => PendingApproval.Create("fixture", "21", tool,
            new JsonObject { [key] = path, ["password"] = "private-value", ["softwarePath"] = "PLC_1/Blocks" }.ToJsonString(), null, 30);
        const string path = @"D:\bundle\exports\key=x\..\..\..\Users\me\Startup\evil.cmd";
        var card = Card(path);
        Assert.Contains(Path.GetFullPath(path), card.Operations.Select(operation => operation.Target));
        Assert.Equal(Path.GetFullPath(path), (string?)JsonNode.Parse(card.ParametersJson)![key]);
        Assert.DoesNotContain("private-value", card.ParametersJson);
        Assert.Equal("PLC_1/Blocks", (string?)JsonNode.Parse(card.ParametersJson)!["softwarePath"]);
        Assert.NotEqual(Card(@"D:\outside\key=first").ArgumentDigest, Card(@"D:\outside\key=second").ArgumentDigest);
    }

    [Fact]
    public void A_multi_target_card_keeps_each_complete_filesystem_path()
    {
        const string archive = @"D:\outside\key=input.zap21";
        const string destination = @"D:\outside\key=project";
        var card = PendingApproval.Create("fixture", "21", "RetrieveProjectArchive",
            new JsonObject { ["archivePath"] = archive, ["destinationDirectory"] = destination }.ToJsonString(), null, 30);
        Assert.Equal(new[] { archive, destination }, card.Operations.Select(operation => operation.Target));
    }

    [Theory]
    [InlineData("14sp1", true)]
    [InlineData("14sp1", false)]
    [InlineData("15.1", true)]
    [InlineData("15.1", false)]
    [InlineData("16", true)]
    [InlineData("16", false)]
    [InlineData("17", true)]
    [InlineData("17", false)]
    [InlineData("18", true)]
    [InlineData("18", false)]
    [InlineData("19", true)]
    [InlineData("19", false)]
    [InlineData("20", true)]
    [InlineData("20", false)]
    [InlineData("21", true)]
    [InlineData("21", false)]
    public async Task Whitespace_is_invalid_before_any_card_or_file_write(string release, bool leading)
    {
        using var settings = new SettingsScope();
        int cards = 0;
        Task<ApprovalOutcome> Wait(PendingApproval card, ApprovalSettings _, CancellationToken token)
        { cards++; return Task.FromResult(new ApprovalOutcome(card, false, "denied")); }
        var previous = Host.McpServer.ApprovalWaitOverrideForTests;
        Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        try
        {
            using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, approvalWait: Wait);
            using var session = fixture.EnterSession();
            string path = leading ? @" ..\..\Startup\evil.cmd" : Path.Combine(fixture.Root, "out.txt") + " ";
            string exportId = enginehost::TiaMcp.FoundationHost.SessionExportStore.PutAndSlice("fixture", "fixture", "payload", 4).id;
            var result = await fixture.Call("SaveExportContent", new { outputPath = path, exportId });
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal(0, cards);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "out.txt")));
        }
        finally { Host.McpServer.ApprovalWaitOverrideForTests = previous; }
    }

    [Fact]
    public void Output_cards_bind_the_full_normalized_target_without_redacting_path_segments()
    {
        const string path = @"D:\bundle\exports\key=x\..\..\..\Users\me\Startup\evil.cmd";
        PendingApproval Card(string target) => PendingApproval.Create("fixture", "21", "SaveExportContent",
            new JsonObject { ["outputPath"] = target, ["password"] = "private-value" }.ToJsonString(), null, 30);
        var card = Card(path);
        Assert.Equal(Path.GetFullPath(path), card.Operations.Single().Target);
        Assert.Equal(Path.GetFullPath(path), (string?)JsonNode.Parse(card.ParametersJson)!["outputPath"]);
        Assert.DoesNotContain("private-value", card.ParametersJson);
        Assert.NotEqual(Card(@"D:\outside\key=first.cmd").ArgumentDigest, Card(@"D:\outside\key=second.cmd").ArgumentDigest);
    }

    [Fact]
    public void Output_digest_distinguishes_path_text_after_a_secret_marker()
    {
        PendingApproval Card(string name) => PendingApproval.Create("fixture", "21", "SaveExportContent",
            new JsonObject { ["outputPath"] = @"D:\outside\key=" + name }.ToJsonString(), null, 30);
        Assert.NotEqual(Card("first.cmd").ArgumentDigest, Card("second.cmd").ArgumentDigest);
        Assert.NotEqual(Card("first.cmd").InvocationArgumentsDigest, Card("second.cmd").InvocationArgumentsDigest);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Oversized_output_is_invalid_before_any_card(string release)
    {
        using var settings = new SettingsScope();
        int cards = 0;
        Task<ApprovalOutcome> Wait(PendingApproval card, ApprovalSettings _, CancellationToken token)
        { cards++; return Task.FromResult(new ApprovalOutcome(card, false, "denied")); }
        var previous = Host.McpServer.ApprovalWaitOverrideForTests;
        Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        try
        {
            using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, approvalWait: Wait);
            string path = Path.Combine(Path.GetPathRoot(fixture.Root)!, string.Join("\\", Enumerable.Repeat(new string('x', 100), 42)), "out.txt");
            var result = await fixture.Call("SaveExportContent", new { outputPath = path, exportId = "unused" });
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal(0, cards);
        }
        finally { Host.McpServer.ApprovalWaitOverrideForTests = previous; }
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("commit")]
    public void Git_mutations_inside_output_root_still_need_approval(string action)
    {
        using var actor = ActorScope.EnterCall("review-test");
        var args = new JsonObject { ["repositoryPath"] = DataLocations.Current.OutputRoot, ["action"] = action, ["dryRun"] = false };
        Assert.True(HostBehavior.ApprovalWrite("ManagePlcGitRepository", args, false, true, true));
        args["dryRun"] = true;
        Assert.False(HostBehavior.ApprovalWrite("ManagePlcGitRepository", args, false, true, true));
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Git_inside_output_root_is_denied_before_staging(string release)
    {
        using var settings = new SettingsScope();
        var cards = new List<PendingApproval>();
        Task<ApprovalOutcome> Wait(PendingApproval card, ApprovalSettings _, CancellationToken token)
        { cards.Add(card); return Task.FromResult(new ApprovalOutcome(card, false, "denied")); }
        var previous = Host.McpServer.ApprovalWaitOverrideForTests;
        Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        try
        {
            using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, approvalWait: Wait);
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = fixture.Root,
                UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("init");
            using (var git = System.Diagnostics.Process.Start(start)!)
            { await git.WaitForExitAsync(); Assert.Equal(0, git.ExitCode); }
            File.WriteAllText(Path.Combine(fixture.Root, "Test.scl"), "FUNCTION Test : Void\nEND_FUNCTION");
            foreach (string action in new[] { "stage", "commit" })
            {
                var result = await fixture.Call("ManagePlcGitRepository", new { action, repositoryPath = fixture.Root,
                    files = new[] { "Test.scl" }, message = "Fixture commit", dryRun = false });
                Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
                Assert.Equal("denied", (string?)result.StructuredContent?["error"]?["details"]?["reason"]);
                Assert.DoesNotContain("skip confirmation", (string?)result.StructuredContent?["error"]?["message"]);
            }
            Assert.Equal(2, cards.Count);
            Assert.All(cards, card => Assert.Equal(fixture.Root, card.Operations.Single().Target));
            Assert.False(File.Exists(Path.Combine(fixture.Root, ".git", "index")));
        }
        finally { Host.McpServer.ApprovalWaitOverrideForTests = previous; }
    }

    [Fact]
    public void Empty_handoff_output_tracks_the_input_report_parent()
    {
        string parent = Path.Combine(Path.GetTempPath(), "outside-reports");
        var args = new JsonObject { ["outputDirectory"] = "", ["offlineReleaseSuiteJsonPath"] = Path.Combine(parent, "suite.json") };
        Assert.Equal(new[] { parent }, CallerInputFiles.OutputTargets("BuildReleaseHandoffArtifacts", args));
        Assert.True(CallerInputFiles.WritesOutsideOutputRoot("BuildReleaseHandoffArtifacts", args));
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Empty_handoff_output_is_reviewed_before_writing_beside_input(string release)
    {
        using var settings = new SettingsScope();
        string parent = Path.Combine(Path.GetDirectoryName(DataLocations.Current.OutputRoot)!, "handoff-review", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        string input = Path.Combine(parent, "suite.json");
        File.WriteAllText(input, "{\"ok\":true}");
        var cards = new List<PendingApproval>();
        Task<ApprovalOutcome> Wait(PendingApproval card, ApprovalSettings _, CancellationToken token)
        { cards.Add(card); return Task.FromResult(new ApprovalOutcome(card, false, "denied")); }
        var previous = Host.McpServer.ApprovalWaitOverrideForTests;
        Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        try
        {
            using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, approvalWait: Wait);
            Refused(await fixture.Call("BuildReleaseHandoffArtifacts", new { offlineReleaseSuiteJsonPath = input, outputDirectory = "" }), "denied");
            Assert.Equal(new[] { input, parent }, Assert.Single(cards).Operations.Select(operation => operation.Target));
            Assert.Equal(new[] { input }, Directory.GetFiles(parent));
        }
        finally { Host.McpServer.ApprovalWaitOverrideForTests = previous; Directory.Delete(parent, true); }
    }

    public static IEnumerable<object[]> RefusalCases()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (string tool in new[] { "SaveExportContent", "WritePlcSclSourceFile", "GenerateErrorReport",
            "BuildDeviceAmlDocument", "RunOfflineReleaseValidationSuite" })
        foreach (bool nested in new[] { false, true })
        foreach (string reason in new[] { "workbench-unavailable", "denied", "timeout" })
            yield return new object[] { release, tool, nested, reason };
    }

    [Theory, MemberData(nameof(RefusalCases))]
    public async Task Outside_targets_are_refused_before_creating_files(string release, string tool, bool nested, string reason)
    {
        using var settings = new SettingsScope();
        Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>? wait = reason == "workbench-unavailable"
            ? null : (card, _, _) => Task.FromResult(new ApprovalOutcome(card, false, reason));
        var previous = Host.McpServer.ApprovalWaitOverrideForTests;
        Host.McpServer.ApprovalWaitOverrideForTests = wait;
        try
        {
            using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, approvalWait: wait);
            using var session = fixture.EnterSession();
            string parent = Path.Combine(Path.GetDirectoryName(DataLocations.Current.OutputRoot)!, "outroot-tests", Guid.NewGuid().ToString("N"));
            var args = Arguments(tool, parent);
            var result = await fixture.Call(nested ? "CallTool" : tool, nested ? new { name = tool, arguments = args } : args);
            Refused(result, reason);
            Assert.False(Directory.Exists(parent));
            Assert.Equal(0, fixture.Worker.Calls);
        }
        finally { Host.McpServer.ApprovalWaitOverrideForTests = previous; }
    }

    [Theory]
    [InlineData("14sp1", "SaveExportContent")]
    [InlineData("15.1", "SaveExportContent")]
    [InlineData("16", "SaveExportContent")]
    [InlineData("17", "SaveExportContent")]
    [InlineData("18", "SaveExportContent")]
    [InlineData("19", "SaveExportContent")]
    [InlineData("20", "SaveExportContent")]
    [InlineData("21", "SaveExportContent")]
    [InlineData("14sp1", "WritePlcSclSourceFile")]
    [InlineData("15.1", "WritePlcSclSourceFile")]
    [InlineData("16", "WritePlcSclSourceFile")]
    [InlineData("17", "WritePlcSclSourceFile")]
    [InlineData("18", "WritePlcSclSourceFile")]
    [InlineData("19", "WritePlcSclSourceFile")]
    [InlineData("20", "WritePlcSclSourceFile")]
    [InlineData("21", "WritePlcSclSourceFile")]
    [InlineData("14sp1", "GenerateErrorReport")]
    [InlineData("15.1", "GenerateErrorReport")]
    [InlineData("16", "GenerateErrorReport")]
    [InlineData("17", "GenerateErrorReport")]
    [InlineData("18", "GenerateErrorReport")]
    [InlineData("19", "GenerateErrorReport")]
    [InlineData("20", "GenerateErrorReport")]
    [InlineData("21", "GenerateErrorReport")]
    [InlineData("14sp1", "BuildDeviceAmlDocument")]
    [InlineData("15.1", "BuildDeviceAmlDocument")]
    [InlineData("16", "BuildDeviceAmlDocument")]
    [InlineData("17", "BuildDeviceAmlDocument")]
    [InlineData("18", "BuildDeviceAmlDocument")]
    [InlineData("19", "BuildDeviceAmlDocument")]
    [InlineData("20", "BuildDeviceAmlDocument")]
    [InlineData("21", "BuildDeviceAmlDocument")]
    public async Task Denied_granted_and_disabled_follow_the_existing_approval_flow(string release, string tool)
    {
        using var settings = new SettingsScope();
        string? reason = "denied";
        bool disabled = false;
        var cards = new List<PendingApproval>();
        Task<ApprovalOutcome> Wait(PendingApproval card, ApprovalSettings _, CancellationToken token)
        {
            cards.Add(card);
            return Task.FromResult(new ApprovalOutcome(card, disabled, reason));
        }
        var previous = Host.McpServer.ApprovalWaitOverrideForTests;
        Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        string parent = Path.Combine(Path.GetDirectoryName(DataLocations.Current.OutputRoot)!, "outroot-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, approvalWait: Wait);
            using var session = fixture.EnterSession();
            var args = Arguments(tool, parent);
            Refused(await fixture.Call(tool, args), "denied");
            Assert.Single(cards);
            Assert.False(Directory.Exists(parent));
            Assert.StartsWith(parent, cards[0].Operations[0].Target);
            reason = null;
            if (tool == "BuildDeviceAmlDocument") Directory.CreateDirectory(parent);
            var granted = await fixture.Call(tool, args);
            Assert.False(granted.IsError, granted.StructuredContent?.ToJsonString());
            Assert.NotEmpty(Directory.GetFiles(parent, "*", SearchOption.AllDirectories));
            Assert.Equal(2, cards.Count);
            disabled = true;
            new ApprovalSettings(false, 1).Save(ApprovalSettings.SettingsPath);
            string other = Path.Combine(parent, "disabled");
            if (tool == "BuildDeviceAmlDocument") Directory.CreateDirectory(other);
            var result = await fixture.Call(tool, Arguments(tool, other));
            Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
            Assert.Contains(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), warning =>
                (string?)warning?["code"] == "APPROVAL_DISABLED");
        }
        finally
        {
            Host.McpServer.ApprovalWaitOverrideForTests = previous;
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Inside_targets_and_workbench_actor_do_not_ask_and_session_reports_root(string release)
    {
        using var settings = new SettingsScope();
        using var worker = new PreviewWorker(release);
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, worker: worker);
        var result = await fixture.Call("WritePlcSclSourceFile", Arguments("WritePlcSclSourceFile", fixture.Root));
        Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        foreach (string target in new[] { Path.Combine(fixture.Root, "approval.settings"), Path.Combine(fixture.Root, ".git", "config") })
        {
            var refused = await fixture.Call("WritePlcSclSourceFile", new { outputPath = target, sclContent = "FUNCTION Test : Void\nEND_FUNCTION" });
            Assert.Equal("PRECONDITION_FAILED", (string?)refused.StructuredContent?["error"]?["code"]);
            Assert.Contains("Human-only", (string?)refused.StructuredContent?["error"]?["message"]);
            Assert.False(File.Exists(target));
        }
        var state = await fixture.Call("GetSessionState", new { });
        Assert.False(state.IsError, state.StructuredContent?.ToJsonString());
        Assert.Equal(DataLocations.Current.OutputRoot, (string?)state.StructuredContent?["data"]?["outputRoot"]);
        string outside = Path.Combine(Path.GetDirectoryName(DataLocations.Current.OutputRoot)!, "outroot-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var actor = ActorScope.EnterWorkbench("output-test");
            result = await fixture.Call("WritePlcSclSourceFile", Arguments("WritePlcSclSourceFile", outside));
            Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        }
        finally { if (Directory.Exists(outside)) Directory.Delete(outside, true); }
    }

    [Theory]
    [InlineData("ExchangeUnifiedTags", "directory", "export", true)]
    [InlineData("ExchangeUnifiedTags", "directory", "import", true)]
    [InlineData("ManagePlcGitRepository", "repositoryPath", "status", false)]
    [InlineData("ManagePlcGitRepository", "repositoryPath", "stage", true)]
    [InlineData("ManagePlcGitRepository", "repositoryPath", "commit", true)]
    [InlineData("ManageHardwareUtilities", "filePath", "list", false)]
    [InlineData("ManageHardwareUtilities", "filePath", "exportOpcUa", true)]
    [InlineData("ManageUnifiedCwcPackage", "outputPath", "inspect", false)]
    [InlineData("ManageUnifiedCwcPackage", "outputPath", "build", true)]
    public void Actions_preserve_import_approval_and_gate_only_output_actions(string tool, string parameter, string action, bool write)
    {
        var args = new JsonObject { [parameter] = Path.GetTempPath(), ["action"] = action, ["dryRun"] = false };
        Assert.Equal(write, HostBehavior.ApprovalWrite(tool, args, false, true, true));
        args["dryRun"] = true;
        Assert.False(HostBehavior.ApprovalWrite(tool, args, false, true, true));
        Assert.Empty(CallerInputFiles.OutputTargets(tool, new JsonObject()));
    }

    [Theory]
    [InlineData("outputDirectory")]
    [InlineData("exportDir")]
    [InlineData("dir")]
    [InlineData("directory")]
    public void Directory_targets_are_visible_on_cards_without_changing_the_digest(string parameter)
    {
        string path = @"\\127.0.0.1\nonexistent$\x.zap21";
        var card = PendingApproval.Create("test", "21", "ArchiveSavedProject",
            new JsonObject { [parameter] = path }.ToJsonString(), null, 1);
        Assert.Equal(Path.GetFullPath(path), card.Operations.Single().Target);
        Assert.Contains("127.0.0.1", card.Operations[0].Target);
        Assert.True(CallerInputFiles.WritesOutsideOutputRoot("ArchiveSavedProject", new JsonObject { ["archivePath"] = path }));
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Export_preview_and_batch_apply_ask_before_the_worker_writes(string release)
    {
        using var settings = new SettingsScope();
        using var worker = new PreviewWorker(release);
        int cards = 0;
        Task<ApprovalOutcome> Wait(PendingApproval card, ApprovalSettings _, CancellationToken token)
        {
            cards++;
            Assert.Equal(0, worker.Writes);
            return Task.FromResult(new ApprovalOutcome(card, false, "denied"));
        }
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, approvalWait: Wait, worker: worker);
        string directory = Path.Combine(Path.GetDirectoryName(DataLocations.Current.OutputRoot)!, "outroot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Host.McpServer.ApprovalWaitOverrideForTests;
        Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        try
        {
            var args = new JsonObject { ["softwarePath"] = "PLC", ["groupPath"] = "", ["exportPath"] = directory, ["dryRun"] = true };
            var preview = await fixture.Call("ExportPlcBlocks", args);
            Assert.False(preview.IsError, preview.StructuredContent?.ToJsonString());
            Assert.Equal(0, cards);
            Assert.Equal(1, worker.Previews);
            args["dryRun"] = false;
            args["confirm"] = true;
            args["expectedProjectFile"] = worker.ProjectFile;
            args["expectedInventoryHash"] = (string?)preview.StructuredContent?["data"]?["inventoryHash"];
            Refused(await fixture.Call("ExportPlcBlocks", args), "denied");
            Assert.Equal(1, cards);
            Assert.Equal(2, worker.Previews);
            int batchWrites = 0;
            var batchTool = McpServerTool.Create((string exportPath, bool dryRun = true) =>
            {
                if (!dryRun) batchWrites++;
                return new JsonObject { ["success"] = true, ["dryRun"] = dryRun, ["exportPath"] = exportPath };
            }, new() { Name = "ExportPlcBlocks" });
            using var batchFixture = new VersionControlAdmissionTests.FileToolFixture(release, worker: worker,
                catalogForTest: catalog => new BatchExportCatalog(catalog),
                replacementTool: new FoundationV4Tool(batchTool, release, () => new ApprovalSettings(true, 1), Wait));
            var batch = await batchFixture.Call("PreviewToolBatch", new { expectedProject = "Fixture",
                operations = new[] { new { name = "ExportPlcBlocks", arguments = new { exportPath = directory, dryRun = true } } } });
            Assert.False(batch.IsError, batch.StructuredContent?.ToJsonString());
            var applied = await batchFixture.Call("ApplyToolBatch", new { previewToken = (string?)batch.StructuredContent?["data"]?["token"] });
            Assert.True(applied.IsError, applied.StructuredContent?.ToJsonString());
            Assert.True((string?)applied.StructuredContent?["error"]?["code"] == "CONFIRMATION_REQUIRED",
                applied.StructuredContent?.ToJsonString());
            Assert.Equal(2, cards);
            Assert.Equal(0, batchWrites);
            Assert.Equal(0, worker.Writes);
        }
        finally
        {
            Host.McpServer.ApprovalWaitOverrideForTests = previous;
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Unc_archive_card_names_the_host_and_denial_never_dispatches_a_write(string release)
    {
        using var settings = new SettingsScope();
        // Missing settings defaults to approval enabled. With a settings file present, the sandbox refuses
        // the UNC file-identity probe; the existing Human-only fail-closed check must remain unchanged.
        File.Delete(ApprovalSettings.SettingsPath);
        using var worker = new PreviewWorker(release);
        int cards = 0;
        Task<ApprovalOutcome> Wait(PendingApproval card, ApprovalSettings _, CancellationToken token)
        {
            cards++;
            Assert.Contains("127.0.0.1", card.Operations[0].Target);
            Assert.Equal(0, worker.Writes);
            return Task.FromResult(new ApprovalOutcome(card, false, "denied"));
        }
        var previous = Host.McpServer.ApprovalWaitOverrideForTests;
        Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        try
        {
            using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, approvalWait: Wait, worker: worker);
            var result = await fixture.Call("ArchiveSavedProject", new { archivePath = @"\\127.0.0.1\nonexistent$\x.zap21", dryRun = false });
            Refused(result, "denied");
            Assert.Equal(1, cards);
            Assert.Equal(0, worker.Writes);
        }
        finally { Host.McpServer.ApprovalWaitOverrideForTests = previous; }
    }

    [Theory]
    [InlineData("19")]
    [InlineData("21")]
    public async Task Output_actions_show_a_card_through_the_real_entry(string release)
    {
        using var settings = new SettingsScope();
        using var worker = new PreviewWorker(release);
        int cards = 0;
        Task<ApprovalOutcome> Wait(PendingApproval card, ApprovalSettings _, CancellationToken token)
        {
            cards++;
            Assert.Equal(0, worker.Writes);
            return Task.FromResult(new ApprovalOutcome(card, false, "denied"));
        }
        var previous = Host.McpServer.ApprovalWaitOverrideForTests;
        Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        string root = Path.Combine(Path.GetDirectoryName(DataLocations.Current.OutputRoot)!, "outroot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, approvalWait: Wait, worker: worker);
            Refused(await fixture.Call("ExchangeUnifiedTags", new { softwarePath = "HMI", action = "export", directory = root,
                fileName = "tags.xlsx", dryRun = false }), "denied");
            Refused(await fixture.Call("ManageHardwareUtilities", new { action = "exportOpcUa", devicePath = new[] { "PLC" },
                filePath = Path.Combine(root, "opc.xml"),
                dryRun = false }), "denied");
            string source = Path.Combine(root, "cwc");
            Directory.CreateDirectory(Path.Combine(source, "control"));
            File.WriteAllText(Path.Combine(source, "control", "index.html"), "<html/>");
            File.WriteAllText(Path.Combine(source, "manifest.json"),
                "{\"mver\":\"1.2.0\",\"control\":{\"identity\":{\"name\":\"Test\",\"displayname\":\"Test\",\"version\":\"1.0\","
                    + "\"type\":\"guid://551BF148-2F0D-4293-8E10-C9C3A1A6A073\"}}}");
            var inspect = TiaMcpServer.ModelContextProtocol.UnifiedCwcPackage.Run(source, "inspect", "", true, "");
            Refused(await fixture.Call("ManageUnifiedCwcPackage", new { action = "build", directory = source, dryRun = false,
                outputPath = Path.Combine(root, (string)inspect["suggestedFileName"]!),
                expectedPackageHash = (string)inspect["packageFingerprint"]! }), "denied");
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = root,
                UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("init");
            using (var git = System.Diagnostics.Process.Start(start)!)
            {
                await git.WaitForExitAsync();
                Assert.Equal(0, git.ExitCode);
            }
            File.WriteAllText(Path.Combine(root, "Test.scl"), "FUNCTION Test : Void\nEND_FUNCTION");
            foreach (string action in new[] { "stage", "commit" })
                Refused(await fixture.Call("ManagePlcGitRepository", new { action, repositoryPath = root, files = new[] { "Test.scl" },
                    message = "Output approval fixture", dryRun = false }), "denied", rootHint: false);
            Assert.Equal(5, cards);
            Assert.Equal(0, worker.Writes);
        }
        finally
        {
            Host.McpServer.ApprovalWaitOverrideForTests = previous;
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> ExpectedOutputs = new Dictionary<string, string>
    {
        ["ExportAlarmClasses"] = "exportPath",
        ["ExportAlarmInstanceTexts"] = "exportPath",
        ["ExportAlarmTextLists"] = "exportPath",
        ["ExportDeviceAml"] = "exportPath",
        ["ExportHmiConnection"] = "exportPath",
        ["ExportHmiScreen"] = "exportPath",
        ["ExportHmiTagTable"] = "exportPath",
        ["ExportOpcUaInterface"] = "exportPath",
        ["ExportPlcBlock"] = "exportPath",
        ["ExportPlcBlockDocuments"] = "exportPath",
        ["ExportPlcBlocks"] = "exportPath",
        ["ExportPlcBlocksDocuments"] = "exportPath",
        ["ExportPlcTagTable"] = "exportPath",
        ["ExportPlcType"] = "exportPath",
        ["ExportPlcTypes"] = "exportPath",
        ["ExportPlcWatchTable"] = "exportPath",
        ["ExportTechnologyObject"] = "exportPath",
        ["ExportProjectTexts"] = "filePath",
        ["ExportSafetyPrintout"] = "filePath",
        ["ExportScadaData"] = "filePath",
        ["GeneratePlcLoadableFile"] = "filePath",
        ["GeneratePlcSourceFromBlocks"] = "filePath",
        ["ExchangeSystemDiagnosticsSettings"] = "filePath",
        ["ManageHardwareUtilities"] = "filePath",
        ["GenerateOpcUaModelledInterface"] = "outputPath",
        ["GeneratePlcDocumentation"] = "outputPath",
        ["PatchPlcBlockDocument"] = "outputPath",
        ["RenderPlcBlock"] = "outputPath",
        ["RenderPlcBlockDocument"] = "outputPath",
        ["RenderPlcProgramAtlas"] = "outputPath",
        ["RenderPlcVisualDiff"] = "outputPath",
        ["SaveExportContent"] = "outputPath",
        ["WritePlcSclSourceFile"] = "outputPath",
        ["ManageUnifiedCwcPackage"] = "outputPath",
        ["BuildDeviceAmlDocument"] = "outputPath",
        ["BuildReleaseHandoffArtifacts"] = "outputDirectory",
        ["GenerateAcceptanceReport"] = "outputDirectory",
        ["GenerateErrorReport"] = "outputDirectory",
        ["InstantiatePlcTemplates"] = "outputDirectory",
        ["WriteClassicHmiMinimalPackageFiles"] = "outputDirectory",
        ["ExportHmiProgram"] = "exportDir",
        ["ExportTechnologyObjectsToDirectory"] = "exportDir",
        ["DownloadPlcToFolder"] = "destinationDirectory",
        ["ExportUnifiedEngineeringList"] = "destinationDirectory",
        ["ExchangeUnifiedTags"] = "directory",
        ["ExchangeUnifiedScriptModules"] = "directory",
        ["ArchiveSavedProject"] = "archivePath",
        ["AuditEngineeringExports"] = "reportPath",
        ["ScanPlcSourceAnnotations"] = "csvPath",
        ["ExportPlcProDiagInfo"] = "directoryPath",
        ["ExportPlcWatchTablesToDirectory"] = "dir",
        ["ManagePlcGitRepository"] = "repositoryPath",
        ["RunClassicHmiOfflineValidationSuite"] = "reportDirectory",
        ["RunClassicHmiTemporaryImportPreflight"] = "reportDirectory",
        ["RunHmiTemplatePlcSyncPrecheckSuite"] = "reportDirectory",
        ["RunOfflineReleaseValidationSuite"] = "reportDirectory",
    };

    [Fact]
    public void All_eight_file_catalogs_have_explicit_output_coverage()
        => AssertOutputCoverage(CallerInputFiles.OutputTargets);

    [Theory]
    [InlineData("RunClassicHmiOfflineValidationSuite")]
    [InlineData("RunClassicHmiTemporaryImportPreflight")]
    [InlineData("RunHmiTemplatePlcSyncPrecheckSuite")]
    public void Completeness_rejects_a_wrong_output_parameter_for_each_execute_suite(string mutatedTool)
    {
        IEnumerable<string> MutatedTargets(string tool, JsonObject arguments)
        {
            if (tool != mutatedTool) return CallerInputFiles.OutputTargets(tool, arguments);
            var wrong = arguments.DeepClone().AsObject();
            wrong.Remove("reportDirectory");
            wrong["outputDirectory"] = "outputDirectory";
            return CallerInputFiles.OutputTargets(tool, wrong);
        }
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertOutputCoverage(MutatedTargets));
    }

    private static void AssertOutputCoverage(Func<string, JsonObject, IEnumerable<string>> targets)
    {
        string repository = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "manifest", "tools-list.json")))!;
        var exceptions = new HashSet<string>(StringComparer.Ordinal) { "ManageStandardPackage", "ManageMachineDescription",
            "PublishGenerationRecord", "ManageGenerationArtifacts", "StageImportFiles", "CleanupStagedImportFiles" };
        var actions = new[] { "", "export", "exportOpcUa", "exportCardReaderPsc", "build", "stage", "commit" };
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var schemaCovered = new HashSet<string>(StringComparer.Ordinal);
        var baselines = Directory.GetFiles(Path.Combine(repository, "manifest", "contracts", "v4", "baseline"), "*.json");
        Assert.Equal(new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            baselines.Select(Path.GetFileNameWithoutExtension).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(56, ExpectedOutputs.Count);
        foreach (string file in baselines)
        {
            var catalog = JsonNode.Parse(File.ReadAllText(file))!;
            foreach (var tool in catalog["tools"]!.AsArray())
            {
                string name = (string)tool!["name"]!;
                var properties = tool["inputSchema"]!["properties"]!.AsObject();
                if (ExpectedOutputs.TryGetValue(name, out string? expected))
                {
                    Assert.True(properties.ContainsKey(expected), name + ": expected schema parameter " + expected);
                    schemaCovered.Add(name);
                }
                var args = new JsonObject();
                foreach (var property in properties) args[property.Key] = property.Key;
                bool output = false;
                foreach (string action in actions)
                {
                    args["action"] = action;
                    foreach (string target in targets(name, args))
                    {
                        Assert.True(properties.ContainsKey(target), name + ": " + target);
                        if (ExpectedOutputs.TryGetValue(name, out var expectedTarget)) Assert.Equal(expectedTarget, target);
                        covered.Add(name); output = true;
                    }
                }
                if (IsFile(metadata, name)) Assert.True(output || exceptions.Contains(name), name);
            }
        }
        Assert.Equal(ExpectedOutputs.Keys.OrderBy(name => name), schemaCovered.OrderBy(name => name));
        Assert.Equal(56, covered.Count(name => ExpectedOutputs.ContainsKey(name)));
        Assert.Contains("BuildDeviceAmlDocument", covered);
        Assert.Contains("RunOfflineReleaseValidationSuite", covered);
    }

    private static bool IsFile(JsonNode metadata, string name)
    {
        var tools = metadata is JsonArray array ? array : metadata["tools"]!.AsArray();
        return tools.Any(tool => (string?)tool?["name"] == name && (string?)tool?["operation"] == "FILE");
    }

    private static object Arguments(string tool, string parent) => tool switch
    {
        "SaveExportContent" => new { exportId = enginehost::TiaMcp.FoundationHost.SessionExportStore.PutAndSlice("test", "test", "export payload", 4).id,
            outputPath = Path.Combine(parent, "out.txt") },
        "WritePlcSclSourceFile" => new { sclContent = "FUNCTION Test : Void\nEND_FUNCTION", outputPath = Path.Combine(parent, "Test.scl") },
        "GenerateErrorReport" => new { errorCode = "Fixture", summary = "Fixture report", outputDirectory = parent },
        "BuildDeviceAmlDocument" => new { outputPath = Path.Combine(parent, "out.aml"), spec = new { projectName = "Fixture",
            devices = new[] { new { name = "PLC", typeIdentifier = "System:Device.S71500", deviceItems = Array.Empty<object>() } } } },
        _ => new { workspaceRoot = parent, reportDirectory = Path.Combine(parent, "report") }
    };

    private static void Refused(CallToolResult result, string reason, bool rootHint = true)
    {
        var body = result.StructuredContent!;
        Assert.True(result.IsError, body.ToJsonString());
        Assert.True((string?)body["error"]?["code"] == "CONFIRMATION_REQUIRED", body.ToJsonString());
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)body["error"]?["code"]);
        Assert.Equal(reason, (string?)body["error"]?["details"]?["reason"]);
        if (rootHint) Assert.Contains("GetSessionState", (string?)body["error"]?["message"]);
        else Assert.DoesNotContain("skip confirmation", (string?)body["error"]?["message"]);
        Assert.DoesNotContain("cannot write Workbench approval settings", (string?)body["error"]?["message"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }

    private sealed class SettingsScope : IDisposable
    {
        private readonly byte[]? previous = File.Exists(ApprovalSettings.SettingsPath) ? File.ReadAllBytes(ApprovalSettings.SettingsPath) : null;
        internal SettingsScope() => new ApprovalSettings(true, 1).Save(ApprovalSettings.SettingsPath);
        public void Dispose()
        {
            if (previous == null) File.Delete(ApprovalSettings.SettingsPath);
            else File.WriteAllBytes(ApprovalSettings.SettingsPath, previous);
        }
    }

    // Production FILE tools currently have BatchWrite=false. Keep that gate intact and use an explicitly
    // admitted catalog fixture to exercise per-item approval through the real preview/apply entry points.
    private sealed class BatchExportCatalog : Host.IToolCatalogView
    {
        private readonly Host.IToolCatalogView original;
        public IReadOnlyDictionary<string, Host.ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, Host.ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<Host.ToolDescriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities => original.BehaviorCapabilities;
        public Host.ToolDescriptor? Find(string name, bool includeUnavailable = false) => All.GetValueOrDefault(name);
        internal BatchExportCatalog(Host.IToolCatalogView original)
        {
            this.original = original;
            All = original.All.ToDictionary(pair => pair.Key, pair =>
            {
                var descriptor = pair.Value;
                if (pair.Key != "ExportPlcBlocks") return descriptor;
                Assert.False(descriptor.Classification!.BatchWrite);
                var classification = new Host.ToolDescriptorClassification("L2", "PLC-Software", "FILE", false, true);
                return new Host.ToolDescriptor(descriptor.Name, descriptor.RawDescription, descriptor.Tool, classification,
                    descriptor.Signature, descriptor.Parameters, descriptor.DryRun, descriptor.CandidateFamily, descriptor.Execution);
            }, StringComparer.Ordinal);
        }
    }

    private sealed class PreviewWorker(string release = "19") : IFoundationWorker, enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        internal int Previews, Writes;
        internal string ProjectFile => "C:/Fixture.ap" + release;
        public object SessionKey { get; } = new();
        public bool Faulted => false;
        public JsonNode? Binding => Snapshot()["binding"];
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true },
            ["session"] = new JsonObject { ["isConnected"] = true, ["project"] = "Fixture" },
            ["binding"] = new JsonObject { ["identity"] = new JsonObject {
                ["tiaMajorVersion"] = int.Parse(release, System.Globalization.CultureInfo.InvariantCulture), ["processId"] = 123,
                ["processStartUtc"] = "2026-10-09T00:00:00Z", ["projectPath"] = ProjectFile, ["projectName"] = "Fixture", ["generation"] = 1 } } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No restart expected.");
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject args, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No engine write expected.");
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ReleaseKey"] = release, [nameof(ProjectFile)] = ProjectFile, ["ProcessId"] = 123,
                ["IsAttached"] = true, ["OwnsProject"] = false, ["IsLocalSession"] = false,
                ["IdentityStatus"] = "unverified-cached-pid", ["RuntimeConnectionStatus"] = "not-probed" });
            if ((bool?)arguments["dryRun"] == false) Writes++; else Previews++;
            if (operation == "ExportBlocks")
            {
                var result = TiaMcp.Adapters.PlcBatchExportPolicy.Run("blocks", ProjectFile, "PLC", "", false,
                    (string)arguments["exportPath"]!, 128, (bool)arguments["dryRun"]!, (string?)arguments["expectedInventoryHash"] ?? "",
                    new[] { new TiaMcp.Adapters.PlcBatchExportSource { Path = "Block", Consistent = true,
                        Export = _ => throw new InvalidOperationException("No native write expected.") } }, () => { });
                return Task.FromResult(JsonSerializer.SerializeToNode(result));
            }
            if (operation is "project-lifecycle.ArchiveSavedProject" or "hardware-devices.HardwareManageHardwareUtilities")
                return Task.FromResult<JsonNode?>(new JsonObject { ["Message"] = "Archive preview", ["Meta"] = new JsonObject {
                    ["success"] = true, ["operationSuccess"] = true, ["apiCallSuccess"] = true, ["dataComplete"] = true,
                    ["executed"] = false, ["mayHaveWrittenFiles"] = false } });
            return Task.FromResult<JsonNode?>(new JsonObject { ["Value"] = new JsonObject {
                ["Message"] = "fixture", ["Meta"] = new JsonObject { ["success"] = true, ["operationSuccess"] = true,
                    ["apiCallSuccess"] = true, ["dataComplete"] = true, ["verificationSuccess"] = true, ["mayHaveChanged"] = false } } });
        }
        public void Dispose() { }
        private sealed class Lane : IDisposable { public void Dispose() { } }
    }
}
