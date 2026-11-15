extern alias enginehost;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4.Domain;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;

[Collection(BundleSelectionCollection.Name)]
public sealed class OfflineToolRefusalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vmfix11a-" + Guid.NewGuid().ToString("N"));
    private const string PackageName = "{A1234567-1234-1234-1234-123456789ABC}.zip";
    public OfflineToolRefusalTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });
    public static IEnumerable<object[]> CwcReleases => new[] { "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    private static void Refused(JsonNode body, string parameter, string reason)
    {
        Assert.False((bool)body["ok"]!);
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal(parameter, (string?)body["error"]?["details"]?["parameter"]);
        Assert.Contains(reason, (string)body["error"]!["message"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        foreach (string internalName in new[] { "rulesJson", "rowsJson", "expectedFingerprint" })
            Assert.DoesNotContain(internalName, body.ToJsonString());
    }

    private string Template()
    {
        string path = Path.Combine(root, "template.xml");
        File.WriteAllText(path, "<Document><SW.Blocks.FC><AttributeList><Name>{{Name}}</Name></AttributeList></SW.Blocks.FC></Document>");
        return path;
    }

    private static TemplateRow[] Rows(string values) => JsonSerializer.Deserialize<TemplateRow[]>(
        "[{\"fileName\":\"FC_1.xml\",\"values\":" + values + "}]")!;

    private string Package()
    {
        string path = Path.Combine(root, "cwc");
        Directory.CreateDirectory(Path.Combine(path, "control"));
        File.WriteAllText(Path.Combine(path, "control", "index.html"), "<!doctype html><title>Fixture</title>");
        File.WriteAllText(Path.Combine(path, "manifest.json"), """
            {"mver":"1.2.0","control":{"identity":{"name":"Fixture","version":"1.0.0",
            "displayname":"Fixture","type":"guid://a1234567-1234-1234-1234-123456789abc","start":"./control/index.html"}}}
            """);
        return path;
    }

    [Theory, MemberData(nameof(Releases))]
    public void Audit_with_a_relative_directory_names_directoryPath_and_says_why(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        Refused(new Host.QualityAuditTools().AuditEngineeringExportsV4("rel").StructuredContent!, "/directoryPath", "absolute");
    }

    [Theory, MemberData(nameof(Releases))]
    public void Audit_with_a_bad_report_suffix_is_rejected_not_unknown(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string output = Path.Combine(root, "report.txt");
        Refused(new Host.QualityAuditTools().AuditEngineeringExportsV4(root, reportPath: output).StructuredContent!, "/reportPath", ".html");
        Assert.False(File.Exists(output));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Audit_invalid_rules_and_network_limit_name_the_declared_parameters(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var rules = new[] { new XPathRule(JsonSerializer.SerializeToElement(new { id = "r", xpath = "[" })) };
        Refused(new Host.QualityAuditTools().AuditEngineeringExportsV4(root, rules).StructuredContent!, "/rules", "XPath");
        Refused(new Host.QualityAuditTools().AuditEngineeringExportsV4(root, maxNetworks: 0).StructuredContent!, "/maxNetworks", "1..10000");
    }

    [Theory, MemberData(nameof(Releases))]
    public void Audit_output_planning_refusals_are_not_unknown(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var audit = new Host.QualityAuditTools();
        string existing = Path.Combine(root, "existing.html");
        File.WriteAllText(existing, "keep");
        foreach (string output in new[] { "relative.html", existing, Path.Combine(root, "missing", "report.html") })
            Refused(audit.AuditEngineeringExportsV4(root, reportPath: output).StructuredContent!, "/reportPath", "absolute");
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Template_with_an_unused_key_names_the_row_values_and_never_the_key(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = new Host.TemplateTools().InstantiatePlcXmlTemplatesV4(Template(),
            Rows("{\"Name\":\"FC_1\",\"caller_private_key_11a\":\"value\"}"), root).StructuredContent!;
        Refused(body, "/rows/0/values", "not used");
        Assert.DoesNotContain("caller_private_key_11a", body.ToJsonString());
        Assert.False(File.Exists(Path.Combine(root, "FC_1.xml")));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Template_apply_with_a_missing_token_is_rejected_not_unknown(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        Refused(new Host.TemplateTools().InstantiatePlcXmlTemplatesV4(Template(), Rows("{}"), root, dryRun: false).StructuredContent!,
            "/rows/0/values", "template token 'Name'");
        Assert.False(File.Exists(Path.Combine(root, "FC_1.xml")));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Template_path_and_output_refusals_are_not_unknown(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var tools = new Host.TemplateTools();
        var rows = Rows("{\"Name\":\"FC_1\"}");
        Refused(tools.InstantiatePlcXmlTemplatesV4("relative.xml", rows, root, false).StructuredContent!, "/templatePath", "absolute");
        Refused(tools.InstantiatePlcXmlTemplatesV4(Template(), rows, "relative", false).StructuredContent!, "/outputDirectory", "absolute");
        string existing = Path.Combine(root, "FC_1.xml"); File.WriteAllText(existing, "keep");
        Refused(tools.InstantiatePlcXmlTemplatesV4(Template(), rows, root, false).StructuredContent!, "/outputDirectory", "existing output");
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Theory, MemberData(nameof(CwcReleases))]
    public void Cwc_build_with_a_bad_output_path_is_rejected_not_unknown(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var tools = new Host.V21EcosystemTools(); string source = Package();
        string hash = (string)tools.ManageUnifiedCwcPackageV4(source).StructuredContent!["data"]!["data"]!["packageFingerprint"]!;
        Refused(tools.ManageUnifiedCwcPackageV4(source, "build", "relative.zip", false, hash).StructuredContent!, "/outputPath", "absolute");
        Assert.Empty(Directory.GetFiles(root, "*.zip"));
    }

    [Theory, MemberData(nameof(CwcReleases))]
    public void Cwc_inspect_of_a_missing_directory_names_directory(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        Refused(new Host.V21EcosystemTools().ManageUnifiedCwcPackageV4(Path.Combine(root, "missing")).StructuredContent!, "/directory", "existing");
    }

    [Theory, MemberData(nameof(CwcReleases))]
    public void Cwc_build_requires_the_matching_public_package_hash(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        Refused(new Host.V21EcosystemTools().ManageUnifiedCwcPackageV4(Package(), "build", Path.Combine(root, PackageName), false).StructuredContent!,
            "/expectedPackageHash", "inspect");
        Assert.Empty(Directory.GetFiles(root, "*.zip"));
    }

    [Theory, MemberData(nameof(CwcReleases))]
    public void Cwc_manifest_refusals_identify_directory_without_echoing_keys_or_paths(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string source = Package(); string manifest = Path.Combine(source, "manifest.json");
        var tools = new Host.V21EcosystemTools();
        foreach (string invalid in new[] { "{", "[]", "{\"control\":{}}", "{\"caller_private_key_11a\":1,\"caller_private_key_11a\":2}" })
        {
            File.WriteAllText(manifest, invalid);
            var body = tools.ManageUnifiedCwcPackageV4(source, "build", Path.Combine(root, PackageName), false).StructuredContent!;
            Refused(body, "/directory", "manifest");
            Assert.DoesNotContain("caller_private_key_11a", body.ToJsonString());
            Assert.DoesNotContain(root, (string)body["error"]!["message"]!);
        }
        Assert.Empty(Directory.GetFiles(root, "*.zip"));
    }

    [Theory, MemberData(nameof(CwcReleases))]
    public void Cwc_output_location_and_overwrite_refusals_preserve_files(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string source = Package(); var tools = new Host.V21EcosystemTools();
        string hash = (string)tools.ManageUnifiedCwcPackageV4(source).StructuredContent!["data"]!["data"]!["packageFingerprint"]!;
        string existing = Path.Combine(root, PackageName); File.WriteAllText(existing, "keep");
        foreach (string output in new[] { existing, Path.Combine(root, "missing", PackageName), Path.Combine(source, PackageName) })
            Refused(tools.ManageUnifiedCwcPackageV4(source, "build", output, false, hash).StructuredContent!, "/outputPath", "existing");
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Companion_missing_working_directory_names_workingDirectory(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var result = await new Host.EcosystemTools().RunPlcCompanionToolV4("relative");
        Refused(result.StructuredContent!, "/workingDirectory", "absolute");
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Companion_missing_runtime_identifies_the_resource_and_fix(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON", Path.Combine(root, "missing-python.exe"));
            var body = (await new Host.EcosystemTools().RunPlcCompanionToolV4(root)).StructuredContent!;
            Assert.Equal("RESOURCE_UNAVAILABLE", (string?)body["error"]?["code"]);
            Assert.Equal("companion-python-runtime", (string?)body["error"]?["details"]?["resource"]);
            Assert.Contains("install-plc-tools", (string)body["error"]!["message"]!);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.DoesNotContain(root, (string)body["error"]!["message"]!);
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON", previous); }
    }

    [Theory, MemberData(nameof(Releases))]
    public void Unproven_write_failure_remains_unknown(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = Host.OfflineContracts.Run("InstantiatePlcTemplates", () => throw new ArgumentException("No proof of write status."),
            writes: true).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal("unknown", (string?)body["meta"]?["execution"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Source_BOM_is_still_refused_with_ASCII_resave_guidance(string release)
    {
        var request = new PlcExternalSourceImportRequest { Release = release, Project = @"C:\Projects\Fixture.ap21", ProcessId = 123,
            Software = "Device/PLC", File = @"C:\Sources\Fixture.scl", AllowedFile = @"C:\Sources\Fixture.scl" };
        var error = Assert.Throws<AdapterPreconditionException>(() => PlcExternalSourceImportPolicy.Plan(request,
            _ => new MemoryStream(new byte[] { 0xef, 0xbb, 0xbf, 65 }), () => Array.Empty<string>(), () => { }));
        Assert.Equal("filePath", error.ParamName);
        Assert.Contains("re-save the source as ASCII without a BOM", error.Message);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Valid_template_preview_and_apply_keep_the_same_files(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string template = Template(); var rows = Rows("{\"Name\":\"FC_1\"}"); var tools = new Host.TemplateTools();
        var preview = tools.InstantiatePlcXmlTemplatesV4(template, rows, root).StructuredContent!;
        Assert.True((bool)preview["ok"]!); Assert.False(File.Exists(Path.Combine(root, "FC_1.xml")));
        var applied = tools.InstantiatePlcXmlTemplatesV4(template, rows, root, false).StructuredContent!;
        Assert.True((bool)applied["ok"]!);
        Assert.Contains("<Name>FC_1</Name>", File.ReadAllText(Path.Combine(root, "FC_1.xml")));
    }

    [Theory, MemberData(nameof(CwcReleases))]
    public void Valid_cwc_build_preserves_manifest_and_control_files(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var tools = new Host.V21EcosystemTools(); string source = Package();
        var preview = tools.ManageUnifiedCwcPackageV4(source).StructuredContent!;
        Assert.True((bool)preview["ok"]!);
        string hash = (string)preview["data"]!["data"]!["packageFingerprint"]!;
        string output = Path.Combine(root, PackageName);
        var applied = tools.ManageUnifiedCwcPackageV4(source, "build", output, false, hash).StructuredContent!;
        Assert.True((bool)applied["ok"]!);
        using var archive = ZipFile.OpenRead(output);
        Assert.Equal(new[] { "control/index.html", "manifest.json" }, archive.Entries.Select(entry => entry.FullName));
    }
}
