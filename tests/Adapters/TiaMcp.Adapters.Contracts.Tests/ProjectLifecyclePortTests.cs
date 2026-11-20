using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class ProjectLifecyclePortTests
{
    private static (TiaMcp.Adapters.PlcFoundationEngine Engine, Siemens.Engineering.Project Project) CloseFixture(bool modified,
        bool borrowedProject = false, bool borrowedPortal = false)
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine("19");
        if (borrowedPortal) engine.ReattachForTest(Environment.ProcessId);
        else engine.ProjectConnectionCreated!(engine.Connection);
        var project = engine.NewProject("P");
        project.IsModified = modified;
        engine.Connection.Projects.Add(project);
        project.OnClose = () => engine.Connection.Projects.Remove(project);
        if (borrowedProject) engine.BorrowPrimaryForTest(project);
        else engine.ProjectRetrieved!(project);
        return (engine, project);
    }

    private static System.Text.Json.Nodes.JsonObject ClosePreview(TiaMcp.Adapters.PlcFoundationEngine engine, bool discard = true)
        => System.Text.Json.Nodes.JsonNode.Parse(TiaMcp.PlcWorker.WorkerJson.Serialize(
            engine.CloseProject(discardChanges: discard, bindingOwnedByCaller: true)))!.AsObject();

    [Fact]
    public void Ordinary_close_preserves_unsaved_changes_and_discard_requires_the_binding_owner()
    {
        var (engine, project) = CloseFixture(true);
        Assert.Throws<AdapterPreconditionException>(() => engine.CloseProject());
        Assert.Throws<AdapterPreconditionException>(() => engine.CloseProject(false, bindingOwnedByCaller: true,
            confirm: true, expectedProjectFile: project.Path.FullName));
        Assert.Throws<AdapterPreconditionException>(() => engine.CloseProject(discardChanges: true));
        Assert.Equal(0, project.CloseCalls);
        Assert.Equal(0, project.SaveCalls);
        Assert.Equal(project.Path.FullName, engine.ProjectFile);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Reviewed_discard_closes_once_and_releases_the_binding_without_saving(bool modified)
    {
        var (engine, project) = CloseFixture(modified);
        var preview = ClosePreview(engine);
        string file = project.Path.FullName, hash = (string)preview["PlanHash"]!;
        Assert.Equal(64, hash.Length);
        Assert.Equal(modified, (bool?)preview["IsModified"]);
        Assert.Equal(file, (string?)preview["ProjectFile"]);
        Assert.False((bool?)preview["Executed"]);
        Assert.True((bool?)preview["DiscardChanges"]);
        Assert.Equal(0, project.CloseCalls);
        var result = engine.CloseProject(false, discardChanges: true, expectedPlanHash: hash, bindingOwnedByCaller: true,
            confirm: true, expectedProjectFile: file);
        Assert.True(result.Executed);
        Assert.Equal(file, result.ProjectFile);
        Assert.Equal(1, project.CloseCalls);
        Assert.Equal(0, project.SaveCalls);
        Assert.Empty(engine.Connection.Projects);
        Assert.Null(engine.ReadState().ProjectFile);
    }

    [Theory, InlineData("borrowed-project"), InlineData("attached-portal"), InlineData("different-session"), InlineData("local-session"),
        InlineData("secondary")]
    public void Discard_refuses_ineligible_targets_before_preview_or_apply(string target)
    {
        var (engine, project) = CloseFixture(true, target == "borrowed-project", target == "attached-portal");
        if (target == "local-session") engine.LocalSession = true;
        foreach (bool preview in new[] { true, false })
            Assert.Throws<AdapterPreconditionException>(() => engine.CloseProject(preview, target == "secondary" ? project.Path.FullName : "",
                true, "unused", target != "different-session", true, project.Path.FullName));
        Assert.Equal(0, project.CloseCalls);
        Assert.Equal(0, project.SaveCalls);
        Assert.Equal(project.Path.FullName, engine.ProjectFile);
    }

    [Theory, InlineData("modified-after-preview"), InlineData("clean-after-preview"), InlineData("other-file"), InlineData("ordinary-preview"),
        InlineData("no-confirm"), InlineData("wrong-expected-file"), InlineData("missing-hash")]
    public void Discard_rechecks_the_reviewed_identity_and_state_before_close(string change)
    {
        var (engine, project) = CloseFixture(change == "clean-after-preview");
        string file = project.Path.FullName;
        var preview = ClosePreview(engine, change != "ordinary-preview");
        if (change == "modified-after-preview") project.IsModified = true;
        if (change == "clean-after-preview") project.IsModified = false;
        if (change == "other-file") project.Path = new FileInfo(@"C:\fixture\Other.ap19");
        string expectedFile = change == "wrong-expected-file" ? @"C:\fixture\Other.ap19" : project.Path.FullName;
        var refused = Assert.Throws<AdapterPreconditionException>(() => engine.CloseProject(false, discardChanges: true,
            expectedPlanHash: change == "missing-hash" ? "" : (string)preview["PlanHash"]!, bindingOwnedByCaller: true,
            confirm: change != "no-confirm", expectedProjectFile: expectedFile));
        Assert.Equal(change switch { "no-confirm" => "confirm", "wrong-expected-file" => "expectedProjectFile", _ => "expectedPlanHash" },
            refused.ParamName);
        Assert.Equal(0, project.CloseCalls);
        Assert.Equal(file, engine.ProjectFile);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Discard_needs_post_close_absence_before_reporting_success(bool unreadable)
    {
        var (engine, project) = CloseFixture(true);
        string hash = (string)ClosePreview(engine)["PlanHash"]!;
        project.OnClose = () => { if (unreadable) engine.Connection.EngineeringCall = () => throw new IOException("Cannot enumerate projects."); };
        Assert.Throws<IOException>(() => engine.CloseProject(false, discardChanges: true, expectedPlanHash: hash,
            bindingOwnedByCaller: true, confirm: true, expectedProjectFile: project.Path.FullName));
        Assert.Equal(1, project.CloseCalls);
        Assert.Equal(project.Path.FullName, engine.ProjectFile);
    }

    public static IEnumerable<object[]> SecondaryReleases()
        => new[] { "15.1", "16", "17", "18", "19", "20", "21" }.Select(release => new object[] { release });

    [Theory, MemberData(nameof(SecondaryReleases))]
    public void Secondary_close_identity_is_the_reference_file_and_never_uses_the_primary_target(string release)
    {
        string suffix = TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release);
        string primary = @"C:\Projects\Primary.ap" + suffix, reference = @"C:\Projects\Reference.ap" + suffix;
        int primaryChecks = 0;
        void Bound(string file) { primaryChecks++; Assert.Equal(primary, file); }
        TiaMcp.Adapters.MutationIdentityPolicy.ValidateTarget(false, true, reference, "CloseProject", release, "", "", "", Bound, reference);
        Assert.Equal(0, primaryChecks);
        Assert.Throws<ArgumentException>(() => TiaMcp.Adapters.MutationIdentityPolicy.ValidateTarget(false, true, primary, "CloseProject", release,
            "", "", "", Bound, reference));
        TiaMcp.Adapters.MutationIdentityPolicy.ValidateTarget(false, true, primary, "CloseProject", release, "", "", "", Bound);
        Assert.Equal(1, primaryChecks);
    }

    [Theory, MemberData(nameof(SecondaryReleases))]
    public void Secondary_requires_a_bound_primary_project_before_opening(string release)
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine(release);
        engine.ProjectConnectionCreated!(engine.Connection);
        Assert.Throws<AdapterPreconditionException>(() => engine.OpenSecondaryForTest(@"C:\missing.ap21"));
        var primary = engine.NewProject("Primary");
        engine.ProjectRetrieved!(primary);
        engine.LocalSession = true;
        Assert.Throws<AdapterPreconditionException>(() => engine.OpenSecondaryForTest(@"C:\missing.ap21"));
        engine.LocalSession = false;
        primary.IsPrimary = false;
        Assert.Throws<AdapterPreconditionException>(() => engine.OpenSecondaryForTest(@"C:\missing.ap21"));
        Assert.Equal(0, engine.Connection.Projects.OpenCalls);
        Assert.Null(engine.SecondaryFileForTest);
    }

    [Theory, MemberData(nameof(SecondaryReleases))]
    public void Secondary_preview_execute_and_explicit_close_preserve_the_primary_binding(string release)
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine(release);
        engine.ProjectConnectionCreated!(engine.Connection);
        var primary = engine.NewProject("Primary");
        engine.BorrowPrimaryForTest(primary);
        string primaryFile = engine.ProjectFile;
        string root = Path.Combine(AppContext.BaseDirectory, "secondary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "Reference.ap" + TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release));
        File.WriteAllBytes(file, Array.Empty<byte>());
        try
        {
            var preview = engine.OpenSecondaryForTest(file);
            Assert.False(preview.Executed);
            Assert.Equal(file, preview.ProjectFile);
            Assert.Equal(0, engine.Connection.Projects.OpenCalls);
            Assert.Null(engine.SecondaryFileForTest);
            var opened = engine.OpenSecondaryForTest(file, false);
            Assert.True(opened.Executed);
            Assert.Equal(1, engine.Connection.Projects.OpenCalls);
            Assert.Equal(Siemens.Engineering.ProjectOpenMode.Secondary, engine.Connection.Projects.LastMode);
            var secondary = Assert.Single(engine.Connection.Projects);
            Assert.False(secondary.IsPrimary);
            Assert.Equal(file, engine.SecondaryFileForTest);
            Assert.Throws<AdapterPreconditionException>(() => engine.RequirePrimaryCloseForTest());
            Assert.Throws<AdapterPreconditionException>(() => engine.OpenSecondaryForTest(file, false));
            Assert.Throws<AdapterPreconditionException>(() => engine.CloseSecondaryForTest(primaryFile, false));
            Assert.False(engine.CloseSecondaryForTest(file).Executed);
            Assert.Equal(0, secondary.CloseCalls);
            Assert.True(engine.CloseSecondaryForTest(file, false).Executed);
            Assert.Equal(1, secondary.CloseCalls);
            Assert.Equal(0, secondary.SaveCalls);
            Assert.Null(engine.SecondaryFileForTest);
            engine.RequirePrimaryCloseForTest();
            Assert.Equal(primaryFile, engine.ProjectFile);
            Assert.False(engine.ReadState().OwnsProject);
            Assert.Equal(0, primary.CloseCalls);
            Assert.Equal(0, primary.SaveCalls);
            Assert.Throws<AdapterPreconditionException>(() => engine.CloseSecondaryForTest(file, false));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory, MemberData(nameof(SecondaryReleases))]
    public void Primary_result_from_secondary_open_leaves_state_unchanged_and_closes_nothing(string release)
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine(release);
        engine.ProjectConnectionCreated!(engine.Connection);
        engine.BorrowPrimaryForTest(engine.NewProject("Primary"));
        string primary = engine.ProjectFile;
        string file = Path.Combine(AppContext.BaseDirectory, "Unexpected.ap" + TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release));
        File.WriteAllBytes(file, Array.Empty<byte>());
        try
        {
            engine.Connection.Projects.NextIsPrimary = true;
            Assert.Throws<InvalidOperationException>(() => engine.OpenSecondaryForTest(file, false));
            Assert.Null(engine.SecondaryFileForTest);
            Assert.Equal(primary, engine.ProjectFile);
            Assert.Equal(0, Assert.Single(engine.Connection.Projects).CloseCalls);
        }
        finally { File.Delete(file); }
    }

    [Theory, MemberData(nameof(SecondaryReleases))]
    public void Binding_refuses_secondary_and_list_projects_reports_primary_flags(string release)
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine(release);
        engine.ProjectConnectionCreated!(engine.Connection);
        var primary = engine.NewProject("Line1");
        var secondary = new Siemens.Engineering.Project { Name = "Line1", IsPrimary = false,
            Path = new FileInfo(Path.Combine(AppContext.BaseDirectory, "Reference", "Line1.ap" + TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release))) };
        engine.Connection.Projects.AddRange(new[] { primary, secondary });
        var fault = new Dictionary<string, object?> { ["status"] = "unchanged" };
        engine.BindingHmiFaultForTest = fault;
        var error = Assert.Throws<AdapterPreconditionException>(() => engine.BindProject("Line1", secondary.Path.FullName));
        Assert.Contains("primary", error.Message);
        Assert.Null(engine.ReadState().ProjectFile);
        Assert.Same(fault, engine.BindingHmiFaultForTest);
        engine.BindProject("Line1", primary.Path.FullName);
        Assert.Equal(primary.Path.FullName, engine.ProjectFile);
        var rows = engine.ListProjects();
        Assert.True((bool)((Dictionary<string, object>)rows[0].Meta!)["isPrimary"]);
        Assert.False((bool)((Dictionary<string, object>)rows[1].Meta!)["isPrimary"]);
        Assert.Throws<InvalidOperationException>(() => TiaOpenness.Openness.OpennessSession.ReuseForTest(secondary));
        Assert.Same(primary, TiaOpenness.Openness.OpennessSession.AutoBindForTest(engine.Connection));
        engine.Connection.Projects.Remove(primary);
        Assert.Null(TiaOpenness.Openness.OpennessSession.AutoBindForTest(engine.Connection));
    }

    [Theory, MemberData(nameof(SecondaryReleases))]
    public void Same_name_comparison_resolves_the_held_secondary_by_file_and_keeps_other_name_ambiguity(string release)
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine(release);
        engine.ProjectConnectionCreated!(engine.Connection);
        var primary = engine.NewProject("Line1");
        engine.Connection.Projects.Add(primary);
        engine.BorrowPrimaryForTest(primary);
        string root = Path.Combine(AppContext.BaseDirectory, "comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "Line1.ap" + TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release));
        File.WriteAllBytes(file, Array.Empty<byte>());
        try
        {
            engine.OpenSecondaryForTest(file, false);
            var reference = engine.Connection.Projects.Single(p => !p.IsPrimary);
            Assert.Same(reference, engine.ExactProjectForTest("Line1"));
            Assert.Same(reference, engine.ExactProjectForTest(file));
            Assert.Equal(primary.Path.FullName, engine.ProjectFile);
            Assert.Throws<ArgumentException>(() => TiaMcp.Adapters.MutationIdentityPolicy.ValidateTarget(false, true, file,
                "SaveProject", release, "", "", "", expected => TiaMcp.Adapters.MutationIdentityPolicy.RequireSameProject(expected, engine.ProjectFile)));
            var rebound = new TiaMcp.Adapters.PlcFoundationEngine(release);
            rebound.ProjectConnectionCreated!(engine.Connection);
            Assert.Same(reference, rebound.ExactProjectForTest(file));
            Assert.Throws<InvalidOperationException>(() => rebound.ExactProjectForTest("Line1"));
            Assert.Throws<AdapterPreconditionException>(() => rebound.CloseSecondaryForTest(primary.Path.FullName, false));
            Assert.Throws<AdapterPreconditionException>(() => rebound.CloseSecondaryForTest(file, false));
            Assert.Equal(0, reference.CloseCalls);
            rebound.BindProject("Line1", primary.Path.FullName, file, Environment.ProcessId,
                System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
            Assert.Same(reference, rebound.ExactProjectForTest("Line1"));
            Assert.False(rebound.CloseSecondaryForTest(file).Executed);
            rebound.CloseSecondaryForTest(file, false);
            Assert.Equal(1, reference.CloseCalls);
            Assert.Equal(0, primary.CloseCalls);
            Assert.Equal(file, engine.SecondaryFileForTest);
            var absent = engine.CloseSecondaryForTest(file);
            Assert.True((bool)absent.GetType().GetProperty("SecondaryProjectGone")!.GetValue(absent)!);
            Assert.False(absent.Executed);
            using var wire = System.Text.Json.JsonDocument.Parse(TiaMcp.PlcWorker.WorkerJson.Serialize(absent));
            Assert.True(wire.RootElement.GetProperty("SecondaryProjectGone").GetBoolean());
            Assert.False(wire.RootElement.GetProperty("Executed").GetBoolean());
            Assert.Null(engine.SecondaryFileForTest);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory, MemberData(nameof(SecondaryReleases))]
    public void Secondary_absence_on_another_process_keeps_the_record(string release)
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine(release);
        engine.ProjectConnectionCreated!(engine.Connection);
        var primary = engine.NewProject("Primary");
        engine.Connection.Projects.Add(primary);
        string file = @"C:\Reference\Line1.ap" + TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release);
        long start = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        engine.BindProject("Primary", primary.Path.FullName, file, Environment.ProcessId, start);
        engine.ReattachForTest(Environment.ProcessId + 1);
        var refused = Assert.Throws<AdapterPreconditionException>(() => engine.CloseSecondaryForTest(file, false));
        Assert.Contains("TIA PID " + Environment.ProcessId, refused.Message);
        Assert.Contains(new DateTime(start, DateTimeKind.Utc).ToString("O"), refused.Message);
        Assert.Equal(file, engine.SecondaryFileForTest);
        engine.ReattachForTest(Environment.ProcessId);
        var gone = engine.CloseSecondaryForTest(file, false);
        Assert.False(gone.Executed);
        Assert.True((bool)gone.GetType().GetProperty("SecondaryProjectGone")!.GetValue(gone)!);
        Assert.Null(engine.SecondaryFileForTest);
        Assert.Equal(0, primary.CloseCalls);
    }

    [Theory, MemberData(nameof(SecondaryReleases))]
    public void Reconnected_holder_restores_reference_without_primary_binding(string release)
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine(release);
        engine.ProjectConnectionCreated!(engine.Connection);
        string file = @"C:\Reference\Line1.ap" + TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release);
        long start = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        engine.ReattachForTest(Environment.ProcessId, file, Environment.ProcessId, start);
        Assert.Null(engine.ReadState().ProjectFile);
        var gone = engine.CloseSecondaryForTest(file, false);
        Assert.False(gone.Executed);
        Assert.True((bool)gone.GetType().GetProperty("SecondaryProjectGone")!.GetValue(gone)!);
        Assert.Null(engine.SecondaryFileForTest);
    }

    public static IEnumerable<object[]> SecondaryGoneHolders()
        => SecondaryReleases().SelectMany(row => new[] { false, true }.Select(reused => new object[] { row[0], reused }));

    [Theory, MemberData(nameof(SecondaryGoneHolders))]
    public void Gone_or_replaced_holder_clears_native_record_without_closing_any_project(string release, bool reused)
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine(release);
        engine.ProjectConnectionCreated!(engine.Connection);
        var primary = engine.NewProject("Primary");
        var secondary = new Siemens.Engineering.Project { Name = "Reference", IsPrimary = false,
            Path = new FileInfo(@"C:\Reference\Line1.ap" + TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release)) };
        engine.Connection.Projects.AddRange(new[] { primary, secondary });
        engine.BindProject("Primary", primary.Path.FullName, secondary.Path.FullName, reused ? Environment.ProcessId : int.MaxValue,
            DateTime.UnixEpoch.Ticks);
        var gone = engine.CloseSecondaryForTest(secondary.Path.FullName, false);
        Assert.False(gone.Executed);
        Assert.True((bool)gone.GetType().GetProperty("SecondaryProjectGone")!.GetValue(gone)!);
        Assert.Null(engine.SecondaryFileForTest);
        Assert.Equal(0, secondary.CloseCalls);
        Assert.Equal(0, primary.CloseCalls);
    }

    public static IEnumerable<object[]> RetrievalCases()
        => new[] { "15.1", "16", "17", "18", "19", "20", "21" }.SelectMany(release =>
            new[] { "existing", "open", "bound", "session", "relative", "missing", "preview", "disconnected" }
                .Select(reason => new object[] { release, reason }));

    [Theory, MemberData(nameof(RetrievalCases))]
    public void Retrieve_refuses_before_native_operation_with_typed_evidence(string release, string reason)
    {
        TiaMcp.Adapters.Native.Hmi.HmiValues.Renderer = values => System.Text.Json.JsonSerializer.Serialize(values);
        var engine = new TiaMcp.Adapters.PlcFoundationEngine(release);
        if (reason != "disconnected") engine.ProjectConnectionCreated!(engine.Connection);
        if (reason == "open") engine.Connection.Projects.Add(engine.NewProject("Existing"));
        if (reason is "bound" or "session") engine.ProjectRetrieved!(engine.NewProject("Existing"));
        if (reason == "session") { engine.LocalSession = true; engine.ReplaceProjectHandle(null!); }
        string root = Path.Combine(AppContext.BaseDirectory, "retrieve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, "project.zap" + (release == "15.1" ? "15_1" : release));
            if (reason != "missing")
            {
                using var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create);
                using var writer = new StreamWriter(zip.CreateEntry("project.txt").Open());
                writer.Write("Synthetic archived project.");
            }
            string destination = reason == "existing" ? root : reason == "relative" ? "relative-project" : Path.Combine(root, "new");
            var result = engine.RetrieveProjectArchive(new ProjectRetrieveRequest {
                ArchivePath = archive, DestinationDirectory = destination, DryRun = reason == "preview" });
            if (reason == "preview")
            {
                Assert.Equal(true, result.Meta["operationSuccess"]);
                Assert.Equal(true, result.Meta["dryRun"]);
                Assert.False(Directory.Exists(destination));
            }
            else
            {
                Assert.Equal(reason switch { "existing" => "AlreadyExists", "relative" => "InvalidParams", "missing" => "NotFound",
                    _ => "PreconditionFailed" }, result.Meta["status"]);
                Assert.Equal(false, result.Meta["operationSuccess"]);
                if (reason is "open" or "bound" or "session") Assert.Equal("project-open", result.Meta["precondition"]);
                else if (reason == "disconnected") Assert.Equal("portal-connected", result.Meta["precondition"]);
                else Assert.Equal(reason == "missing" ? "archivePath" : "destinationDirectory", result.Meta["parameter"]);
                Assert.False(Directory.Exists(Path.Combine(root, "new")));
            }
            Assert.False(result.MayHaveChanged);
            Assert.NotEqual(true, result.Meta.GetValueOrDefault("mayHaveWrittenFiles"));
            Assert.False(result.OutcomeUnknown);
            Assert.Equal(0, engine.Connection.ExclusiveCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    public static IEnumerable<object[]> LifecycleCases()
        => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.SelectMany(release =>
            new[] { "CreateProject", "OpenProject", "AttachOpenProject" }.SelectMany(operation =>
                new[] { false, true }.Select(owns => new object[] { release, operation, owns })));

    [Theory, MemberData(nameof(LifecycleCases))]
    public void Bound_project_refusal_names_the_calling_operation(string release, string operation, bool owns)
    {
        var state = new TiaMcp.Adapters.PlcLifecycleState();
        state.Attached(123);
        string project = @"C:\Projects\Existing.ap" + TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release);
        state.Bound(project, owns, false);
        var error = Assert.Throws<AdapterPreconditionException>(() => state.RequireUnbound(operation));
        Assert.Contains(operation, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BindProject", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(operation == "CreateProject" ? "OpenProject" : "CreateProject", error.Message, StringComparison.Ordinal);
        Assert.False(error.IsArgument);
        Assert.Equal(project, state.ProjectFile);
        var bindingError = Assert.Throws<AdapterPreconditionException>(() => state.Bound(project, owns, false));
        Assert.DoesNotContain("BindProject", bindingError.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> Availability()
    {
        foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release);
            foreach (var tool in new[] { "ArchiveSavedProject", "BuildProjectScaffold", "ConnectIsolatedPortal",
                "EnsureOpennessUserGroup", "GetPortalInfo", "ManageMultiuserSession", "RetrieveProjectArchive",
                "SaveProjectCopy", "ValidateAutomationContext" })
            {
                bool available = tool == "ManageMultiuserSession" ? version >= 17
                    : tool is "ArchiveSavedProject" or "RetrieveProjectArchive" or "SaveProjectCopy" ? version >= 15 : true;
                yield return new object[] { release, tool, available };
            }
        }
    }

    [Theory, MemberData(nameof(Availability))]
    public void RegistrationMatchesProjectAndMultiuserApiAvailability(string release, string tool, bool available)
        => Assert.Equal(available, PortedFamilies.Available(release, tool));
}
