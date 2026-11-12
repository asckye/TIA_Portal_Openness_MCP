extern alias enginehost;
using System.Text.Json.Nodes;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;

public sealed class OfflineLibraryAnalysisTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vmfix10b-library-" + Guid.NewGuid().ToString("N"));
    public OfflineLibraryAnalysisTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    private string NewLibrary(bool peData = true)
    {
        string library = Path.Combine(root, "Library");
        Directory.CreateDirectory(Path.Combine(library, "System"));
        Directory.CreateDirectory(Path.Combine(library, "XRef"));
        File.WriteAllText(Path.Combine(library, "Lib.al21"), "Synthetic library fixture");
        if (peData)
        {
            File.WriteAllText(Path.Combine(library, "System", "PEData.plf"), "Synthetic PEData");
            File.WriteAllText(Path.Combine(library, "System", "PEData.idx"), "Synthetic index");
        }
        return library;
    }

    private static JsonObject Analyze(string path) => new Host.HmiOfflineTools().AnalyzeGlobalLibraryPackageV4(path).StructuredContent!.AsObject();

    [Theory, MemberData(nameof(Releases))]
    public void New_library_without_xref_database_completes(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = Analyze(NewLibrary());
        Assert.True((bool)body["ok"]!);
        Assert.True((bool)body["data"]!["packageComplete"]!);
        Assert.False((bool)body["data"]!["xrefDatabasePresent"]!);
        Assert.Null(body["error"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Good_hash_carries_no_error_field(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = Analyze(NewLibrary());
        var file = body["data"]!["libraryFiles"]![0]!.AsObject();
        Assert.Equal("ok", (string?)file["sha256Status"]);
        Assert.False(file.ContainsKey("sha256Error"));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Library_without_pedata_is_a_finding(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = Analyze(NewLibrary(false));
        Assert.True((bool)body["ok"]!);
        Assert.False((bool)body["data"]!["packageComplete"]!);
        Assert.Null(body["error"]);
        Assert.NotEmpty(body["data"]!["recommendations"]!.AsArray());
    }

    [Theory, MemberData(nameof(Releases))]
    public void Missing_library_folder_is_not_found(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = Analyze(Path.Combine(root, "missing"));
        Assert.False((bool)body["ok"]!);
        Assert.Equal("NOT_FOUND", (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Library_file_resolves_its_package(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = Analyze(Path.Combine(NewLibrary(), "Lib.al21"));
        Assert.True((bool)body["ok"]!);
        Assert.True((bool)body["data"]!["packageComplete"]!);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Template_analysis_with_no_templates_completes(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = new Host.HmiOfflineTools().AnalyzeHmiTemplateReferenceV4(root, Path.Combine(root, "Project"), NewLibrary()).StructuredContent!;
        Assert.True((bool)body["ok"]!);
        Assert.False((bool)body["data"]!["templatesFound"]!);
        Assert.NotNull(body["data"]?["readiness"]);
        Assert.NotNull(body["data"]?["recommendations"]);
        Assert.Null(body["error"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Missing_template_folder_is_not_found(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = new Host.HmiOfflineTools().AnalyzeHmiTemplateReferenceV4(Path.Combine(root, "missing"), root, NewLibrary()).StructuredContent!;
        Assert.False((bool)body["ok"]!);
        Assert.Equal("NOT_FOUND", (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }
}
