using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class ProjectLifecyclePortTests
{
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
