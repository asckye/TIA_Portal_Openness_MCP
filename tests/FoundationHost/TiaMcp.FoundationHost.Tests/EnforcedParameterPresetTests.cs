extern alias toolpipeline;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.Generation;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;
using Xunit;
using Boundary = toolpipeline::TiaMcp.FoundationHost.CanonicalParameterTool;
using Context = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;

[Collection(BundleSelectionCollection.Name)]
public sealed class EnforcedParameterPresetTests
{
    [Fact]
    public void Every_software_selector_in_all_eight_contracts_has_an_explicit_review()
    {
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "manifest/contracts/v4/baseline", release + ".json")))!;
            foreach (var tool in baseline["tools"]!.AsArray())
                foreach (string key in SoftwareSelectors(tool!["inputSchema"]!, (string)tool["name"]!))
                {
                    bool enforced = ParameterPresets.SoftwareParameters.TryGetValue(key, out var reason);
                    Assert.True(enforced || ParameterPresets.ReviewedSoftwareParameters.TryGetValue(key, out reason), key);
                    Assert.False(string.IsNullOrWhiteSpace(reason), key);
                    encountered.Add(key);
                }
        }
        Assert.Equal(ParameterPresets.SoftwareParameters.Keys.Concat(ParameterPresets.ReviewedSoftwareParameters.Keys).OrderBy(key => key),
            encountered.OrderBy(key => key));
    }

    private static IEnumerable<string> SoftwareSelectors(JsonNode schema, string path)
    {
        if (schema is not JsonObject node) yield break;
        if (node["properties"] is JsonObject properties)
            foreach (var property in properties)
            {
                string name = property.Key, nested = path + "." + name;
                if (name.EndsWith("SoftwarePath", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("SoftwarePaths", StringComparison.OrdinalIgnoreCase) || name.StartsWith("plc", StringComparison.OrdinalIgnoreCase))
                    yield return nested;
                foreach (string match in SoftwareSelectors(property.Value!, nested)) yield return match;
            }
        if (node["items"] is JsonObject items)
            foreach (string match in SoftwareSelectors(items, path + "[]")) yield return match;
        foreach (string keyword in new[] { "allOf", "anyOf", "oneOf" })
            if (node[keyword] is JsonArray branches)
                foreach (var branch in branches)
                    foreach (string match in SoftwareSelectors(branch!, path)) yield return match;
        foreach (string keyword in new[] { "if", "then", "else", "additionalProperties" })
            if (node[keyword] is JsonObject branch)
                foreach (string match in SoftwareSelectors(branch, path)) yield return match;
        if (node["$defs"] is JsonObject definitions)
            foreach (var definition in definitions)
                foreach (string match in SoftwareSelectors(definition.Value!, path + ".$defs." + definition.Key)) yield return match;
    }

    [Theory, MemberData(nameof(CanonicalParameterTests.Releases), MemberType = typeof(CanonicalParameterTests))]
    public void Rebased_master_tools_have_reviewed_software_selectors(string release)
    {
        var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "manifest/contracts/v4/baseline", release + ".json")))!;
        var tools = baseline["tools"]!.AsArray().ToDictionary(tool => (string)tool!["name"]!, tool => tool!["inputSchema"]!);
        foreach (string name in new[] { "ExportPlcTagTables", "ConvertEngineeringTable", "ManagePlcTestLibrary",
            "SetUnifiedRuntimeSettings", "SetUnifiedGlobalScript", "ShowObjectInEditor" })
        {
            if (name is "SetUnifiedRuntimeSettings" or "SetUnifiedGlobalScript" && release is not ("19" or "20" or "21"))
            {
                Assert.False(tools.ContainsKey(name), release + "/" + name);
                continue;
            }
            Assert.True(tools.TryGetValue(name, out var schema), release + "/" + name);
            var names = ParameterPresets.Names(name, JsonSerializer.SerializeToElement(schema), Presets);
            if (name == "ExportPlcTagTables") Assert.Contains("softwarePath", names);
            else if (name == "ShowObjectInEditor")
            {
                Assert.Contains("softwarePath", names);
                Assert.True(ParameterPresets.PlcTarget(name, "softwarePath", new JsonObject { ["kind"] = "plcBlock" }));
                Assert.False(ParameterPresets.PlcTarget(name, "softwarePath", new JsonObject { ["kind"] = "device" }));
            }
            else if (name is "SetUnifiedRuntimeSettings" or "SetUnifiedGlobalScript")
            {
                Assert.DoesNotContain("softwarePath", names);
                Assert.StartsWith("HMI", ParameterPresets.SoftwareParameters[name + ".softwarePath"]);
            }
            else Assert.Empty(SoftwareSelectors(schema!, name));
        }
    }

    [Fact]
    public void Coverage_scan_finds_mixed_case_names_in_nested_objects_and_arrays()
    {
        var schema = JsonNode.Parse("""
            {"properties":{"rows":{"items":{"properties":{"targetSOFTWAREPATH":{},"nested":{"properties":{"PlcTarget":{}}}}}},
            "other":{"properties":{"sourceSoftwarePaths":{}}},"plc":{}}}
            """)!;
        Assert.Equal(new[] { "Tool.rows[].targetSOFTWAREPATH", "Tool.rows[].nested.PlcTarget", "Tool.other.sourceSoftwarePaths", "Tool.plc" },
            SoftwareSelectors(schema, "Tool"));
    }

    [Fact]
    public async Task Canonical_output_without_aliases_keeps_its_default_when_PLC_presets_are_enabled()
    {
        using var fixture = new Fixture("21", new Dictionary<string, string> { ["softwarePath"] = "PLC_1" });
        using var session = fixture.Pipeline.EnterSession();
        var input = new JsonObject { ["kind"] = "rows", ["direction"] = "toTable", ["format"] = "csv",
            ["rows"] = new JsonArray(new JsonObject { ["Name"] = "Example" }) };
        var effective = Boundary.ApplyDefaults("ConvertEngineeringTable", Arguments(input), out var error, prepareDirectory: false);
        Assert.Null(error);
        string path = effective.Json.GetProperty("exportFile").GetString()!;
        Assert.Equal(Context.Current.ExportDirectory, Path.GetDirectoryName(path));
        Assert.Equal(".csv", Path.GetExtension(path));
        var capture = new Capture("ConvertEngineeringTable", Schema("ConvertEngineeringTable"));
        var result = await Boundary.InvokeWithDefaults(capture, Request(capture, input), CancellationToken.None);
        Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        Assert.Equal(path, (string?)capture.Last!["exportFile"]);
        Assert.Equal(1, capture.Calls);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Fact]
    public async Task Library_master_copy_cannot_instantiate_into_another_PLC()
    {
        const string name = "ManageLibraryMasterCopy";
        var capture = new Capture(name, Schema(name));
        var tool = new Boundary(capture, "21", presets: Presets);
        var arguments = new JsonObject { ["sourcePath"] = "Sources/Example", ["action"] = "instantiate", ["targetKind"] = "block",
            ["targetSoftwarePath"] = "PLC_2", ["dryRun"] = false };
        Refused(await tool.InvokeAsync(Request(tool, arguments)), "/targetSoftwarePath");
        Assert.Equal(0, capture.Calls);
        arguments["targetSoftwarePath"] = "PLC_1";
        arguments["dryRun"] = true;
        var result = await tool.InvokeAsync(Request(tool, arguments));
        Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        Assert.Equal("PLC_1", (string?)capture.Last!["targetSoftwarePath"]);
        arguments.Remove("targetSoftwarePath");
        var omitted = await tool.InvokeAsync(Request(tool, arguments));
        Assert.False(omitted.IsError, omitted.StructuredContent?.ToJsonString());
        Assert.Equal("PLC_1", (string?)capture.Last!["targetSoftwarePath"]);
        Assert.Equal(2, capture.Calls);
    }

    [Theory, InlineData("ManageLibraryTypeVersion"), InlineData("ImportLibraryTypeDocuments")]
    public async Task Other_library_target_paths_accept_only_the_PLC_preset(string name)
    {
        var capture = new Capture(name, Schema(name));
        var tool = new Boundary(capture, "21", presets: Presets);
        var arguments = name == "ManageLibraryTypeVersion"
            ? new JsonObject { ["typePath"] = "Types/Example", ["version"] = "1.0.0", ["action"] = "instantiate" }
            : new JsonObject { ["filePath"] = "C:/Inputs/Example.xml" };
        arguments["targetSoftwarePath"] = "PLC_2";
        arguments["dryRun"] = false;
        Refused(await tool.InvokeAsync(Request(tool, arguments)), "/targetSoftwarePath");
        Assert.Equal(0, capture.Calls);
        arguments.Remove("targetSoftwarePath");
        arguments["dryRun"] = true;
        var omitted = await tool.InvokeAsync(Request(tool, arguments));
        Assert.False(omitted.IsError, omitted.StructuredContent?.ToJsonString());
        Assert.Equal("PLC_1", (string?)capture.Last!["targetSoftwarePath"]);
        Assert.Equal(1, capture.Calls);
    }

    public static IEnumerable<object[]> RefusedReflectionKinds()
        => from tool in new[] { "InvokeObject", "InvokeService" }
           from kind in new[] { "Software", "plcsoftware", "plc_software", "plc-software", "Device", "DeviceItem", "device_item", "device-item", " SOFTWARE " }
           select new object[] { tool, kind };

    [Theory, MemberData(nameof(RefusedReflectionKinds))]
    public async Task Reflection_targets_that_cannot_be_pinned_are_refused_before_dispatch(string name, string kind)
    {
        var capture = new Capture(name, Schema(name));
        var tool = new Boundary(capture, "21", presets: Presets);
        var result = await tool.InvokeAsync(Request(tool, new JsonObject { ["objectKind"] = kind, ["objectPath"] = "PLC_2",
            ["methodName"] = "Mutate", ["serviceTypeSuffix"] = "Service", ["dryRun"] = false }));
        Assert.True(result.IsError);
        Assert.Equal("PRECONDITION_FAILED", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Contains("--preset-plc-software (PLC_1)", (string?)result.StructuredContent["error"]!["message"]);
        Assert.Equal(0, capture.Calls);
        Assert.Null(ParameterPresets.Refusal(name, new JsonObject { ["objectKind"] = kind }, Array.Empty<string>(), ParameterPresets.Empty));
        using var fixture = new Fixture("21", new Dictionary<string, string> { ["softwarePath"] = "PLC_1" });
        using var session = fixture.Pipeline.EnterSession();
        var bridged = Host.CallTool(name, Arguments(new JsonObject { ["objectKind"] = kind, ["objectPath"] = "PLC_2" }));
        Assert.True(bridged.IsError);
        Assert.Equal("PRECONDITION_FAILED", (string?)bridged.StructuredContent!["error"]!["code"]);
        Assert.Contains("--preset-plc-software (PLC_1)", (string?)bridged.StructuredContent["error"]!["message"]);
        var bridge = fixture.Pipeline.AllTools.Single(tool => tool.ProtocolTool.Name == "CallTool");
        var outer = await bridge.InvokeAsync(Request(bridge, new JsonObject { ["name"] = name,
            ["arguments"] = new JsonObject { ["objectKind"] = kind, ["objectPath"] = "PLC_2" } }));
        Assert.True(outer.IsError);
        Assert.Equal("PRECONDITION_FAILED", (string?)outer.StructuredContent!["error"]!["code"]);
        Assert.Contains("--preset-plc-software (PLC_1)", (string?)outer.StructuredContent["error"]!["message"]);
        foreach (string wrapper in new[] { "PreviewToolCall", "PreviewToolBatch", "RunReadOnlyToolBatch" })
        {
            var input = new JsonObject { ["name"] = name, ["arguments"] = new JsonObject { ["objectKind"] = kind, ["objectPath"] = "PLC_2" } };
            var arguments = wrapper == "PreviewToolCall" ? input : new JsonObject { ["operations"] = new JsonArray(input) };
            var toolWrapper = fixture.Pipeline.AllTools.Single(tool => tool.ProtocolTool.Name == wrapper);
            var refusal = await toolWrapper.InvokeAsync(Request(toolWrapper, arguments));
            Assert.True(refusal.IsError);
            Assert.Equal("PRECONDITION_FAILED", (string?)refusal.StructuredContent!["error"]!["code"]);
            Assert.Contains("--preset-plc-software (PLC_1)", (string?)refusal.StructuredContent["error"]!["message"]);
        }
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, InlineData("InvokeObject", "Block"), InlineData("InvokeObject", "Type"),
        InlineData("InvokeService", "Block"), InlineData("InvokeService", "Type")]
    public async Task Reflection_Block_and_Type_accept_the_PLC_preset(string name, string kind)
    {
        var capture = new Capture(name, Schema(name));
        var tool = new Boundary(capture, "21", presets: Presets);
        var arguments = new JsonObject { ["objectKind"] = kind, ["objectPath"] = "Example", ["methodName"] = "Read",
            ["softwarePath"] = "PLC_1" };
        if (name == "InvokeService") arguments["serviceTypeSuffix"] = "Service";
        var result = await tool.InvokeAsync(Request(tool, arguments));
        Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        Assert.Equal(1, capture.Calls);
        Assert.Equal("PLC_1", (string?)capture.Last!["softwarePath"]);
    }

    [Fact]
    public void Every_configured_description_is_registered_in_the_source_catalog()
    {
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "reference/tool-catalog/engine-source.json")))!;
        var descriptions = catalog["presetDescriptions"]!.AsObject();
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        {
            var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "manifest/contracts/v4/baseline", release + ".json")))!;
            foreach (var tool in baseline["tools"]!.AsArray())
            {
                string name = (string)tool!["name"]!;
                var names = ParameterPresets.Names(name, JsonSerializer.SerializeToElement(tool["inputSchema"]), Presets);
                if (names.Length == 0) continue;
                Assert.Equal(ParameterPresets.Description(name, names), (string?)descriptions[name]);
                encountered.Add(name);
            }
        }
        Assert.Equal(descriptions.Select(pair => pair.Key).OrderBy(name => name), encountered.OrderBy(name => name));
    }

    [Theory, InlineData("CreatePlcTag", "plc"), InlineData("SeedProjectFromReference", "plcSoftwarePath"),
        InlineData("ManagePlcSafety", "softwarePath"), InlineData("ManageTestSuiteCase", "softwarePath"),
        InlineData("ManageWatchForceTableWebAccess", "softwarePath"), InlineData("ImportLibraryTypeDocuments", "targetSoftwarePath")]
    public void PLC_preset_covers_other_parameter_names_and_domains(string tool, string parameter)
    {
        var schema = Schema(tool);
        Assert.Contains(parameter, ParameterPresets.Names(tool, schema, Presets));
        Assert.False(ParameterPresets.Schema(tool, schema, Presets).GetProperty("properties").TryGetProperty(parameter, out _));
        Assert.Equal("PLC_1", ParameterPresets.Value(Presets, parameter));
    }

    [Theory, InlineData("InvokeObject", "objectKind", "Block", "HmiScripts"),
        InlineData("InvokeService", "objectKind", "Type", "HmiScriptModule"),
        InlineData("CreateLibraryMasterCopy", "sourceKind", "block", "screen"),
        InlineData("ManageDriveTelegrams", "action", "connectTechnologyObject", "read")]
    public void Mixed_tools_apply_the_PLC_preset_only_to_PLC_targets(string tool, string selector, string plc, string other)
    {
        Assert.True(ParameterPresets.Conditional(tool, "softwarePath"));
        Assert.True(ParameterPresets.PlcTarget(tool, "softwarePath", new JsonObject { [selector] = plc }));
        Assert.False(ParameterPresets.PlcTarget(tool, "softwarePath", new JsonObject { [selector] = other }));
        Assert.True(ParameterPresets.Schema(tool, Schema(tool), Presets).GetProperty("properties").TryGetProperty("softwarePath", out _));
    }

    [Fact]
    public void Project_canonicalizer_uses_filesystem_long_names_and_the_native_identity_rule()
    {
        string directory = Path.Combine(Root(), "bin-build/refactor/canonical project " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "Long project filename.ap21");
        File.WriteAllText(file, "existence fixture");
        try
        {
            string canonical = PresetProjectPath.Canonical(file);
            Assert.True(PresetProjectPath.Same(file, Path.Combine(directory, ".", Path.GetFileName(file)).Replace('\\', '/')));
            TiaMcp.Adapters.MutationIdentityPolicy.RequireSameProject(canonical, file);
            var buffer = new System.Text.StringBuilder(32768);
            uint length = GetShortPathName(file, buffer, (uint)buffer.Capacity);
            if (length > 0 && length < buffer.Capacity)
            {
                Assert.Equal(canonical, PresetProjectPath.Canonical(buffer.ToString()));
                var options = HostOptions.Parse(new[] { "--bundle-root", Root(), "--release-key", "21",
                    "--preset-project-file", buffer.ToString() }, Root());
                Assert.Equal(canonical, options.Presets["expectedProjectFile"]);
                Console.WriteLine("Project canonicalizer short-name fixture: " + buffer);
            }
            else Assert.Equal(canonical, PresetProjectPath.Canonical(canonical));
            Assert.False(PresetProjectPath.Same(file, file + ":stream"));
            Assert.False(PresetProjectPath.Same(file, Path.Combine(directory, "Other.ap21")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern uint GetShortPathName(string path, System.Text.StringBuilder output, uint size);

    [Theory, MemberData(nameof(CanonicalParameterTests.Releases), MemberType = typeof(CanonicalParameterTests))]
    public async Task Project_preset_refuses_rebinding_and_every_following_unbound_or_wrong_project_write(string release)
    {
        using var fixture = new ProjectFixture(release);
        var closed = await fixture.Call("CloseProject", new JsonObject());
        Assert.False(closed.IsError, closed.StructuredContent?.ToJsonString());
        Assert.Null(fixture.Worker.Project);
        if (release == "14sp1") Assert.False(fixture.HasTool("RetrieveProjectArchive"));
        else ProjectRefused(await fixture.Call("RetrieveProjectArchive", new JsonObject {
            ["archivePath"] = "C:/Other/Other.zap21", ["destinationDirectory"] = "C:/Other/retrieved", ["dryRun"] = false }));
        ProjectRefused(await fixture.Call(fixture.WriteTool, new JsonObject()));
        fixture.Worker.Project = fixture.Project;
        if (release == "14sp1") Assert.False(fixture.HasTool("SaveProjectCopy"));
        else ProjectRefused(await fixture.Call("SaveProjectCopy", new JsonObject { ["newProjectPath"] = "C:/Other/copy" }, bridge: true));
        fixture.Worker.Project = "C:/Other/Other.ap21";
        ProjectRefused(await fixture.Call(fixture.WriteTool, new JsonObject(), bridge: true));
        ProjectRefused(await fixture.Call("CallTool", new JsonObject { ["name"] = fixture.WriteTool, ["arguments"] = new JsonObject() }));
        ProjectRefused(await fixture.Call("CallTool", new JsonObject { ["name"] = fixture.WriteTool }));
        ProjectRefused(await fixture.Call("PreviewToolBatch", new JsonObject { ["operations"] = new JsonArray(
            new JsonObject { ["name"] = fixture.WriteTool, ["arguments"] = new JsonObject() }) }));
        ProjectRefused(await fixture.Call("ConnectPortal", new JsonObject()));
        ProjectRefused(await fixture.Call("BuildProjectScaffold", new JsonObject { ["dryRun"] = false,
            ["spec"] = new JsonObject { ["projectName"] = "Escaped", ["directoryPath"] = "C:/Other" } }));
        ProjectRefused(await fixture.Call("OpenProject", new JsonObject { ["path"] = "C:/Other/Other.ap21", ["dryRun"] = false }));
        Refused(await fixture.Call("AttachOpenProject", new JsonObject { ["projectName"] = "Other",
            ["expectedProjectFile"] = "C:/Other/Other.ap21" }), "/expectedProjectFile");
        int checks = 0;
        var generated = Host.DispatchGenerationStep(fixture.WriteTool, Arguments(new JsonObject()), () => { checks++; return null; });
        ProjectRefused(generated);
        ProjectRefused(await fixture.Call("RunToolTransaction", new JsonObject(), bridge: true));
        ProjectRefused(await fixture.Call("ApplyGenerationPlan", new JsonObject()));
        Assert.Equal(0, checks);
        Assert.Equal(new[] { "CloseProject" }, fixture.Worker.Dispatched);
    }

    public static IEnumerable<object[]> SecondaryPresetCases()
        => new[] { "15.1", "16", "17", "18", "19", "20", "21" }
            .SelectMany(release => new[] { false, true }.Select(bridge => new object[] { release, bridge }));

    [Theory, MemberData(nameof(SecondaryPresetCases))]
    public async Task Secondary_reference_lifecycle_preserves_the_preset_binding_and_cannot_become_a_write_target(string release, bool bridge)
    {
        using var fixture = new ProjectFixture(release);
        string reference = "C:/Reference/Line1.ap" + (release == "15.1" ? "15_1" : release);
        foreach (bool preview in new[] { true, false })
        {
            var opened = await fixture.Call("OpenProject", new JsonObject { ["path"] = reference, ["openMode"] = "secondary",
                ["dryRun"] = preview, ["confirm"] = !preview, ["expectedProjectFile"] = reference }, bridge);
            Assert.False(opened.IsError, opened.StructuredContent?.ToJsonString());
            Assert.Equal(fixture.Project, fixture.Worker.Project);
            Assert.Equal(reference, (string?)fixture.Worker.Last!["expectedProjectFile"]);
            Assert.Equal("secondary", (string?)fixture.Worker.Last!["openMode"]);
            var closed = await fixture.Call("CloseProject", new JsonObject { ["secondaryProjectFile"] = reference,
                ["dryRun"] = preview, ["confirm"] = !preview, ["expectedProjectFile"] = reference }, bridge);
            Assert.False(closed.IsError, closed.StructuredContent?.ToJsonString());
            Assert.Equal(fixture.Project, fixture.Worker.Project);
            Assert.Equal(reference, (string?)fixture.Worker.Last!["expectedProjectFile"]);
        }
        int dispatches = fixture.Worker.Dispatched.Count;
        Refused(await fixture.Call("SaveProject", new JsonObject { ["expectedProjectFile"] = reference }, bridge), "/expectedProjectFile");
        Assert.Equal(dispatches, fixture.Worker.Dispatched.Count);
        fixture.Worker.Project = reference;
        ProjectRefused(await fixture.Call(fixture.WriteTool, new JsonObject(), bridge));
        ProjectRefused(await fixture.Call("OpenProject", new JsonObject { ["path"] = reference, ["openMode"] = "secondary" }, bridge));
        ProjectRefused(await fixture.Call("CloseProject", new JsonObject { ["secondaryProjectFile"] = reference }, bridge));
        fixture.Worker.Project = null;
        ProjectRefused(await fixture.Call("OpenProject", new JsonObject { ["path"] = reference, ["openMode"] = "secondary" }, bridge));
        Assert.Equal(dispatches, fixture.Worker.Dispatched.Count);
    }

    [Fact]
    public void Automatic_binding_checks_the_same_canonical_project_before_assigning_it()
    {
        const string preset = "C:/Preset/Example.ap21";
        TiaMcp.Adapters.Contracts.ProjectFilePreset.RequireTarget(preset, null);
        TiaMcp.Adapters.Contracts.ProjectFilePreset.RequireTarget(preset, "c:\\PRESET\\.\\Example.ap21");
        var refusal = Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            TiaMcp.Adapters.Contracts.ProjectFilePreset.RequireTarget(preset, "C:/Other/Other.ap21"));
        Assert.Equal("server-project-preset", refusal.Condition);
        Assert.Contains("--preset-project-file", refusal.Message);
    }

    [Theory, MemberData(nameof(CanonicalParameterTests.Releases), MemberType = typeof(CanonicalParameterTests))]
    public async Task Every_WRITE_and_ONLINE_WRITE_route_checks_the_current_binding_first(string release)
    {
        using var fixture = new ProjectFixture(release);
        fixture.Worker.Project = "C:/Other/Other.ap21";
        var writes = Context.Current.Catalog!.All.Values
            .Where(tool => tool.Classification?.Operation is "WRITE" or "ONLINE-WRITE").Select(tool => tool.Name).ToArray();
        Assert.NotEmpty(writes);
        foreach (string name in writes)
        {
            ProjectRefused(await fixture.Call(name, new JsonObject()));
            ProjectRefused(await fixture.Call(name, new JsonObject(), bridge: true));
        }
        Assert.Empty(fixture.Worker.Dispatched);
    }

    private static void ProjectRefused(CallToolResult result)
    {
        Assert.True(result.IsError, result.StructuredContent?.ToJsonString());
        Assert.Equal("PRECONDITION_FAILED", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Contains("--preset-project-file", (string?)result.StructuredContent["error"]!["message"]);
    }
    private static readonly IReadOnlyDictionary<string, string> Presets = new Dictionary<string, string> {
        ["softwarePath"] = "PLC_1", ["expectedProjectFile"] = "C:/Projects/Fixture.ap21" };

    [Theory, MemberData(nameof(CanonicalParameterTests.Releases), MemberType = typeof(CanonicalParameterTests))]
    public void Registry_and_full_catalog_hide_only_declared_PLC_presets(string release)
    {
        using var fixture = new Fixture(release, Presets);
        using var session = fixture.Pipeline.EnterSession();
        var catalog = Context.Current.Catalog!;
        var plc = catalog.Find("CompilePlcSoftware")!;
        var properties = plc.Tool.InputSchema.GetProperty("properties");
        foreach (string name in Presets.Keys)
        {
            Assert.False(properties.TryGetProperty(name, out _));
            Assert.DoesNotContain(plc.Tool.InputSchema.GetProperty("required").EnumerateArray(), field => field.GetString() == name);
            Assert.DoesNotContain(plc.Parameters, parameter => parameter.Name == name);
            Assert.DoesNotContain(name + ":", plc.Signature);
        }
        Assert.Contains("Preset by the server: expectedProjectFile, softwarePath.", plc.Tool.Description);
        Assert.True(catalog.Find("ListHmiTags")!.Tool.InputSchema.GetProperty("properties").TryGetProperty("softwarePath", out _));
        var listing = catalog.Find("ListPlcBlocks")!;
        var effective = Boundary.ApplyDefaults("ListPlcBlocks", Arguments(new JsonObject()), out var error);
        Assert.Null(error);
        Assert.Equal("PLC_1", effective.Json.GetProperty("softwarePath").GetString());
        Assert.False(effective.Json.TryGetProperty("expectedProjectFile", out _));
        Assert.Null(Context.Current.Invoker!.Bind("ListPlcBlocks", effective, out var bound));
        Assert.NotNull(bound);
    }

    [Theory, InlineData("14sp1"), InlineData("21")]
    public void Without_presets_every_input_schema_matches_the_captured_contract(string release)
    {
        using var fixture = new Fixture(release, ParameterPresets.Empty);
        var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "manifest/contracts/v4/baseline", release + ".json")))!;
        var tools = fixture.Pipeline.AllTools.ToDictionary(tool => tool.ProtocolTool.Name);
        var expectedNames = baseline["tools"]!.AsArray().Select(tool => (string)tool!["name"]!)
            .Append("GetApprovalStatus").Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(expectedNames, tools.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Empty(tools["GetApprovalStatus"].ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject());
        foreach (var expected in baseline["tools"]!.AsArray())
        {
            string name = (string)expected!["name"]!;
            Assert.True(JsonNode.DeepEquals(expected["inputSchema"], JsonNode.Parse(tools[name].ProtocolTool.InputSchema.GetRawText())), name);
            string descriptionHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(tools[name].ProtocolTool.Description ?? "")));
            if ((string?)expected["descriptionSha256"] != descriptionHash)
                Console.WriteLine("Description baseline mismatch for " + release + "/" + name + ": " + tools[name].ProtocolTool.Description);
            Assert.Equal((string?)expected["descriptionSha256"], descriptionHash);
        }
    }

    [Theory, InlineData("softwarePath", "PLC_1"), InlineData("softwarePath", "Other"),
        InlineData("expectedProjectFile", "C:/Projects/Fixture.ap21"), InlineData("expectedProjectFile", "")]
    public async Task Direct_boundary_accepts_equal_presets_and_refuses_different_values(string parameter, string value)
    {
        var capture = new Capture("CompilePlcSoftware", CompileSchema());
        var tool = new Boundary(capture, "21", presets: Presets);
        var result = await tool.InvokeAsync(Request(tool, new JsonObject { [parameter] = value }));
        bool equal = value == Presets[parameter];
        if (equal)
        {
            Assert.False(result.IsError);
            Assert.Equal(Presets[parameter], (string?)capture.Last![parameter]);
            Assert.Equal(1, capture.Calls);
        }
        else { Refused(result, "/" + parameter); Assert.Equal(0, capture.Calls); }
    }

    [Fact]
    public async Task Preview_and_execution_receive_the_same_injected_plan_identity()
    {
        var capture = new Capture("CompilePlcSoftware", CompileSchema());
        var tool = new Boundary(capture, "21", presets: Presets);
        var preview = await tool.InvokeAsync(Request(tool, new JsonObject { ["dryRun"] = true }));
        var previewArguments = capture.Last!.DeepClone();
        var execute = await tool.InvokeAsync(Request(tool, new JsonObject { ["dryRun"] = false, ["confirm"] = true }));
        Assert.Equal("PLC_1", (string?)capture.Last!["softwarePath"]);
        Assert.Equal(Presets["expectedProjectFile"], (string?)capture.Last["expectedProjectFile"]);
        Assert.Equal((string?)preview.StructuredContent!["data"]!["planHash"], (string?)execute.StructuredContent!["data"]!["planHash"]);
        Assert.Equal((string?)previewArguments["expectedProjectFile"], (string?)capture.Last["expectedProjectFile"]);
    }

    [Theory, InlineData("14sp1"), InlineData("21")]
    public async Task Bridge_batch_and_preflight_refuse_supplied_presets(string release)
    {
        using var fixture = new Fixture(release, Presets, captureListing: true);
        using var session = fixture.Pipeline.EnterSession();
        var direct = fixture.Pipeline.AllTools.Single(tool => tool.ProtocolTool.Name == "ListPlcBlocks");
        using var requestScope = Host.UseProgressRequest(Request(direct, new JsonObject()), CancellationToken.None);
        var supplied = Arguments(new JsonObject { ["softwarePath"] = "Other" });
        Refused(Host.CallTool("ListPlcBlocks", supplied), "/softwarePath");
        Refused(Host.PreviewToolCall("ListPlcBlocks", supplied), "/softwarePath");
        var batch = Host.ReadToolBatch(new[] { new ToolCall("ListPlcBlocks", supplied) });
        Refused(batch, "/operations/0/arguments/softwarePath");
        Assert.Equal(0, fixture.Capture!.Calls);
        var result = Host.CallTool("ListPlcBlocks", Arguments(new JsonObject()));
        Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        Assert.Equal("PLC_1", (string?)fixture.Capture.Last!["softwarePath"]);
        var directResult = await direct.InvokeAsync(Request(direct, new JsonObject()));
        Assert.False(directResult.IsError, directResult.StructuredContent?.ToJsonString());
        Assert.Equal(2, fixture.Capture.Calls);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, InlineData("14sp1"), InlineData("21")]
    public async Task Usage_calls_and_sequences_follow_the_served_preset_schemas(string release)
    {
        using var fixture = new Fixture(release, Presets);
        using var session = fixture.Pipeline.EnterSession();
        var usage = new toolpipeline::TiaMcpServer.ModelContextProtocol.ToolUsageTools();
        var result = usage.GetToolUsage(toolName: "CompilePlcSoftware");
        Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        var arguments = result.StructuredContent!["data"]!["example"]!["request"]!["params"]!["arguments"]!.AsObject();
        Assert.False(arguments.ContainsKey("softwarePath"));
        Assert.False(arguments.ContainsKey("expectedProjectFile"));
        var sequence = usage.GetToolUsage(exampleId: "sequence/connect-project", exampleKind: "sequence");
        Assert.False(sequence.IsError, sequence.StructuredContent?.ToJsonString());
        var row = sequence.StructuredContent!["data"]!["examples"]!.AsArray().Single()!;
        Assert.True((bool?)row["available"], row.ToJsonString());
        var attach = row["steps"]!.AsArray().Single(step => (string?)step!["tool"] == "AttachOpenProject")!;
        Assert.False(attach["arguments"]!.AsObject().ContainsKey("expectedProjectFile"));
        var legacyUsage = fixture.Shared.Single(tool => tool.ProtocolTool.Name == "GetToolUsage");
        var legacyResult = await legacyUsage.InvokeAsync(Request(legacyUsage, new JsonObject { ["toolName"] = "CompilePlcSoftware" }));
        Assert.False(legacyResult.IsError);
        Assert.False(legacyResult.StructuredContent!["data"]!["example"]!["request"]!["params"]!["arguments"]!.AsObject().ContainsKey("softwarePath"));
    }

    [Fact]
    public async Task Export_alias_translation_preserves_injected_presets()
    {
        var capture = new Capture("ExportPlcTagTable", Schema("ExportPlcTagTable"));
        var tool = new Boundary(capture, "21", presets: Presets);
        var result = await tool.InvokeAsync(Request(tool, new JsonObject {
            ["tagTableName"] = "Main", ["exportFile"] = "C:/Exports/Tags.xml" }));
        Assert.False(result.IsError);
        Assert.Equal("PLC_1", (string?)capture.Last!["softwarePath"]);
        Assert.Equal(Presets["expectedProjectFile"], (string?)capture.Last["expectedProjectFile"]);
        Assert.Equal("C:/Exports/Tags.xml", (string?)capture.Last["exportPath"]);
        Assert.False(capture.Last.ContainsKey("exportFile"));
    }

    [Fact]
    public void Usage_sequence_batch_items_omit_presets_from_each_child_call()
    {
        using var fixture = new Fixture("21", Presets);
        using var session = fixture.Pipeline.EnterSession();
        var usage = new toolpipeline::TiaMcpServer.ModelContextProtocol.ToolUsageTools();
        var result = usage.GetToolUsage(exampleId: "sequence/bulk-plc-objects", exampleKind: "sequence");
        Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        var row = result.StructuredContent!["data"]!["examples"]!.AsArray().Single()!;
        Assert.True((bool?)row["available"], row.ToJsonString());
        var batch = row["steps"]!.AsArray().Single(step => (string?)step!["tool"] == "PreviewToolBatch")!;
        var operations = batch["arguments"]!["operations"]!.AsArray();
        Assert.Equal(5, operations.Count);
        foreach (var operation in operations)
        {
            var arguments = operation!["arguments"]!.AsObject();
            Assert.False(arguments.ContainsKey("softwarePath"));
            Assert.False(arguments.ContainsKey("expectedProjectFile"));
        }
    }

    [Fact]
    public void Serialized_equal_arguments_are_accepted_without_trusting_provenance()
    {
        using var fixture = new Fixture("21", Presets);
        using var session = fixture.Pipeline.EnterSession();
        var effective = Boundary.ApplyDefaults("ListPlcBlocks", Arguments(new JsonObject()), out var error);
        Assert.Null(error);
        Assert.Null(Context.Current.Invoker!.Bind("ListPlcBlocks", effective, out _));
        var copied = new ToolArguments(effective.Json);
        var refusal = Context.Current.Invoker.Bind("ListPlcBlocks", copied, out var bound);
        Assert.Null(refusal);
        Assert.NotNull(bound);
    }

    [Fact]
    public async Task Catalog_boundary_injects_before_admission_and_restores_the_original_request()
    {
        using var fixture = new Fixture("21", Presets);
        using var session = fixture.Pipeline.EnterSession();
        var descriptor = Context.Current.Catalog!.Find("CompilePlcSoftware")!;
        var capture = new Capture(descriptor.Name, descriptor.Tool.InputSchema);
        var admission = new toolpipeline::TiaMcpServer.ModelContextProtocol.InfrastructureInputTool(capture, descriptor);
        var request = Request(admission, new JsonObject { ["dryRun"] = true });
        var original = request.Params;
        var result = await Boundary.InvokeWithDefaults(admission, request, CancellationToken.None);
        Assert.False(result.IsError, result.StructuredContent?.ToJsonString());
        Assert.Equal("PLC_1", (string?)capture.Last!["softwarePath"]);
        Assert.Equal(Presets["expectedProjectFile"], (string?)capture.Last["expectedProjectFile"]);
        Assert.Same(original, request.Params);
        var supplied = Request(admission, new JsonObject { ["softwarePath"] = "Other" });
        Refused(await Boundary.InvokeWithDefaults(admission, supplied, CancellationToken.None), "/softwarePath");
        Assert.Equal(1, capture.Calls);
    }

    [Fact]
    public async Task Live_and_cached_session_state_report_presets_without_an_extra_worker_query()
    {
        using var worker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(worker, "17", false, hostPorts: false, presets: Presets);
        var services = FoundationHostPorts.CreateServices(worker, "17", () => new JsonObject { ["ready"] = true }, null);
        string previousRelease = Context.ReleaseKey;
        string previousProfile = Host.ResolvedProfile();
        try
        {
            using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("17", services, shared, Presets);
            using var session = pipeline.EnterSession();
            var state = pipeline.AllTools.Single(tool => tool.ProtocolTool.Name == "GetSessionState");
            var live = await state.InvokeAsync(Request(state, new JsonObject()));
            Assert.True((bool?)live.StructuredContent!["ok"], live.StructuredContent.ToJsonString());
            Assert.Equal(Presets["expectedProjectFile"], (string?)live.StructuredContent["data"]!["presets"]!["expectedProjectFile"]);
            Assert.Equal("PLC_1", (string?)live.StructuredContent["data"]!["presets"]!["softwarePath"]);
            int calls = worker.Calls;
            SharedSessionState.For(worker).Unsettled.Record("preset-unknown", "CompilePlcSoftware", null, DateTimeOffset.UtcNow);
            var cached = await state.InvokeAsync(Request(state, new JsonObject()));
            Assert.True((bool?)cached.StructuredContent!["ok"], cached.StructuredContent.ToJsonString());
            Assert.True(JsonNode.DeepEquals(live.StructuredContent["data"]!["presets"], cached.StructuredContent["data"]!["presets"]));
            Assert.Equal(calls, worker.Calls);
            Assert.True(JsonNode.DeepEquals(cached.StructuredContent, JsonNode.Parse(((TextContentBlock)cached.Content.Single()).Text)));
        }
        finally { Context.ReleaseKey = previousRelease; Host.SetProfileOverride(previousProfile); }
    }

    private static void Refused(CallToolResult result, string pointer)
    {
        Assert.True(result.IsError);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Equal(pointer, (string?)result.StructuredContent["error"]!["details"]!["parameter"]);
        Assert.Contains("differs from the server preset", (string?)result.StructuredContent["error"]!["message"]);
        Assert.Equal("not-started", (string?)result.StructuredContent["meta"]!["execution"]);
    }

    private static JsonElement CompileSchema() => Schema("CompilePlcSoftware");
    private static JsonElement Schema(string name) => JsonSerializer.SerializeToElement(JsonNode.Parse(
        File.ReadAllText(Path.Combine(Root(), "manifest/contracts/v4/baseline/21.json")))!["tools"]!.AsArray()
        .Single(tool => (string?)tool!["name"] == name)!["inputSchema"]);
    private static ToolArguments Arguments(JsonObject arguments) => new(JsonSerializer.SerializeToElement(arguments));
    private static string Root([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../.."));
    private static RequestContext<CallToolRequestParams> Request(McpServerTool tool, JsonObject arguments) =>
        new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = tool.ProtocolTool.Name,
            Arguments = arguments.Deserialize<Dictionary<string, JsonElement>>() } };

    private sealed class Capture(string name, JsonElement schema) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new() { Name = name, InputSchema = schema };
        internal JsonObject? Last;
        internal int Calls;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = JsonSerializer.SerializeToNode(request.Params!.Arguments)!.AsObject();
            var plan = Last.DeepClone().AsObject(); plan.Remove("dryRun"); plan.Remove("confirm");
            var data = new JsonObject { ["planHash"] = CanonicalJson.Hash(JsonSerializer.SerializeToElement(plan)) };
            var body = JsonNode.Parse(V4Json.Serialize(Envelope.Create(data, null,
                new Meta(DateTimeOffset.UtcNow, "21", name, "preset-test", Outcome.Succeeded, Execution.ReadOnly,
                    BehaviorPolicy.NotApplicable, Completeness.Complete, null, Array.Empty<Warning>()))))!;
            return ValueTask.FromResult(new CallToolResult { IsError = false, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } });
        }
    }

    private sealed class ProjectWorker : IFoundationWorker, toolpipeline::TiaMcp.FoundationHost.IEngineWorker
    {
        internal string? Project;
        internal readonly List<string> Dispatched = new();
        internal JsonObject? Last;
        public bool Faulted => false;
        public object SessionKey { get; } = new();
        public JsonNode? Binding => Project == null ? null : new JsonObject { ["identity"] = new JsonObject { ["projectPath"] = Project } };
        public JsonObject Snapshot() => new() { ["binding"] = Binding, ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No restart expected.");
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lease());
        public Task<toolpipeline::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No native worker dispatch expected.");
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
            => throw new InvalidOperationException("No native call expected.");
        public void Dispose() { }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    private sealed class ProjectCapture(string name, JsonElement schema, string release, ProjectWorker worker) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new() { Name = name, InputSchema = schema };
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            worker.Dispatched.Add(name);
            worker.Last = JsonSerializer.SerializeToNode(request.Params!.Arguments)!.AsObject();
            if (name == "CloseProject" && string.IsNullOrEmpty((string?)worker.Last["secondaryProjectFile"])) worker.Project = null;
            var body = JsonNode.Parse(V4Json.Serialize(Envelope.Create(new { value = true }, null,
                new Meta(DateTimeOffset.UtcNow, release, name, "preset-project", Outcome.Succeeded, Execution.Completed,
                    BehaviorPolicy.NotApplicable, Completeness.Complete, null, Array.Empty<Warning>()))))!;
            return ValueTask.FromResult(new CallToolResult { IsError = false, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } });
        }
    }

    private sealed class ProjectFixture : IDisposable
    {
        internal readonly ProjectWorker Worker = new();
        internal readonly string Project;
        internal readonly string WriteTool;
        private readonly toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline pipeline;
        private readonly IDisposable scope;
        private readonly string previousRelease = Context.ReleaseKey;
        private readonly string previousProfile = Host.ResolvedProfile();
        internal ProjectFixture(string release)
        {
            Project = "C:/Preset/Main.ap" + (release == "14sp1" ? "14" : release == "15.1" ? "15_1" : release);
            WriteTool = release is "14sp1" or "15.1" or "16" or "17" ? "ImportPlcBlock" : "SetPlcProgram";
            Worker.Project = Project;
            Context.ReleaseKey = release; Host.SetProfileOverride("full");
            var presets = new Dictionary<string, string> { ["expectedProjectFile"] = Project, ["softwarePath"] = "PLC_1" };
            var tools = LegacyHostToolRegistry.Create(Worker, release, false, hostPorts: false).ToList();
            foreach (string name in new[] { "CloseProject", "RetrieveProjectArchive", "SaveProjectCopy", "BuildProjectScaffold",
                "OpenProject", "AttachOpenProject", "SaveProject", WriteTool })
            {
                int index = tools.FindIndex(tool => tool.ProtocolTool.Name == name);
                if (index < 0)
                {
                    var rows = JsonNode.Parse(File.ReadAllText(Path.Combine(Root(), "manifest/contracts/v4/baseline", release + ".json")))!["tools"]!.AsArray();
                    var row = rows.FirstOrDefault(row => (string?)row!["name"] == name);
                    if (row == null) continue;
                    tools.Add(new Boundary(new ProjectCapture(name, JsonSerializer.SerializeToElement(row["inputSchema"]), release, Worker), release,
                        presets: presets));
                }
                else tools[index] = new Boundary(new ProjectCapture(name, tools[index].ProtocolTool.InputSchema, release, Worker), release, presets: presets);
            }
            pipeline = new(release, Worker, tools, presets);
            scope = pipeline.EnterSession();
        }
        internal bool HasTool(string name) => pipeline.AllTools.Any(tool => tool.ProtocolTool.Name == name);
        internal async Task<CallToolResult> Call(string name, JsonObject arguments, bool bridge = false)
        {
            var tool = pipeline.AllTools.Single(tool => tool.ProtocolTool.Name == name);
            var request = Request(tool, arguments);
            using var progress = Host.UseProgressRequest(request, CancellationToken.None);
            return bridge ? Host.CallTool(name, Arguments(arguments)) : await tool.InvokeAsync(request);
        }
        public void Dispose()
        { scope.Dispose(); pipeline.Dispose(); Worker.Dispose(); Context.ReleaseKey = previousRelease; Host.SetProfileOverride(previousProfile); }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly HardwareAddressingAdmissionTests.UnboundWorker Worker = new();
        internal readonly toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline Pipeline;
        internal readonly IReadOnlyList<McpServerTool> Shared;
        internal readonly Capture? Capture;
        private readonly string previousRelease = Context.ReleaseKey;
        private readonly string previousProfile = Host.ResolvedProfile();
        internal Fixture(string release, IReadOnlyDictionary<string, string> presets, bool captureListing = false)
        {
            Context.ReleaseKey = release; Host.SetProfileOverride("full");
            var tools = LegacyHostToolRegistry.Create(Worker, release, false, hostPorts: false, presets: presets).ToList();
            if (captureListing)
            {
                int index = tools.FindIndex(tool => tool.ProtocolTool.Name == "ListPlcBlocks");
                using var plainWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
                var plain = LegacyHostToolRegistry.Create(plainWorker, release, false, hostPorts: false);
                Capture = new Capture("ListPlcBlocks", plain.Single(tool => tool.ProtocolTool.Name == "ListPlcBlocks").ProtocolTool.InputSchema);
                tools[index] = new Boundary(Capture, release, presets: presets);
            }
            Shared = tools;
            Pipeline = new(release, Worker, Shared, presets);
        }
        public void Dispose()
        { Pipeline.Dispose(); Worker.Dispose(); Context.ReleaseKey = previousRelease; Host.SetProfileOverride(previousProfile); }
    }
}
