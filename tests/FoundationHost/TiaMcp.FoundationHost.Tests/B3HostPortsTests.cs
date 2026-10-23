extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Service = enginehost::TiaMcpServer.Siemens.Services.PlcOrganisationPortService;
using Xunit;

public sealed class B3HostPortsTests
{
    private static readonly IReadOnlyDictionary<string, int> FirstRelease = new Dictionary<string, int> {
        ["ManagePlcBlockProtection"] = 15, ["ListPlcSystemGroups"] = 14, ["CreatePlcInstanceDb"] = 15,
        ["SetPlcProgram"] = 18, ["GetPlcCrossReferences"] = 18, ["CreateHardwareCatalogDevice"] = 18,
        ["ManageDeviceServiceObjects"] = 18, ["ExchangeSystemDiagnosticsSettings"] = 17,
        ["ListNetworkDomains"] = 16, ["ManageNetworkDomain"] = 16,
        ["ListTransferAreas"] = 15, ["ManageTransferArea"] = 15,
        ["ListCommunicationConnections"] = 21, ["ManageCommunicationConnection"] = 21,
        // GSD creation needs a HardwareCatalog TypeIdentifier (V18); the GSD:<file>/DAP/<id> form is not verified earlier (P8-81b H-05).
        ["CreateGsdDevice"] = 18
    };

    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(r => new object[] { r });
    [Theory, MemberData(nameof(Releases))]
    public void Whole_tool_availability_matches_the_implemented_API_routes(string release)
    {
        int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release);
        foreach (var pair in FirstRelease)
            Assert.Equal(version >= pair.Value, PortedFamilies.Available(release, pair.Key));
        foreach (string name in new[] { "ManageDeviceUserGroup", "PlugDeviceItem", "ManageIoSystem", "SetDeviceItemChannel",
            "ManageHardwareUtilities", "ManagePlcUserGroup", "ManagePlcExternalSources", "BuildAndImportPlcArtifact", "CompileHmiDiagnostics" })
            Assert.True(PortedFamilies.Available(release, name));
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    public async Task Old_release_discovery_usage_bridges_batches_and_passive_reports_share_the_registry(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var registry = LegacyHostToolRegistry.Create(worker, release, false);
        var names = registry.Select(t => t.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        int count = registry.Count(t => !PortedFamilies.Contains(t.ProtocolTool.Name))
            + PortedFamilies.All.SelectMany(f => f.Tools).Count(name => PortedFamilies.Available(release, name));
        Assert.Equal(count, names.Count);
        foreach (string name in PortedFamilies.All.SelectMany(f => f.Tools))
            Assert.Equal(PortedFamilies.Available(release, name), names.Contains(name));
        async Task<JsonNode> Invoke(string name, object arguments)
        {
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments)) } };
            return (await registry.Single(t => t.ProtocolTool.Name == name).InvokeAsync(request)).StructuredContent!;
        }
        var categories = await Invoke("ListToolCategories", new { });
        Assert.Equal(count, (int)categories["data"]!["toolCount"]!);
        var foundItems = new List<string>();
        const int pageSize = 6;
        for (int offset = 0; offset < count; offset += pageSize)
        {
            var found = await Invoke("FindTools", new { limit = pageSize, offset });
            Assert.True((bool)found["ok"]!, found.ToJsonString());
            Assert.True(found["data"]?["items"] != null, found.ToJsonString());
            foundItems.AddRange(found["data"]!["items"]!.AsArray().Select(item => (string)item!));
        }
        Assert.Equal(count * 3, foundItems.Count);
        var usage = await Invoke("GetToolUsage", new { limit = 1 });
        Assert.Equal(count, (int)usage["data"]!["toolCount"]!);
        foreach (string name in FirstRelease.Keys.Where(name => !names.Contains(name)))
        {
            foreach (string bridge in new[] { "CallTool", "PreviewToolCall" })
            {
                var refusal = await Invoke(bridge, new { name, arguments = new { } });
                Assert.Equal("TOOL_NOT_FOUND", (string?)refusal["error"]?["code"]);
                Assert.Equal("Tool is not registered in this release.", (string?)refusal["error"]?["message"]);
                Assert.Equal("not-started", (string?)refusal["meta"]?["execution"]);
            }
            foreach (string batch in new[] { "RunReadOnlyToolBatch", "PreviewToolBatch" })
            {
                var refusal = await Invoke(batch, new { expectedProject = "Fixture", operations = new[] { new { name, arguments = new { } } } });
                Assert.Equal("TOOL_NOT_FOUND", (string?)refusal["error"]?["code"]);
            }
            var missingUsage = await Invoke("GetToolUsage", new { toolName = name });
            Assert.False((bool)missingUsage["ok"]!);
            Assert.DoesNotContain(foundItems, item => item.StartsWith(name + "(", StringComparison.Ordinal));
        }
        var passive = FoundationPassiveDiagnostics.Inspect(release, false, registry);
        Assert.Equal(count, (int)passive["registeredToolCount"]!);
        Assert.True((bool)passive["checks"]!["passed"]!);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Every_declaration_is_available_without_native_host_dependencies(string release)
    {
        var families = PortedFamilies.All.Where(f => f.Name is "F08" or "F09" or "F11" or "F17").ToArray();
        Assert.Equal(26, families.Sum(f => f.Tools.Length));
        foreach (var family in families) foreach (var tool in family.Tools)
            Assert.Equal(PlcSoftwareCapabilities.Unsupported(release, tool, family: "udt") == null, PortedFamilies.Available(release, tool));
        Assert.DoesNotContain(typeof(Service).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Siemens."));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Build_import_candidates_use_old_release_formats_and_preserve_modern_builder_bytes(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string target = release;
        var format = global::TiaMcpServer.ModelContextProtocol.PlcDeclarationXmlFormat.ForRelease(target);
        var build = typeof(Host.PlcBuildTools).GetMethod("BuildPlcArtifact", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var candidate in new[] {
            ("udt", "{\"name\":\"T\",\"members\":[{\"name\":\"Ready\",\"datatype\":\"Bool\"}]}"),
            ("globaldb", "{\"name\":\"DB\",\"number\":1,\"members\":[{\"name\":\"Ready\",\"datatype\":\"Bool\"}]}"),
            ("tagtable", "{\"name\":\"Tags\",\"tags\":[{\"name\":\"Ready\",\"datatype\":\"Bool\",\"address\":\"%M0.0\"}]}") })
        {
            var actual = (JsonObject)build.Invoke(null, new object[] { candidate.Item1, candidate.Item2 })!;
            var xml = System.Xml.Linq.XDocument.Parse((string)actual["xml"]!);
            Assert.Equal(format.EngineeringVersion, (string?)xml.Root!.Element("Engineering")!.Attribute("version"));
            if (candidate.Item1 != "tagtable")
                Assert.Contains(xml.Descendants(), node => node.Name == format.InterfaceNamespace + "Sections");
            if (release is "20" or "21")
            {
                var original = candidate.Item1 switch {
                    "udt" => global::TiaMcpServer.ModelContextProtocol.PlcBuilderToolJson.BuildUdt(candidate.Item2, target),
                    "globaldb" => global::TiaMcpServer.ModelContextProtocol.PlcBuilderToolJson.BuildGlobalDb(candidate.Item2, target),
                    _ => global::TiaMcpServer.ModelContextProtocol.PlcBuilderToolJson.BuildTagTableForRelease(candidate.Item2, target) };
                Assert.Equal(original.ToJsonString(), actual.ToJsonString());
            }
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Passive_diagnostics_preserve_the_existing_modern_projection_without_hiding_catalog_tools(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var registry = LegacyHostToolRegistry.Create(worker, release, false);
        var moved = PortedFamilies.All.Where(family => family.Name is "F08" or "F09" or "F11" or "F17")
            .SelectMany(family => family.Tools).ToArray();
        Assert.All(moved, name => Assert.Equal(PortedFamilies.Available(release, name), registry.Any(tool => tool.ProtocolTool.Name == name)));
        var legacyRegistry = registry.Where(tool => !moved.Contains(tool.ProtocolTool.Name)).ToArray();
        var actual = FoundationPassiveDiagnostics.Inspect(release, false, registry);
        JsonObject Projection(JsonObject report)
        {
            var projection = report.DeepClone().AsObject();
            foreach (string field in new[] { "registeredToolCount", "registeredTools", "nextOffset" }) projection.Remove(field);
            // These two checks now cover the full catalog, including the moved tools.
            projection["checks"]!.AsObject().Remove("foundationOperationsInSourceAllowlist");
            projection["checks"]!.AsObject().Remove("passed");
            return projection;
        }
        if (release is "20" or "21")
            Assert.Equal(Projection(FoundationPassiveDiagnostics.Inspect(release, false, legacyRegistry)).ToJsonString(),
                Projection(actual).ToJsonString());
        else
            Assert.Equal(registry.Count, (int)actual["registeredToolCount"]!);
        Assert.True((bool)actual["checks"]!["passed"]!);
        Assert.True((bool)actual["checks"]!["foundationOperationsInSourceAllowlist"]!);
        var legacyRows = new Dictionary<string, string>(StringComparer.Ordinal);
        const int pageSize = 17;
        for (int offset = 0; offset < legacyRegistry.Length;)
        {
            var page = FoundationPassiveDiagnostics.Inspect(release, false, legacyRegistry, offset: offset, limit: pageSize);
            Assert.InRange(page["registeredTools"]!.AsArray().Count, 1, pageSize);
            foreach (var row in page["registeredTools"]!.AsArray()) legacyRows.Add((string)row!["name"]!, row.ToJsonString());
            offset += page["registeredTools"]!.AsArray().Count;
        }
        var expectedNames = registry.Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal).ToArray();
        foreach (string name in new[] { "InitializeEnvironment", "RunCapabilitySelfTest" })
        {
            var tool = registry.Single(tool => tool.ProtocolTool.Name == name);
            var names = new List<string>();
            int offset = 0;
            while (true)
            {
                var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                    Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        JsonSerializer.Serialize(new { offset, limit = pageSize })) } };
                var response = await tool.InvokeAsync(request);
                var text = ((TextContentBlock)response.Content.Single()).Text;
                var body = response.StructuredContent!;
                Assert.False(response.IsError == true);
                Assert.True((bool)body["ok"]!);
                Assert.True(text.Length < 65536);
                Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(text)));
                var data = body["data"]!;
                Assert.Equal(registry.Count, (int)data["registeredToolCount"]!);
                foreach (string check in new[] { "uniqueRegisteredNames", "objectSchemaContracts", "foundationOperationsInSourceAllowlist", "passed" })
                    Assert.True((bool)data["checks"]![check]!);
                var rows = data["registeredTools"]!.AsArray();
                Assert.InRange(rows.Count, 1, pageSize);
                foreach (var row in rows)
                {
                    string rowName = (string)row!["name"]!;
                    names.Add(rowName);
                    Assert.True((bool)row["objectSchemaContractValid"]!);
                    if (legacyRows.TryGetValue(rowName, out string? expectedRow)) Assert.Equal(expectedRow, row.ToJsonString());
                }
                var paging = body["meta"]!["paging"]!;
                Assert.Equal("offset", (string?)paging["mode"]);
                Assert.Equal(offset, (int)paging["offset"]!);
                Assert.Equal(pageSize, (int)paging["limit"]!);
                Assert.Equal(registry.Count, (int)paging["total"]!);
                int? next = (int?)paging["nextOffset"];
                Assert.Equal((int?)FoundationPassiveDiagnostics.Inspect(release, false, registry, offset: offset, limit: pageSize)["nextOffset"], next);
                Assert.Equal(offset + rows.Count == registry.Count, (bool)paging["complete"]!);
                if (next == null)
                {
                    Assert.True((bool)paging["complete"]!);
                    break;
                }
                Assert.Equal(offset + rows.Count, next.Value);
                offset = next.Value;
            }
            Assert.Equal(expectedNames, names);
            Assert.Equal(registry.Count, names.Distinct(StringComparer.Ordinal).Count());
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Version_branches_are_action_level_and_independent_of_attachment(string release)
    {
        int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release);
        foreach (var branch in new[] {
            ("ManagePlcBlockProtection", "read", "", 15), ("ListPlcSystemGroups", "", "", 14),
            ("CreatePlcInstanceDb", "", "", 15), ("SetPlcProgram", "", "", 18),
            // P8-81d: watch-table group rename needs the V21 public Name setter (API audit F20); fc/fb builds are V21 only (2026-10-20).
            ("GetPlcCrossReferences", "", "", 18), ("ManagePlcUserGroup", "rename", "watchTables", 21),
            ("ManagePlcUserGroup", "rename", "technology", 19), ("ManagePlcUserGroup", "rename", "externalSources", 21),
            ("ManagePlcExternalSources", "renameGroup", "", 21), ("BuildAndImportPlcArtifact", "", "fc", 21) })
            Assert.Equal(version < branch.Item4, PlcSoftwareCapabilities.Unsupported(release, branch.Item1, branch.Item2, branch.Item3) != null);
        Assert.Equal(version < 16, PlcSoftwareCapabilities.Unsupported(release, "ListPlcSystemGroups", unitName: "U") != null);
        Assert.Equal(version < 18, PlcSoftwareCapabilities.Unsupported(release, "ListPlcSystemGroups", unitName: "U", unitKind: "safety") != null);
        Assert.Equal(version < 17, PlcSoftwareCapabilities.Unsupported(release, "ManagePlcExternalSources", "generateBlocks", targetKind: "block") != null);
        Assert.Equal(version < 20, PlcSoftwareCapabilities.Unsupported(release, "ManagePlcExternalSources", "createFromMasterCopy", copyMode: "Replace") != null);
        Assert.Equal(version < 15, PlcSoftwareCapabilities.Unsupported(release, "ManagePlcExternalSources", "generateBlocks", generateOption: "KeepOnError") != null);
        Assert.Null(PlcSoftwareCapabilities.Unsupported(release, "BuildAndImportPlcArtifact", family: "udt"));
        Assert.Null(PlcSoftwareCapabilities.Unsupported(release, "BuildAndImportPlcArtifact", family: "tagtable"));
        Assert.Null(PlcSoftwareCapabilities.Unsupported(release, "BuildAndImportPlcArtifact", family: "globaldb"));
    }

    [Theory]
    [InlineData("14sp1", "ManagePlcUserGroup", "{\"softwarePath\":\"PLC\",\"family\":\"watchTables\",\"groupPath\":\"Group\",\"action\":\"rename\",\"newName\":\"Next\"}")]
    [InlineData("16", "ManagePlcExternalSources", "{\"softwarePath\":\"PLC\",\"action\":\"generateBlocks\",\"name\":\"Source\",\"targetKind\":\"block\",\"targetGroupPath\":\"Group\"}")]
    [InlineData("17", "GetPlcTagTableConstants", "{\"softwarePath\":\"PLC\",\"tablePath\":\"Tags\",\"unitName\":\"Safety\",\"unitKind\":\"safety\"}")]
    [InlineData("18", "ManagePlcUserGroup", "{\"softwarePath\":\"PLC\",\"family\":\"technology\",\"groupPath\":\"Group\",\"action\":\"rename\",\"newName\":\"Next\"}")]
    [InlineData("20", "ManagePlcExternalSources", "{\"softwarePath\":\"PLC\",\"action\":\"renameGroup\",\"name\":\"Group\",\"newName\":\"Next\"}")]
    public async Task Missing_api_is_rejected_before_worker_state_or_native_lookup(string release, string name, string json)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
        var result = await tool.InvokeAsync(request);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    // API audit 2026-10-19 F9: PLC data types are imported before blocks (blocks skill "Import Order Rule"); F6: the PLC files
    // carry an in-window Engineering header; F25: overwrite defaults to false.
    public void Seed_PLC_and_HMI_use_explicit_worker_operations_with_types_before_blocks(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var root = Path.Combine(Path.GetTempPath(), "b3-seed-" + Guid.NewGuid().ToString("N"));
        var calls = new List<string>();
        try
        {
            foreach (string part in new[] { "plc/blocks", "plc/types", "hmi/tags", "hmi/screens" }) {
                Directory.CreateDirectory(Path.Combine(root, part)); File.WriteAllText(Path.Combine(root, part, "fixture.xml"), part.StartsWith("plc", StringComparison.Ordinal) ? "<Document><Engineering version=\"V" + release + "\" /></Document>" : "<Document />"); }
            var service = new Service((operation, args) => {
                string action = (string?)args["request"]?["Action"] ?? (string?)args["request"]?["Kind"] ?? "";
                if (operation.EndsWith("ExecutePlcOrganisation", StringComparison.Ordinal)) Assert.False((bool?)args["request"]?["Overwrite"]);
                calls.Add(operation + ":" + action);
                if (operation.EndsWith("SeedReferenceHmi")) return new JsonObject { ["Imported"] = new JsonArray("fixture"), ["Failed"] = new JsonArray() };
                if (action == "importBlockDirectory") return new JsonObject { ["Batch"] = new JsonObject { ["Imported"] = new JsonArray("ActualBlockName"), ["Failed"] = new JsonArray() } };
                return new JsonObject { ["Found"] = true };
            }, () => true, () => "fixture.ap" + release);
            var result = service.SeedProjectFromReference("PLC", "HMI", root);
            Assert.Equal(new[] { "plc-software.ExecutePlcOrganisation:importType", "plc-software.ExecutePlcOrganisation:importBlockDirectory", "plc-software.SeedReferenceHmi:tagTables", "plc-software.SeedReferenceHmi:screens" }, calls);
            Assert.Contains("plc:block:ActualBlockName", result.Imported!);
            Assert.Empty(result.Failed!);
        }
        finally { Directory.Delete(root, true); }
    }

    // API audit 2026-10-19 F6: a PLC file outside the release's SimaticML import window is refused before any worker operation;
    // the header is never rewritten.
    [Theory]
    [InlineData("20", "V16")]
    [InlineData("21", "V17")]
    [InlineData("19", "V15.1")]
    [InlineData("18", "V19")]
    public void Seed_refuses_an_out_of_window_PLC_file_before_any_import(string release, string header)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string root = Path.Combine(Path.GetTempPath(), "b3-window-" + Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(Path.Combine(root, "plc/types")); Directory.CreateDirectory(Path.Combine(root, "plc/blocks"));
            File.WriteAllText(Path.Combine(root, "plc/types/T.xml"), "<Document><Engineering version=\"V" + release + "\" /></Document>");
            File.WriteAllText(Path.Combine(root, "plc/blocks/B.xml"), "<Document><Engineering version=\"" + header + "\" /></Document>");
            var service = new Service((_, _) => throw new Exception("An out-of-window seed must issue no worker operation."), () => true, () => "fixture");
            var refusal = Assert.Throws<AdapterPreconditionException>(() => service.SeedProjectFromReference("PLC", "HMI", root));
            Assert.Contains(header, refusal.Message, StringComparison.Ordinal);
            Assert.False(refusal.IsArgument);
        }
        finally { Directory.Delete(root, true); }
    }

    // API audit 2026-10-19 F25: overwrite=false is sent by default, and a worker refusal raised before its first native mutation
    // (an existing tag table) stays a typed precondition on the host for the first file, then becomes a failed file.
    [Theory]
    [InlineData("17")]
    [InlineData("21")]
    public void Tag_table_directory_import_sends_overwrite_false_and_keeps_worker_refusals_typed(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string root = Path.Combine(Path.GetTempPath(), "b3-overwrite-" + Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(root);
            foreach (var name in new[] { "A", "B" }) File.WriteAllText(Path.Combine(root, name + ".xml"), "<Document><Engineering version=\"V" + release + "\" /></Document>");
            var overwrite = new List<bool?>();
            JsonNode Worker(string operation, JsonObject args)
            {
                if ((string?)args["request"]?["Action"] == "softwarePath") return new JsonObject { ["Message"] = "PLC" };
                overwrite.Add((bool?)args["request"]?["Overwrite"]);
                return (string?)args["request"]?["FilePath"] is string file && file.EndsWith("A.xml", StringComparison.Ordinal)
                    ? new JsonObject { ["Found"] = true }
                    : new JsonObject { ["MayHaveChanged"] = false, ["Failure"] = new JsonObject { ["Code"] = nameof(AdapterPreconditionException), ["Message"] = "The PLC tag table 'B' already exists and overwrite=false; nothing was imported." } };
            }
            var service = new Service(Worker, () => true, () => "fixture.ap" + release);
            var result = service.ImportPlcTagTablesFromDirectory("PLC", "", root);
            Assert.Equal(new bool?[] { false, false }, overwrite);
            Assert.Equal(new[] { "A" }, result.Imported!);
            Assert.Contains("overwrite=false", Assert.Single(result.Failed!).Error, StringComparison.Ordinal);
            File.Delete(Path.Combine(root, "A.xml"));
            Assert.Throws<AdapterPreconditionException>(() => service.ImportPlcTagTablesFromDirectory("PLC", "", root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("19")]
    public void Seed_HMI_dependency_refuses_before_any_PLC_import(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        string root = Path.Combine(Path.GetTempPath(), "b3-refuse-" + Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(Path.Combine(root, "hmi/screens")); File.WriteAllText(Path.Combine(root, "hmi/screens/fixture.xml"), "<Document />");
            var service = new Service((_, _) => throw new Exception("A refused seed must issue no worker operation."), () => true, () => "fixture");
            Assert.Throws<NotSupportedException>(() => service.SeedProjectFromReference("PLC", "HMI", root));
        }
        finally { Directory.Delete(root, true); }
    }
}
