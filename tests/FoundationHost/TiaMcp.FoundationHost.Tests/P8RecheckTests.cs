extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;

public sealed class P8RecheckTests
{
    private static readonly string[] Releases = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };
    [Theory]
    [InlineData("14sp1", "BuildPlcAliasAlarmLad", false)]
    [InlineData("15.1", "BuildPlcAliasAlarmLad", false)]
    [InlineData("16", "BuildPlcAliasAlarmLad", false)]
    [InlineData("17", "BuildPlcAliasAlarmLad", false)]
    [InlineData("18", "BuildPlcAliasAlarmLad", false)]
    [InlineData("19", "BuildPlcAliasAlarmLad", false)]
    [InlineData("14sp1", "DecodePlcSimaticMl", false)]
    [InlineData("15.1", "DecodePlcSimaticMl", false)]
    [InlineData("16", "DecodePlcSimaticMl", false)]
    [InlineData("17", "DecodePlcSimaticMl", false)]
    [InlineData("18", "DecodePlcSimaticMl", false)]
    [InlineData("19", "DecodePlcSimaticMl", false)]
    [InlineData("14sp1", "InspectSimaticSdCompatibility", false)]
    [InlineData("15.1", "InspectSimaticSdCompatibility", false)]
    [InlineData("16", "InspectSimaticSdCompatibility", false)]
    [InlineData("17", "InspectSimaticSdCompatibility", false)]
    [InlineData("18", "InspectSimaticSdCompatibility", false)]
    [InlineData("19", "InspectSimaticSdCompatibility", false)]
    [InlineData("14sp1", "ValidatePlcDocumentSchemas", true)]
    [InlineData("15.1", "ValidatePlcDocumentSchemas", true)]
    [InlineData("16", "ValidatePlcDocumentSchemas", true)]
    [InlineData("17", "ValidatePlcDocumentSchemas", true)]
    [InlineData("18", "ValidatePlcDocumentSchemas", true)]
    [InlineData("19", "ValidatePlcDocumentSchemas", true)]
    [InlineData("14sp1", "AnalyzeUnifiedHmiTemplateLayout", false)]
    [InlineData("15.1", "AnalyzeUnifiedHmiTemplateLayout", false)]
    [InlineData("16", "AnalyzeUnifiedHmiTemplateLayout", false)]
    [InlineData("17", "AnalyzeUnifiedHmiTemplateLayout", false)]
    [InlineData("18", "AnalyzeUnifiedHmiTemplateLayout", false)]
    [InlineData("14sp1", "BuildClassicHmiMinimalPackage", false)]
    [InlineData("15.1", "BuildClassicHmiMinimalPackage", false)]
    [InlineData("16", "BuildClassicHmiMinimalPackage", false)]
    [InlineData("17", "BuildClassicHmiMinimalPackage", false)]
    [InlineData("18", "BuildClassicHmiMinimalPackage", false)]
    [InlineData("19", "BuildClassicHmiMinimalPackage", false)]
    [InlineData("14sp1", "BuildClassicHmiScreen", false)]
    [InlineData("15.1", "BuildClassicHmiScreen", false)]
    [InlineData("16", "BuildClassicHmiScreen", false)]
    [InlineData("17", "BuildClassicHmiScreen", false)]
    [InlineData("18", "BuildClassicHmiScreen", true)]
    [InlineData("19", "BuildClassicHmiScreen", true)]
    [InlineData("14sp1", "BuildClassicHmiTagTable", false)]
    [InlineData("15.1", "BuildClassicHmiTagTable", false)]
    [InlineData("16", "BuildClassicHmiTagTable", false)]
    [InlineData("17", "BuildClassicHmiTagTable", false)]
    [InlineData("18", "BuildClassicHmiTagTable", false)]
    [InlineData("19", "BuildClassicHmiTagTable", false)]
    [InlineData("14sp1", "BuildUnifiedHmiButtonActionScript", false)]
    [InlineData("15.1", "BuildUnifiedHmiButtonActionScript", false)]
    [InlineData("16", "BuildUnifiedHmiButtonActionScript", false)]
    [InlineData("17", "BuildUnifiedHmiButtonActionScript", false)]
    [InlineData("18", "BuildUnifiedHmiButtonActionScript", false)]
    [InlineData("14sp1", "BuildUnifiedHmiLayoutDesign", false)]
    [InlineData("15.1", "BuildUnifiedHmiLayoutDesign", false)]
    [InlineData("16", "BuildUnifiedHmiLayoutDesign", false)]
    [InlineData("17", "BuildUnifiedHmiLayoutDesign", false)]
    [InlineData("18", "BuildUnifiedHmiLayoutDesign", false)]
    [InlineData("14sp1", "BuildUnifiedHmiTemplateApplyDesign", false)]
    [InlineData("15.1", "BuildUnifiedHmiTemplateApplyDesign", false)]
    [InlineData("16", "BuildUnifiedHmiTemplateApplyDesign", false)]
    [InlineData("17", "BuildUnifiedHmiTemplateApplyDesign", false)]
    [InlineData("18", "BuildUnifiedHmiTemplateApplyDesign", false)]
    [InlineData("14sp1", "BuildUnifiedHmiTemplateApplyDesignManifest", false)]
    [InlineData("15.1", "BuildUnifiedHmiTemplateApplyDesignManifest", false)]
    [InlineData("16", "BuildUnifiedHmiTemplateApplyDesignManifest", false)]
    [InlineData("17", "BuildUnifiedHmiTemplateApplyDesignManifest", false)]
    [InlineData("18", "BuildUnifiedHmiTemplateApplyDesignManifest", false)]
    [InlineData("14sp1", "BuildUnifiedHmiThemeDesign", false)]
    [InlineData("15.1", "BuildUnifiedHmiThemeDesign", false)]
    [InlineData("16", "BuildUnifiedHmiThemeDesign", false)]
    [InlineData("17", "BuildUnifiedHmiThemeDesign", false)]
    [InlineData("18", "BuildUnifiedHmiThemeDesign", false)]
    [InlineData("14sp1", "ManageUnifiedCwcPackage", false)]
    [InlineData("15.1", "ManageUnifiedCwcPackage", false)]
    [InlineData("16", "ManageUnifiedCwcPackage", false)]
    [InlineData("17", "ManageUnifiedCwcPackage", true)]
    [InlineData("18", "ManageUnifiedCwcPackage", true)]
    [InlineData("14sp1", "RunClassicHmiOfflineValidationSuite", false)]
    [InlineData("15.1", "RunClassicHmiOfflineValidationSuite", false)]
    [InlineData("16", "RunClassicHmiOfflineValidationSuite", false)]
    [InlineData("17", "RunClassicHmiOfflineValidationSuite", false)]
    [InlineData("18", "RunClassicHmiOfflineValidationSuite", false)]
    [InlineData("19", "RunClassicHmiOfflineValidationSuite", false)]
    [InlineData("14sp1", "RunClassicHmiTemporaryImportPreflight", false)]
    [InlineData("15.1", "RunClassicHmiTemporaryImportPreflight", false)]
    [InlineData("16", "RunClassicHmiTemporaryImportPreflight", false)]
    [InlineData("17", "RunClassicHmiTemporaryImportPreflight", false)]
    [InlineData("18", "RunClassicHmiTemporaryImportPreflight", false)]
    [InlineData("19", "RunClassicHmiTemporaryImportPreflight", false)]
    [InlineData("14sp1", "RunHmiActionScriptRecipeSafetySelfTest", false)]
    [InlineData("15.1", "RunHmiActionScriptRecipeSafetySelfTest", false)]
    [InlineData("16", "RunHmiActionScriptRecipeSafetySelfTest", false)]
    [InlineData("17", "RunHmiActionScriptRecipeSafetySelfTest", false)]
    [InlineData("18", "RunHmiActionScriptRecipeSafetySelfTest", false)]
    [InlineData("14sp1", "RunHmiTemplatePlcSyncPrecheckSuite", false)]
    [InlineData("15.1", "RunHmiTemplatePlcSyncPrecheckSuite", false)]
    [InlineData("16", "RunHmiTemplatePlcSyncPrecheckSuite", false)]
    [InlineData("17", "RunHmiTemplatePlcSyncPrecheckSuite", false)]
    [InlineData("18", "RunHmiTemplatePlcSyncPrecheckSuite", false)]
    [InlineData("19", "RunHmiTemplatePlcSyncPrecheckSuite", true)]
    [InlineData("14sp1", "ValidateClassicHmiMinimalPackageFiles", false)]
    [InlineData("15.1", "ValidateClassicHmiMinimalPackageFiles", false)]
    [InlineData("16", "ValidateClassicHmiMinimalPackageFiles", false)]
    [InlineData("17", "ValidateClassicHmiMinimalPackageFiles", false)]
    [InlineData("18", "ValidateClassicHmiMinimalPackageFiles", false)]
    [InlineData("19", "ValidateClassicHmiMinimalPackageFiles", false)]
    [InlineData("14sp1", "ValidateClassicHmiMinimalPackagePlcSync", false)]
    [InlineData("15.1", "ValidateClassicHmiMinimalPackagePlcSync", false)]
    [InlineData("16", "ValidateClassicHmiMinimalPackagePlcSync", false)]
    [InlineData("17", "ValidateClassicHmiMinimalPackagePlcSync", false)]
    [InlineData("18", "ValidateClassicHmiMinimalPackagePlcSync", false)]
    [InlineData("19", "ValidateClassicHmiMinimalPackagePlcSync", false)]
    [InlineData("14sp1", "WriteClassicHmiMinimalPackageFiles", false)]
    [InlineData("15.1", "WriteClassicHmiMinimalPackageFiles", false)]
    [InlineData("16", "WriteClassicHmiMinimalPackageFiles", false)]
    [InlineData("17", "WriteClassicHmiMinimalPackageFiles", false)]
    [InlineData("18", "WriteClassicHmiMinimalPackageFiles", false)]
    [InlineData("19", "WriteClassicHmiMinimalPackageFiles", false)]
    [InlineData("14sp1", "CompareLibraries", false)]
    [InlineData("15.1", "CompareLibraries", false)]
    [InlineData("16", "CompareLibraries", false)]
    [InlineData("17", "CompareLibraries", false)]
    [InlineData("14sp1", "ListPlcSystemGroups", true)]
    [InlineData("14sp1", "CreatePlcInstanceDb", false)]
    [InlineData("14sp1", "SetPlcProgram", false)]
    [InlineData("15.1", "SetPlcProgram", false)]
    [InlineData("16", "SetPlcProgram", false)]
    [InlineData("17", "SetPlcProgram", false)]
    [InlineData("14sp1", "GetPlcWatchTableCurrentValuesReadOnly", false)]
    [InlineData("14sp1", "ListPlcForceTables", false)]
    [InlineData("14sp1", "MonitorPlcWatchTableS7", false)]
    [InlineData("14sp1", "ProbePlcMonitorOnlineCapabilities", false)]
    [InlineData("15.1", "SetPlcWatchTableModifyValue", false)]
    [InlineData("16", "SetPlcWatchTableModifyValue", false)]
    [InlineData("14sp1", "ExchangeMotionCamData", false)]
    [InlineData("14sp1", "ImportTechnologyObject", false)]
    [InlineData("15.1", "ImportTechnologyObject", false)]
    [InlineData("14sp1", "ImportTechnologyObjectsFromDirectory", false)]
    [InlineData("15.1", "ImportTechnologyObjectsFromDirectory", false)]
    [InlineData("14sp1", "SetPlcUnitObjectAccess", false)]
    [InlineData("15.1", "SetPlcUnitObjectAccess", false)]
    [InlineData("14sp1", "ManageCommunicationConnection", false)]
    [InlineData("15.1", "ManageCommunicationConnection", false)]
    [InlineData("16", "ManageCommunicationConnection", false)]
    [InlineData("17", "ManageCommunicationConnection", false)]
    [InlineData("18", "ManageCommunicationConnection", false)]
    [InlineData("19", "ManageCommunicationConnection", false)]
    [InlineData("14sp1", "ImportMasterCopyFromGlobalLibrary", false)]
    [InlineData("15.1", "ImportMasterCopyFromGlobalLibrary", false)]
    public void Reviewed_pairs_match_the_shared_registry(string release, string name, bool available)
    {
        Assert.Equal(available, PortedFamilies.Available(release, name));
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var registry = LegacyHostToolRegistry.Create(worker, release, false);
        Assert.Equal(available, registry.Any(t => t.ProtocolTool.Name == name));
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Foundation_pairs_and_V21_builders_follow_the_exact_release(string release)
    {
        int index = Array.IndexOf(Releases, release);
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var names = LegacyHostToolRegistry.Create(worker, release, false).Select(t => t.ProtocolTool.Name).ToHashSet();
        Assert.Equal(index >= 4, names.Contains("SearchHardwareCatalog"));
        Assert.Equal(index >= 4, names.Contains("CreateHardwareDevice"));
        Assert.Equal(index >= 1, names.Contains("ExportPlcWatchTable"));
        Assert.Equal(index >= 1, names.Contains("ListPlcWatchTables"));
        Assert.Equal(index >= 5, names.Contains("ImportMasterCopyFromGlobalLibrary"));
        foreach (string name in new[] { "BuildFlgNetCall", "BuildStructuredText", "BuildPlcTagTable" })
        {
            Assert.Equal(index >= 6, PortedFamilies.Available(release, name));
            Assert.Equal(index >= 6, names.Contains(name));
        }
        // 2026-10-20 decision (P8-81d): the whole-block composers write V21 documents and are registered on V21 only.
        foreach (string name in new[] { "BuildPlcLadFcBlock", "BuildPlcFcBlock", "BuildPlcFbBlock" })
        {
            Assert.Equal(index >= 7, PortedFamilies.Available(release, name));
            Assert.Equal(index >= 7, names.Contains(name));
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Master_copy_import_requires_the_exact_Unified_Openness_release(string release)
        => Assert.Equal(Array.IndexOf(Releases, release) < 5, LibraryCapabilities.Unsupported(release, "ImportMasterCopyFromGlobalLibrary") != null);

    [Theory]
    [InlineData("14sp1", "unit")]
    [InlineData("14sp1", "safety")]
    [InlineData("15.1", "unit")]
    [InlineData("15.1", "safety")]
    [InlineData("16", "safety")]
    [InlineData("17", "safety")]
    public async Task Missing_system_group_unit_scope_refuses_before_native_calls(string release, string unitKind)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == "ListPlcSystemGroups");
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = tool.ProtocolTool.Name, Arguments = new Dictionary<string, JsonElement> {
                ["softwarePath"] = JsonSerializer.SerializeToElement("PLC"), ["unitName"] = JsonSerializer.SerializeToElement("U"),
                ["unitKind"] = JsonSerializer.SerializeToElement(unitKind) } } };
        var result = await tool.InvokeAsync(request);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("14sp1", "ValidatePlcDocumentSchemas")]
    [InlineData("15.1", "ValidatePlcDocumentSchemas")]
    [InlineData("16", "ValidatePlcDocumentSchemas")]
    [InlineData("17", "ValidatePlcDocumentSchemas")]
    [InlineData("18", "ValidatePlcDocumentSchemas")]
    [InlineData("19", "ValidatePlcDocumentSchemas")]
    [InlineData("18", "BuildClassicHmiScreen")]
    [InlineData("19", "BuildClassicHmiScreen")]
    [InlineData("17", "ManageUnifiedCwcPackage")]
    [InlineData("18", "ManageUnifiedCwcPackage")]
    [InlineData("19", "RunHmiTemplatePlcSyncPrecheckSuite")]
    public async Task Opened_offline_tools_execute_with_current_policy_and_no_worker_calls(string release, string name)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "p8-opened-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        try
        {
            var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
            async Task<CallToolResult> Invoke(JsonObject args)
            {
                var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                    Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };
                var result = await tool.InvokeAsync(request);
                Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
                Assert.Equal("current", (string?)result.StructuredContent?["meta"]?["behaviorPolicy"]);
                Assert.Equal(release, (string?)result.StructuredContent?["meta"]?["releaseKey"]);
                return result;
            }
            if (name == "ValidatePlcDocumentSchemas")
            {
                string version = Versions(release)[0];
                string source = Path.Combine(scratch, "fragment.xml");
                File.WriteAllText(Path.Combine(scratch, "SW.InterfaceSections_v" + version + ".xsd"),
                    "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:element name='Sections'/></xs:schema>");
                File.WriteAllText(source, "<Document><Sections xmlns='http://www.siemens.com/automation/Openness/SW/Interface/v" + version + "'/></Document>");
                var result = await Invoke(new JsonObject { ["filePath"] = source, ["schemaDirectory"] = scratch });
                Assert.Contains("\"fragmentSchemasPassed\":true", result.StructuredContent!.ToJsonString());
            }
            else if (name == "BuildClassicHmiScreen")
            {
                var result = await Invoke(JsonNode.Parse("{\"design\":{\"screen\":{\"name\":\"Overview\",\"width\":800,\"height\":480},\"items\":[{\"type\":\"Text\",\"name\":\"StatusText\",\"left\":20,\"top\":20,\"width\":160,\"height\":40,\"text\":\"Ready\"}]}}")!.AsObject());
                var xml = System.Xml.Linq.XDocument.Parse((string)result.StructuredContent!["data"]!["xml"]!);
                Assert.Equal("V18", (string?)xml.Root!.Element("Engineering")!.Attribute("version"));
            }
            else if (name == "ManageUnifiedCwcPackage")
            {
                string directory = Path.Combine(scratch, "source");
                Directory.CreateDirectory(Path.Combine(directory, "control"));
                File.WriteAllText(Path.Combine(directory, "control/index.html"), "<html/> ");
                File.WriteAllText(Path.Combine(directory, "manifest.json"), "{\"mver\":\"1.2.0\",\"control\":{\"identity\":{\"name\":\"Test\",\"displayname\":\"Test\",\"version\":\"1.0\",\"type\":\"guid://551BF148-2F0D-4293-8E10-C9C3A1A6A073\"}}}");
                var inspect = await Invoke(new JsonObject { ["directory"] = directory, ["action"] = "inspect" });
                var preview = global::TiaMcpServer.ModelContextProtocol.UnifiedCwcPackage.Run(directory, "inspect", "", true, "");
                string output = Path.Combine(scratch, (string)preview["suggestedFileName"]!);
                await Invoke(new JsonObject { ["directory"] = directory, ["action"] = "build", ["outputPath"] = output, ["dryRun"] = true });
                Assert.False(File.Exists(output));
                await Invoke(new JsonObject { ["directory"] = directory, ["action"] = "build", ["outputPath"] = output,
                    ["dryRun"] = false, ["expectedPackageHash"] = (string)preview["packageFingerprint"]! });
                using var zip = System.IO.Compression.ZipFile.OpenRead(output);
                Assert.Equal(new[] { "control/index.html", "manifest.json" }, zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray());
            }
            else
            {
                string templates = Path.Combine(scratch, "templates");
                string source = Path.Combine(scratch, "tags.xml");
                string reports = Path.Combine(scratch, "reports");
                Directory.CreateDirectory(templates);
                File.WriteAllText(source, "<Document><Engineering version='V19'/><SW.Tags.PlcTagTable><AttributeList><Name>Tags</Name></AttributeList><ObjectList><SW.Tags.PlcTag><AttributeList><Name>Ready</Name><DataType>Bool</DataType><LogicalAddress>%M0.0</LogicalAddress></AttributeList></SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>");
                var result = await Invoke(new JsonObject { ["templateDirectory"] = templates, ["plcXmlPath"] = source, ["reportDirectory"] = reports });
                Assert.NotEmpty(Directory.GetFiles(reports));
                Assert.Contains("\"plcSymbolCount\":1", result.StructuredContent!.ToJsonString());
            }
            Assert.Equal(0, worker.Calls);
        }
        finally { Directory.Delete(scratch, true); }
    }

    public static IEnumerable<object[]> FragmentCases => from release in Releases
        from kind in new[] { "Interface", "FlgNet", "StructuredText", "STL", "Graph" }
        select new object[] { release, kind };

    [Theory, MemberData(nameof(FragmentCases))]
    public void Exact_fragment_names_and_schema_versions_are_checked_on_every_release(string release, string kind)
    {
        var versions = Versions(release);
        string version = versions[Array.IndexOf(new[] { "Interface", "FlgNet", "StructuredText", "STL", "Graph" }, kind)];
        string ns = "http://www.siemens.com/automation/Openness/SW/" + (kind == "Interface" ? "Interface/" : "NetworkSource/" + kind + "/") + "v" + version;
        string element = kind == "Interface" ? "Sections" : kind == "STL" ? "StatementList" : kind;
        string stem = kind == "Interface" ? "SW.InterfaceSections" : "SW.PlcBlocks." + (kind == "FlgNet" ? "LADFBD" : kind == "StructuredText" ? "SCL" : kind);
        string scratch = Path.Combine(Path.GetTempPath(), "p8-fragments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string source = Path.Combine(scratch, "fragment.xml");
            bool available = release != "14sp1" || kind == "Interface";
            string schema = Path.Combine(scratch, stem + (available ? "_v" + version : "") + ".xsd");
            File.WriteAllText(schema, "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:element name='" + element
                + "'><xs:complexType><xs:attribute name='Required' use='required'/></xs:complexType></xs:element></xs:schema>");
            File.WriteAllText(source, "<Document><" + element + " xmlns='" + ns + "' Required='yes'/></Document>");
            var valid = global::TiaMcpServer.ModelContextProtocol.PlcSchemaValidation.Validate(source, scratch);
            Assert.Equal(available, (bool)valid["fragmentSchemasPassed"]!);
            Assert.Equal(available ? 1 : 0, valid["validatedFragments"]!.AsArray().Count);
            Assert.Equal(available ? 0 : 1, valid["skippedFragments"]!.AsArray().Count);
            Assert.False((bool)valid["nativeImportValidated"]!);
            Assert.False((bool)valid["wholeDocumentValidated"]!);
            File.WriteAllText(source, "<Document><" + element + " xmlns='" + ns + "'/></Document>");
            var invalid = global::TiaMcpServer.ModelContextProtocol.PlcSchemaValidation.Validate(source, scratch);
            Assert.False((bool)invalid["fragmentSchemasPassed"]!);
            Assert.Equal(available, (int)invalid["errorCount"]! > 0);
            File.WriteAllText(source, "<Document><" + element + " xmlns='" + ns.Replace("/v" + version, "/v99") + "' Required='yes'/></Document>");
            var missing = global::TiaMcpServer.ModelContextProtocol.PlcSchemaValidation.Validate(source, scratch);
            Assert.False((bool)missing["fragmentSchemasPassed"]!);
            Assert.Single(missing["skippedFragments"]!.AsArray());
        }
        finally { Directory.Delete(scratch, true); }
    }

    internal static string[] Versions(string release) => release switch {
        "14sp1" => new[] { "2", "1", "1", "1", "1" },
        "15.1" => new[] { "3", "3", "2", "3", "4" },
        "16" or "17" => new[] { "4", "4", "3", "4", "5" },
        "18" => new[] { "5", "4", "3", "4", "5" },
        _ => new[] { "5", "5", "4", "5", "6" }
    };
}
