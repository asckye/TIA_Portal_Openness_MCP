using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;
using Xunit;

public sealed class BatchOverwriteTests : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine("bin-build/P6-67/batch-fixtures", Guid.NewGuid().ToString("N")));
    private readonly List<string> calls = new();
    private PlcBatchImportObject[] inventory = Array.Empty<PlcBatchImportObject>();
    private readonly Dictionary<string, string> oldXml = new();
    public BatchOverwriteTests() { Directory.CreateDirectory(Path.Combine(root, "input")); }
    private PlcBatchImportRequest Request(bool overwrite = true) => new() { Release = "17", Project = "C:/P.ap17", ExpectedProject = "C:/P.ap17", Software = "PLC", Directory = Path.Combine(root, "input"), Program = true, Overwrite = overwrite };
    private void Inputs(params (string name, string kind, string dependencies)[] entries)
    {
        inventory = entries.Select((x, i) => new PlcBatchImportObject { Name = x.name, Kind = x.kind, Number = x.kind == "FC" ? i + 1 : null }).ToArray();
        for (int i = 0; i < entries.Length; i++)
        {
            var x = entries[i]; string xml = BatchImportTests.Xml(x.name, x.kind, inventory[i].Number, x.dependencies);
            File.WriteAllText(Path.Combine(root, "input", i + ".xml"), xml + " "); oldXml[x.name] = xml;
        }
    }
    private PlcBatchImportResult Run(PlcBatchImportRequest r, Func<FileInfo, PlcBatchImportObject, PlcBatchImportObject[]>? import = null, bool backupFailure = false, bool restoreFailure = false, string failedBackup = "", bool directoryFailure = false) => PlcBatchImportPolicy.Run(r, inventory, () => { },
        (file, item) => { calls.Add("import:" + item.Name); return import?.Invoke(file, item) ?? new[] { item }; },
        (item, file) => { calls.Add("backup:" + item.Name); if (backupFailure || item.Name == failedBackup) throw new IOException("export failed"); File.WriteAllText(file.FullName, oldXml[item.Name]); },
        (file, item) => { calls.Add("restore:" + item.Name); if (restoreFailure) throw new IOException("uncertain restore"); Assert.Equal(oldXml[item.Name], File.ReadAllText(file.FullName)); return new[] { item }; },
        () => { if (directoryFailure) throw new IOException("No writable recovery location."); var path = Path.Combine(root, "recovery", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; });
    private PlcBatchImportRequest Apply(PlcBatchImportResult preview)
    { var r = Request(); r.DryRun = false; r.Confirm = true; r.ExpectedHash = preview.PlanHash; r.Order = preview.Items.Select(x => x.RelativePath).ToArray(); return r; }
    private static JsonObject Wire(PlcBatchImportResult result) => JsonSerializer.SerializeToNode(result)!.AsObject();
    private static Envelope Envelope(PlcBatchImportResult result) => PlcBatchImportResultMapping.Result(JsonSerializer.SerializeToNode(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject(), "17", "ImportPlcProgramFromDirectory", "fixture", false);
    [Fact]
    public void Preview_hash_includes_overwrite_and_create_replace_actions()
    {
        Inputs(("A", "FC", "")); var preview = Run(Request()); Assert.Empty(calls); Assert.Equal("replace", preview.Items[0].Action);
        inventory = Array.Empty<PlcBatchImportObject>(); var create = Run(Request()); Assert.Equal("create", create.Items[0].Action); Assert.NotEqual(preview.PlanHash, create.PlanHash);
        Assert.NotEqual(create.PlanHash, Run(Request(false)).PlanHash);
        Assert.Throws< TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => Run(Apply(preview)));
        Assert.Empty(calls);
    }
    [Theory]
    [InlineData(false, "inconsistent")][InlineData(true, "inconsistent")]
    [InlineData(false, "know-how-protected")][InlineData(true, "know-how-protected")]
    public void Replacement_blockers_are_planned_hashed_and_refused_before_backup(bool program, string blocker)
    {
        Inputs(("A", "FC", ""), ("B", "FC", "")); var request = Request(); request.Program = program;
        var ready = Run(request); inventory[1].BackupBlocker = blocker;
        var preview = Run(request); Assert.Empty(calls); Assert.NotEqual(ready.PlanHash, preview.PlanHash);
        Assert.Equal("replace-blocked: " + blocker, preview.Items[1].Action);
        BatchImportContract.Validate(Wire(preview), true);
        var envelope = Envelope(preview); Assert.Equal(ErrorCode.PreconditionFailed, envelope.Error!.Code);
        Assert.Equal(Execution.NotStarted, envelope.Meta.Execution); Assert.Contains("B", envelope.Error.Message);
        Assert.Contains("CompilePlcSoftware", envelope.Error.Message);
        var apply = Apply(preview); apply.Program = program;
        var rejected = Run(apply); Assert.Empty(calls); Assert.False(rejected.Executed);
        BatchImportContract.Validate(Wire(rejected), false); Assert.Equal(envelope.Error.Message, Envelope(rejected).Error!.Message);
        inventory[1].BackupBlocker = "";
        Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => Run(apply)); Assert.Empty(calls);
    }
    [Fact]
    public void All_backups_precede_first_import_and_are_hashed()
    {
        Inputs(("A", "FC", ""), ("B", "FC", "")); var result = Run(Apply(Run(Request())));
        Assert.Equal(new[] { "backup:A", "backup:B", "import:A", "import:B" }, calls);
        Assert.All(result.Items, x => Assert.Equal(PlcImportSession.ByteHash(File.ReadAllBytes(x.RecoveryPath)), x.RecoverySha256));
        Assert.False(result.RequiresSessionReset); BatchImportContract.Validate(Wire(result), false);
    }
    [Fact]
    public void Middle_known_failure_restores_earlier_replacements_in_original_dependency_order()
    {
        Inputs(("Dependent", "UDT", "<Member Datatype=\"&quot;Base&quot;\"/>"), ("Base", "UDT", ""), ("Last", "FC", ""));
        var result = Run(Apply(Run(Request())), (_, item) => item.Name == "Last" ? throw new PlcBatchImportKnownFailure("known rejection") : new[] { item });
        Assert.Equal(new[] { "restore:Base", "restore:Last", "restore:Dependent" }, calls.Where(x => x.StartsWith("restore:")));
        Assert.Equal(3, result.Restored.Length); Assert.Empty(result.RemainingChanged); Assert.False(result.NativeOutcomeUnknown);
        Assert.False(result.RequiresSessionReset); BatchImportContract.Validate(Wire(result), false);
        Assert.Empty(result.Imported); Assert.Equal(Outcome.Failed, Envelope(result).Meta.Outcome);
        Assert.All(JsonNode.Parse(Envelope(result).Data!.Value.GetRawText())!["items"]!.AsArray(), x => Assert.NotEqual("succeeded", (string?)x!["result"]!["meta"]!["outcome"]));
    }
    [Fact]
    public void Backup_failure_prevents_every_import_and_reports_retained_directory()
    {
        Inputs(("A", "FC", "")); var result = Run(Apply(Run(Request())), backupFailure: true);
        Assert.Equal(new[] { "backup:A" }, calls); Assert.All(result.Items, x => Assert.False(x.Attempted));
        Assert.NotEmpty(result.RecoveryDirectory); Assert.False(result.RequiresSessionReset);
        BatchImportContract.Validate(Wire(result), false); Assert.Equal(Outcome.RejectedBeforeOperation, Envelope(result).Meta.Outcome);
    }
    [Fact]
    public void Backup_failure_is_attributed_to_the_actual_replacement_and_directory_failure_is_a_refusal()
    {
        Inputs(("A", "FC", ""), ("B", "FC", "")); var apply = Apply(Run(Request()));
        var failed = Run(apply, failedBackup: "B"); Assert.Equal("B", Assert.Single(failed.Items.Where(x => x.Status == "failed")).Planned.Name);
        Assert.All(failed.Items, x => Assert.False(x.Attempted)); Assert.DoesNotContain(calls, x => x.StartsWith("import:"));
        BatchImportContract.Validate(Wire(failed), false); Assert.IsType<PreconditionFailedDetails>(Envelope(failed).Error!.Details);
        var directory = Run(apply, directoryFailure: true); Assert.False(directory.RequiresSessionReset);
        Assert.IsType<PreconditionFailedDetails>(Envelope(directory).Error!.Details); Assert.Equal(Execution.NotStarted, Envelope(directory).Meta.Execution);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Unknown_import_or_restore_retains_recovery_evidence_and_never_claims_success(bool restoreFailure)
    {
        Inputs(("A", "FC", ""), ("B", "FC", ""), ("C", "FC", ""));
        var result = Run(Apply(Run(Request())), (_, item) => item.Name == "B" ? restoreFailure ? throw new PlcBatchImportKnownFailure("known rejection") : throw new IOException("unknown native outcome") : new[] { item }, restoreFailure: restoreFailure);
        Assert.DoesNotContain("import:C", calls); Assert.True(result.NativeOutcomeUnknown); Assert.True(result.RequiresSessionReset);
        Assert.All(result.Items, x => Assert.True(File.Exists(x.RecoveryPath))); Assert.NotEmpty(result.RemainingChanged);
        BatchImportContract.Validate(Wire(result), false); var envelope = Envelope(result);
        Assert.Equal(Outcome.Unknown, envelope.Meta.Outcome); Assert.IsType<OutcomeUnknownDetails>(envelope.Error!.Details);
        Assert.Equal(result.RecoveryDirectory, ((OutcomeUnknownDetails)envelope.Error.Details).Evidence["recoveryDirectory"].GetString());
    }
    [Fact]
    public void Failed_restoration_does_not_restore_a_consumer_against_an_unverified_dependency()
    {
        Inputs(("Dependent", "UDT", "<Member Datatype=\"&quot;Base&quot;\"/>"), ("Base", "UDT", ""), ("Last", "FC", ""));
        var result = Run(Apply(Run(Request())), (_, item) => item.Name == "Last" ? throw new PlcBatchImportKnownFailure("known rejection") : new[] { item }, restoreFailure: true);
        Assert.DoesNotContain("restore:Dependent", calls);
        Assert.Equal("not-attempted-dependency-unverified", result.Items.Single(x => x.Planned.Name == "Dependent").RestoreStatus);
        Assert.True(result.NativeOutcomeUnknown); BatchImportContract.Validate(Wire(result), false);
    }
    [Fact]
    public void Mixed_create_replace_failure_reports_new_objects_remaining_after_originals_are_restored()
    {
        Inputs(("New", "FC", ""), ("Old", "FC", ""), ("Last", "FC", "")); inventory = inventory.Where(x => x.Name != "New").ToArray();
        var result = Run(Apply(Run(Request())), (_, item) => item.Name == "Last" ? throw new PlcBatchImportKnownFailure("known rejection") : new[] { item });
        Assert.Equal(new[] { "New" }, result.RemainingChanged.Select(x => x.Name));
        Assert.Equal(new[] { "Old", "Last" }, result.Restored.Select(x => x.Name));
        Assert.DoesNotContain("backup:New", calls); Assert.DoesNotContain("restore:New", calls);
        Assert.False(result.RequiresSessionReset); BatchImportContract.Validate(Wire(result), false);
        var wire = Wire(result); wire["Items"]![1]!["RecoveryPath"] = Path.Combine(root, "outside.xml");
        Assert.Throws<InvalidDataException>(() => BatchImportContract.Validate(wire, false));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
