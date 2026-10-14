using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class WorkerSessionCallbackTests
{
    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Lifecycle_callbacks_capture_full_identity_and_release_without_rebinding(string release)
    {
        var engine = Create(release);
        engine.ConfigureFixture();
        engine.ProjectConnectionCreated(engine.Connection);
        engine.ClearCalls();
        engine.ProjectRetrieved(engine.NewProject("ActualName"));
        Assert.Equal(new[] { "Binding.Capture", "project.Path", "project.Name" }, (string[])engine.NativeCalls);
        engine.ClearCalls();
        var binding = System.Text.Json.JsonSerializer.SerializeToNode(engine.ProjectCachedBinding())!;
        Assert.Equal(TiaMcp.Versioning.TiaVersionCatalog.Get(release).MajorVersion, (int?)binding["identity"]?["tiaMajorVersion"]);
        Assert.Equal(Environment.ProcessId, (int?)binding["identity"]?["processId"]);
        Assert.Equal("ActualName", (string?)binding["identity"]?["projectName"]);
        Assert.False((bool?)binding["verifiedLive"]);
        Assert.True((bool?)binding["instanceReserved"]);
        Assert.Null(binding["fault"]);
        Assert.NotNull(binding["note"]);
        Assert.Equal("ActualName", (string?)engine.ProjectExpectedProjectName());
        Assert.Equal(binding.ToJsonString(), System.Text.Json.JsonSerializer.SerializeToNode(engine.HmiBindingIdentity())!.ToJsonString());
        Assert.Empty((string[])engine.NativeCalls);
        string generation = (string)binding["identity"]!["generation"]!;
        engine.RefreshWorkerSessionBinding();
        Assert.Equal(generation, (string?)System.Text.Json.JsonSerializer.SerializeToNode(engine.ProjectCachedBinding())!["identity"]?["generation"]);
        engine.ReplaceProjectHandle(engine.NewProject("ActualName"));
        engine.ClearCalls();
        engine.RefreshWorkerSessionBinding();
        Assert.Equal(generation, (string?)System.Text.Json.JsonSerializer.SerializeToNode(engine.ProjectCachedBinding())!["identity"]?["generation"]);
        Assert.Empty((string[])engine.NativeCalls);
        engine.RenameProject("Copy");
        engine.ProjectSavedAs();
        Assert.Equal("Copy", (string?)engine.ProjectExpectedProjectName());
        Assert.NotEqual(generation, (string?)System.Text.Json.JsonSerializer.SerializeToNode(engine.ProjectCachedBinding())!["identity"]?["generation"]);
        engine.ProjectReleased();
        Assert.Null(System.Text.Json.JsonSerializer.SerializeToNode(engine.ProjectCachedBinding())!["identity"]);
        Assert.Null((string?)engine.ProjectExpectedProjectName());
        Assert.True((bool)engine.EngineeringProjectMissing());
    }

    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Safety_callbacks_check_exact_identity_and_reuse_ambient_access(string release)
    {
        var disconnected = Create(release);
        disconnected.ConfigureFixture();
        Assert.True((bool)disconnected.EngineeringProjectMissing());
        Assert.Equal(string.Empty, (string)disconnected.ProjectValidationTree());
        var missing = Assert.ThrowsAny<Exception>(() => disconnected.AcquireEngineeringEditAccess());
        Assert.Equal("TIA session unavailable.", missing.Message);
        var engine = Bound(release);
        engine.ClearCalls();
        Assert.False((bool)engine.EngineeringProjectMissing());
        Assert.Equal(new[] { "Binding.ProjectPath", "project.Path", "Binding.ProjectName", "project.Name" }, (string[])engine.NativeCalls);
        engine.ClearCalls();
        Assert.Equal("adapter:ActualName", (string)engine.ProjectValidationTree());
        Assert.Equal(new[] { "Binding.ProjectPath", "project.Path", "Binding.ProjectName", "project.Name", "tree.Read", "project.Name" }, (string[])engine.NativeCalls);
        using ((IDisposable)engine.AcquireEngineeringEditAccess()) { }
        Assert.Equal(1, (int)engine.ExclusiveCalls);
        engine.HasEngineeringTransaction = true;
        using ((IDisposable)engine.AcquireEngineeringEditAccess()) { }
        Assert.Equal(1, (int)engine.ExclusiveCalls);
        engine.SharedFamilyMutationIdentity((string)engine.ProjectFile);
        Assert.Throws<ArgumentException>(() => { engine.SharedFamilyMutationIdentity("C:/fixture/other.ap" + release); });
        engine.LocalSession = true;
        Assert.Throws<AdapterPreconditionException>(() => { engine.DefaultLocalSessionGuard(); });
        engine.SharedFamilyMutationIdentity((string)engine.ProjectFile);
        var a = (TiaMcp.Adapters.Contracts.Candidates.CandidateIdentity)engine.SharedHardwareCandidateIdentity((Func<string>)(() => engine.ProjectFile));
        var b = (TiaMcp.Adapters.Contracts.Candidates.CandidateIdentity)engine.SharedHardwareCandidateIdentity((Func<string>)(() => engine.ProjectFile));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b));
        engine.RenameProject("Copy");
        engine.ProjectSavedAs();
        var copied = (TiaMcp.Adapters.Contracts.Candidates.CandidateIdentity)engine.SharedHardwareCandidateIdentity((Func<string>)(() => engine.ProjectFile));
        Assert.Equal(b.BindingEpoch + 1, copied.BindingEpoch);
        Assert.NotEqual(b.ProjectFile, copied.ProjectFile);
        engine.RenameProject("Unexpected");
        var failure = Assert.ThrowsAny<Exception>(() => engine.EngineeringProjectMissing());
        Assert.Contains("exact TIA process/project binding changed", failure.Message);
        Assert.NotNull(System.Text.Json.JsonSerializer.SerializeToNode(engine.ProjectCachedBinding())!["fault"]);
        Assert.ThrowsAny<Exception>(() => engine.ProjectValidationTree());
        Assert.ThrowsAny<Exception>(() => engine.AcquireEngineeringEditAccess());
        Assert.Equal(1, (int)engine.ExclusiveCalls);
    }

    [Theory]
    [InlineData("20", "tagTables", "ImportHmiTagTablesFromDirectory")]
    [InlineData("21", "tagTables", "ImportHmiTagTablesFromDirectory")]
    [InlineData("20", "screens", "ImportHmiScreensFromDirectory")]
    [InlineData("21", "screens", "ImportHmiScreensFromDirectory")]
    public void Seed_binding_preserves_request_reply_and_failure(string release, string kind, string operation)
    {
        var engine = Bound(release);
        var request = new SeedHmiRequest { Kind = kind, SoftwarePath = "HMI", FolderPath = "Folder", Directory = "C:/fixture/import" };
        PlcSeedHmiReply reply = engine.SeedReferenceHmiImport(request);
        Assert.Equal(new[] { "imported" }, reply.Imported);
        Assert.Equal("failed", reply.Failed!.Single().Path);
        HmiRequest sent = engine.LastHmiRequest;
        Assert.Equal(operation, sent.Operation);
        Assert.Equal(request.SoftwarePath, sent.SoftwarePath);
        Assert.Equal(request.FolderPath, sent.FolderPath);
        Assert.Equal(request.Directory, sent.Dir);
        Assert.True(sent.Overwrite);
        Assert.False(sent.DryRun);
        Assert.False((bool)engine.LastHmiDryRun);
        engine.HmiFailure = new PlcSoftwareFailure { Code = "NotFound", Message = "Seed fixture refusal." };
        var error = Assert.Throws<PlcSoftwareException>(() => engine.SeedReferenceHmiImport(request));
        Assert.Equal("NotFound", error.Code);
        Assert.Equal("Seed fixture refusal.", error.Message);
        request.Kind = "unsupported";
        Assert.Throws<ArgumentException>(() => engine.SeedReferenceHmiImport(request));
    }

    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Lock_clears_cached_binding_and_cannot_be_undone_by_refresh(string release)
    {
        var engine = Bound(release);
        engine.LockWorkerSessionBinding("Native outcome unknown.");
        engine.RefreshWorkerSessionBinding();
        var snapshot = System.Text.Json.JsonSerializer.SerializeToNode(engine.ProjectCachedBinding())!;
        Assert.Null(snapshot["identity"]);
        Assert.Equal("Native outcome unknown.", (string?)snapshot["fault"]);
        Assert.ThrowsAny<Exception>(() => engine.EngineeringProjectMissing());
        engine.ClearWorkerSessionBinding();
        engine.RefreshWorkerSessionBinding();
        Assert.Null(System.Text.Json.JsonSerializer.SerializeToNode(engine.ProjectCachedBinding())!["fault"]);
    }

    private static PlcFoundationEngine Create(string release) => new(release);

    public static IEnumerable<object[]> Releases()
        => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(key => new object[] { key });

    public static IEnumerable<object[]> IdentityChanges()
        => Releases().SelectMany(row => new[] { "pid", "start", "path", "name" }.Select(field => new object[] { row[0], field }));

    [Theory, MemberData(nameof(Releases))]
    public void Attached_device_identity_uses_exact_binding_and_allows_local_sessions(string release)
    {
        var engine = Bound(release);
        engine.LocalSession = false;
        var binding = System.Text.Json.JsonSerializer.SerializeToNode(engine.ProjectCachedBinding())!["identity"]!;
        var ordinary = engine.SharedHardwareCandidateIdentity!(() => engine.ProjectFile);
        Assert.Equal(Environment.ProcessId, ordinary.ProcessId);
        Assert.Equal(engine.Ticks, ordinary.ProcessStartUtc!.Value.UtcTicks);
        Assert.Equal("C:\\FIXTURE\\P.AP" + PlcLifecyclePolicy.FileSuffix(release).ToUpperInvariant(), ordinary.ProjectFile);
        Assert.Equal("ActualName", (string?)binding["projectName"]);
        Assert.True(ordinary.BindingEpoch > 0);
        engine.LocalSession = true;
        var local = engine.SharedHardwareCandidateIdentity!(() => engine.ProjectFile);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(ordinary), System.Text.Json.JsonSerializer.Serialize(local));
        Assert.Throws<ArgumentException>(() => engine.SharedHardwareCandidateIdentity!(() => "C:/fixture/other.ap" + release));
    }

    [Theory, MemberData(nameof(IdentityChanges))]
    public void Exact_identity_core_refuses_changed_pid_start_path_or_actual_name(string release, string field)
    {
        int major = TiaMcp.Versioning.TiaVersionCatalog.Get(release).MajorVersion;
        int pid = Environment.ProcessId;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        long ticks = process.StartTime.ToUniversalTime().Ticks;
        var identity = new TiaMcpServer.Siemens.ProjectBindingIdentity(major, pid, ticks, "C:/fixture/P.ap" + release, "ActualName");
        string path = field == "path" ? "C:/fixture/other.ap" + release : identity.ProjectPath;
        string name = field == "name" ? "FileNameIsNotTheActualName" : identity.ProjectName;
        var error = Assert.Throws<InvalidOperationException>(() => TiaMcp.Adapters.Session.SessionCallbackPrimitives.Verify(
            true, major, field == "pid" ? pid + 1 : pid, true, () => path, () => name, identity.Verify,
            _ => field == "start" ? ticks + 1 : ticks));
        Assert.Contains("exact TIA process/project binding changed", error.Message);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Candidate_callback_refuses_an_actual_name_change_without_save_as(string release)
    {
        var engine = Bound(release);
        engine.SharedHardwareCandidateIdentity!(() => engine.ProjectFile);
        engine.RenameProjectOnly("Unexpected");
        var error = Assert.ThrowsAny<Exception>(() => engine.SharedHardwareCandidateIdentity!(() => engine.ProjectFile));
        Assert.Contains("exact TIA process/project binding changed", error.Message);
        Assert.NotNull(System.Text.Json.JsonSerializer.SerializeToNode(engine.ProjectCachedBinding())!["fault"]);
    }

    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19")]
    public void Old_release_seed_registration_stays_unavailable(string release)
    {
        var engine = Bound(release);
        Assert.Null(engine.SeedReferenceHmiImport);
        Assert.Null(engine.LastHmiRequest);
    }

    private static PlcFoundationEngine Bound(string release)
    {
        var engine = Create(release);
        engine.ConfigureFixture();
        engine.ProjectConnectionCreated(engine.Connection);
        engine.ProjectRetrieved(engine.NewProject("ActualName"));
        return engine;
    }

}
