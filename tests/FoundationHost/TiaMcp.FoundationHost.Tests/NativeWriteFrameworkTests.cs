extern alias toolpipeline;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;

public sealed class NativeWriteFrameworkTests
{
    [Theory]
    [InlineData("preview", "Startup", 0)]
    [InlineData("refused", "Startup", 0)]
    [InlineData("db", "DBAccessibleFromOPCUA", 0)]
    [InlineData("mismatch", "MCP_RC_DBAttr", 0)]
    [InlineData("missing", "Missing.s7dcl", 0)]
    [InlineData("unreadable-format", "file format", 0)]
    [InlineData("target", "exact mapped PLC block", 0)]
    [InlineData("orphan", "exact mapped PLC block", 0)]
    [InlineData("startup-SimaticSD", "Startup", 0)]
    [InlineData("startup-SimaticSd", "Startup", 0)]
    [InlineData("startup-s7dcl", "Startup", 0)]
    [InlineData("startup-Unknown", "Startup", 0)]
    [InlineData("startup-", "Startup", 0)]
    [InlineData("startup-SimaticML2", "Startup", 0)]
    [InlineData("startup-vendor.xml", "Startup", 0)]
    [InlineData("startup-liblink-custom", "Startup", 0)]
    [InlineData("missing-SimaticSd", "Missing.s7dcl", 0)]
    [InlineData("missing-Unknown", "Missing.s7dcl", 0)]
    [InlineData("missing-", "Missing.s7dcl", 0)]
    [InlineData("elsewhere", "another group", 0)]
    [InlineData("changed", "changed after admission", 1)]
    public void Workbench_refused_SD_mappings_never_synchronize(string scenario, string reason, int exports)
    {
        using var inputs = new Inputs();
        var probe = Run("sd-restore-" + scenario, Path.GetDirectoryName(inputs.Nodes)!);
        Assert.Equal("refused", (string?)probe["item"]?["Outcome"]);
        Assert.Contains(reason, (string?)probe["item"]?["Error"]);
        Assert.Equal(0, (int?)probe["Calls"]);
        Assert.Equal(0, (int?)probe["StatusReads"]);
        Assert.False((bool?)probe["connectionLost"]);
        Assert.Equal(exports, (int?)probe["Exports"]);
        Assert.Empty(probe["Setters"]!.AsArray());
    }

    [Theory]
    [InlineData("plan", "would sync WorkspaceToProject", 0)]
    [InlineData("xml", null, 0)]
    [InlineData("nonblock", null, 0)]
    [InlineData("aggregate", null, 0)]
    [InlineData("tags", null, 0)]
    [InlineData("hmi", null, 0)]
    [InlineData("startup-SimaticML", null, 0)]
    [InlineData("startup-sIMAtiCmL", null, 0)]
    [InlineData("startup-XML", null, 0)]
    [InlineData("startup-LIBLINK", null, 0)]
    [InlineData("liblink", null, 0)]
    [InlineData("format", "synchronized", 1)]
    [InlineData("absolute", "synchronized", 1)]
    [InlineData("restored", "synchronized", 1)]
    [InlineData("opaque", "synchronized", 1)]
    [InlineData("failure", "failed", 1)]
    [InlineData("unknown", "unknown", 1)]
    public void Workbench_verifies_the_mapped_block_after_one_SD_restore(string scenario, string? outcome, int calls)
    {
        using var inputs = new Inputs();
        var probe = Run("sd-restore-" + scenario, Path.GetDirectoryName(inputs.Nodes)!);
        Assert.Equal(outcome, (string?)probe["item"]?["Outcome"]);
        Assert.Equal(calls, (int?)probe["Calls"]);
        Assert.Equal(calls, (int?)probe["Exports"]);
        Assert.Equal(calls, (int?)probe["StatusReads"]);
        Assert.Equal(scenario is "nonblock" or "aggregate" or "tags" or "hmi" ? 0 : 1, (int?)probe["FormatReads"]);
        if (outcome == null) Assert.Empty(probe["Setters"]!.AsArray());
        if (outcome == "synchronized")
        {
            Assert.Equal(9810, (int?)probe["Number"]);
            Assert.Equal(new[] { "AutoNumber=False", "Number=9810", "AutoNumber=True" }, probe["Setters"]!.AsArray().Select(n => (string?)n));
        }
        if (scenario == "opaque") Assert.Equal("VendorValue", (string?)Assert.Single(probe["item"]!["NotCompared"]!.AsArray()));
        if (outcome == "failed") Assert.Contains("DBAccessibleFromOPCUA", (string?)probe["item"]?["Error"]);
        if (outcome == "unknown") Assert.Contains("MemoryReserve is unreadable", (string?)probe["item"]?["Error"]);
        if (outcome is "unknown" or "failed") Assert.Contains("Recovery:", (string?)probe["item"]?["Error"]);
    }

    [Theory]
    [InlineData("format-loss", 0)]
    [InlineData("backup-loss", 1)]
    public void Workbench_connection_loss_before_dispatch_signals_a_batch_stop(string scenario, int exports)
    {
        using var inputs = new Inputs();
        var probe = Run("sd-restore-" + scenario, Path.GetDirectoryName(inputs.Nodes)!);
        Assert.Equal("refused", (string?)probe["item"]?["Outcome"]);
        Assert.True((bool?)probe["connectionLost"]);
        Assert.Equal(0, (int?)probe["Calls"]);
        Assert.Equal(0, (int?)probe["StatusReads"]);
        Assert.Equal(exports, (int?)probe["Exports"]);
    }

    [Theory]
    [InlineData("unequal", "failed", "Unequal")]
    [InlineData("missing", "failed", "WorkspaceFileMissing")]
    [InlineData("unknown", "failed", "Unknown")]
    [InlineData("unreadable", "unknown", "GetStatus unavailable")]
    [InlineData("unreadable-difference", "unknown", "GetStatus unavailable")]
    public void Workbench_requires_an_Equal_post_sync_status(string scenario, string outcome, string diagnostic)
    {
        using var inputs = new Inputs();
        var probe = Run("sd-restore-status-" + scenario, Path.GetDirectoryName(inputs.Nodes)!);
        Assert.Equal(1, (int?)probe["Calls"]);
        Assert.Equal(1, (int?)probe["StatusReads"]);
        Assert.Equal(9810, (int?)probe["Number"]);
        Assert.Equal(9810, (int?)probe["numberAtStatus"]);
        Assert.Equal(outcome, (string?)probe["item"]?["Outcome"]);
        string? error = (string?)probe["item"]?["Error"];
        Assert.Contains(diagnostic, error);
        Assert.Contains("Recovery:", error);
        if (outcome == "failed") Assert.Contains("not-verified", error);
        if (scenario == "unreadable-difference") Assert.Contains("DBAccessibleFromOPCUA", error);
    }

    [Theory]
    [InlineData("to-null")]
    [InlineData("from-null")]
    public void Workbench_reports_nullness_changes_even_for_uncomparable_values(string scenario)
    {
        using var inputs = new Inputs();
        var probe = Run("sd-restore-" + scenario, Path.GetDirectoryName(inputs.Nodes)!);
        Assert.Equal(1, (int?)probe["Calls"]);
        Assert.Equal(1, (int?)probe["StatusReads"]);
        Assert.Equal("failed", (string?)probe["item"]?["Outcome"]);
        Assert.Equal("VendorValue", (string?)Assert.Single(probe["item"]!["NotCompared"]!.AsArray()));
        Assert.Contains("VendorValue", (string?)probe["item"]?["Error"]);
    }

    [Fact]
    public void Scoped_import_lists_opaque_attributes_without_claiming_preservation()
    {
        using var inputs = new Inputs();
        var meta = Run("sd-scoped-optional", Path.GetDirectoryName(inputs.Nodes)!)["reply"]!["Meta"]!;
        Assert.Equal("VendorValue", (string?)Assert.Single(meta["notCompared"]!.AsArray()));
        Assert.Null(meta["propertiesPreserved"]);
        Assert.Empty(meta["propertyDifferences"]!.AsArray());
    }

    [Fact]
    public void Sd_preview_reports_a_blocking_reason_without_a_backup_or_native_import()
    {
        using var inputs = new Inputs();
        var probe = Run("sd-preview", Path.GetDirectoryName(inputs.Nodes)!);
        var evidence = probe["evidence"]!;
        Assert.True((bool?)evidence["applyBlocked"]);
        Assert.Contains("Startup", (string?)Assert.Single(evidence["applyBlockedReasons"]!.AsArray()));
        Assert.Equal("Startup", (string?)evidence["existingBlock"]?["kind"]);
        Assert.Equal(0, (int?)probe["imports"]);
        Assert.Equal(0, (int?)probe["postReads"]);
        Assert.Equal(0, (int?)probe["exports"]);
    }

    [Theory]
    [InlineData("sd-new")]
    [InlineData("sd-backup-refusal")]
    public void Sd_creation_reads_actual_properties_once_and_a_failed_backup_prevents_import(string scenario)
    {
        using var inputs = new Inputs();
        var probe = Run(scenario, Path.GetDirectoryName(inputs.Nodes)!);
        var evidence = probe["evidence"]!;
        if (scenario == "sd-new")
        {
            Assert.Equal(1, (int?)probe["imports"]);
            Assert.Equal(1, (int?)probe["postReads"]);
            Assert.Equal(0, (int?)probe["exports"]);
            Assert.True((bool?)evidence["operationSuccess"]);
            Assert.Null(evidence["existingBlock"]);
            Assert.Equal("GlobalDB", (string?)evidence["importedBlock"]?["kind"]);
            Assert.Equal(1, (int?)evidence["importedBlock"]?["number"]);
        }
        else
        {
            Assert.Equal(0, (int?)probe["imports"]);
            Assert.Equal(0, (int?)probe["postReads"]);
            Assert.Equal(1, (int?)probe["exports"]);
            Assert.False((bool?)evidence["operationSuccess"]);
            Assert.Equal("backup-unavailable", (string?)evidence["condition"]);
        }
        Assert.Empty(probe["Setters"]!.AsArray());
    }

    [Theory]
    [InlineData("sd-restored", 1, 1, true)]
    [InlineData("sd-inconsistent", 1, 0, true)]
    [InlineData("sd-setter", 1, 1, false)]
    [InlineData("sd-before-unreadable", 0, 0, false)]
    [InlineData("sd-after-unreadable", 1, 1, false)]
    [InlineData("sd-batch", 0, 0, false)]
    public void Sd_import_verifies_the_fresh_number_and_refuses_the_entire_batch_before_backup(string scenario, int imports, int exports, bool ok)
    {
        using var inputs = new Inputs();
        var probe = Run(scenario, Path.GetDirectoryName(inputs.Nodes)!);
        Assert.Equal(imports, (int?)probe["imports"]);
        Assert.Equal(exports, (int?)probe["exports"]);
        var evidence = probe["evidence"]!;
        Assert.Equal(ok, (bool?)evidence["operationSuccess"]);
        if (imports == 0)
        {
            Assert.Equal("sd-override-not-preserved", (string?)evidence["condition"]);
            Assert.Contains(scenario == "sd-batch" ? "Startup" : "IsIECCheckEnabled", (string?)evidence["error"]);
            Assert.Empty(probe["Setters"]!.AsArray());
        }
        else if (scenario == "sd-after-unreadable")
        {
            Assert.Contains("unreadable", (string?)evidence["error"]);
            Assert.Null(evidence["propertiesPreserved"]);
            Assert.Empty(probe["Setters"]!.AsArray());
        }
        else
        {
            Assert.Equal(new[] { "AutoNumber=False", "Number=1", "AutoNumber=True" }, probe["Setters"]!.AsArray().Select(v => (string?)v));
            Assert.True((bool?)probe["AutoNumber"]);
            Assert.Equal(ok, (bool?)evidence["numberRestored"]);
            Assert.Equal(ok, (bool?)evidence["propertiesPreserved"]);
            Assert.Equal(ok ? 1 : 123, (int?)evidence["importedBlock"]?["number"]);
            if (exports == 0) Assert.Contains("inconsistent", (string?)evidence["backupWarning"]);
            else Assert.True(File.Exists(Path.Combine((string)evidence["recoveryDirectory"]!, "000.xml")));
            if (!ok)
            {
                Assert.Equal("BlockPropertiesNotPreserved", (string?)evidence["status"]);
                var difference = Assert.Single(evidence["propertyDifferences"]!.AsArray())!;
                Assert.Equal("Number", (string?)difference["name"]);
                Assert.Equal(1, (int?)difference["before"]);
                Assert.Equal(123, (int?)difference["after"]);
                Assert.Equal("Number setter refused.", (string?)difference["restoreError"]);
            }
        }
    }

    [Fact]
    public void Batch_restores_the_snapshot_from_immediately_before_the_second_import()
    {
        using var inputs = new Inputs();
        var probe = Run("sd-batch-refresh", Path.GetDirectoryName(inputs.Nodes)!);
        Assert.Equal(42, (int?)probe["admitted"]);
        Assert.Equal(88, (int?)probe["evidence"]?["existingBlock"]?["number"]);
        Assert.Equal(88, (int?)probe["evidence"]?["importedBlock"]?["number"]);
        Assert.Equal(88, (int?)probe["Number"]);
        Assert.Equal(2, (int?)probe["Exports"]);
        Assert.Equal(new[] { "AutoNumber=False", "Number=88", "AutoNumber=True" }, probe["Setters"]!.AsArray().Select(v => (string?)v));
        foreach (var (field, number) in new[] { ("initialRecoveryDirectory", 42), ("recoveryDirectory", 88) })
        {
            string backup = Path.Combine((string)probe["evidence"]![field]!, "000.xml");
            Assert.Equal(number, (int)XDocument.Load(backup).Descendants("SW.Blocks.GlobalDB").Single().Element("AttributeList")!.Element("Number")!);
        }
    }

    [Theory]
    [InlineData("sd-scoped-startup", "sd-override-not-preserved", "Startup", 0)]
    [InlineData("sd-scoped-instance", "sd-override-not-preserved", "MotorFB", 0)]
    [InlineData("sd-scoped-instance-none", "sd-override-not-preserved", "MotorFB", 0)]
    [InlineData("sd-scoped-required", "sd-override-not-preserved", "MemoryReserve", 0)]
    [InlineData("sd-scoped-change-code", "sd-input-changed", "changed", 1)]
    [InlineData("sd-scoped-change-resource", "sd-input-changed", "changed", 1)]
    [InlineData("sd-scoped-add-resource", "sd-input-changed", "changed", 1)]
    [InlineData("sd-scoped-remove-resource", "sd-input-changed", "changed", 1)]
    public void Scoped_document_refusal_never_reaches_the_native_import(string scenario, string condition, string reason, int exports)
    {
        using var inputs = new Inputs();
        var probe = Run(scenario, Path.GetDirectoryName(inputs.Nodes)!);
        Assert.Equal(0, (int?)probe["calls"]);
        Assert.Equal(exports, (int?)probe["Exports"]);
        Assert.False((bool?)probe["reply"]?["Meta"]?["mayHaveChanged"]);
        Assert.Equal(condition, (string?)probe["reply"]?["Meta"]?["condition"]);
        Assert.Contains(reason, (string?)probe["reply"]?["Message"]);
        if (scenario == "sd-scoped-startup")
        {
            var target = probe["reply"]!["Meta"]!["existingBlock"]!;
            Assert.Equal("MCP_RC_OBStart", (string?)target["name"]);
            Assert.Equal(151, (int?)target["number"]);
        }
    }

    [Theory]
    [InlineData("sd-scoped-auto-true", true)]
    [InlineData("sd-scoped-auto-false", false)]
    public void Scoped_override_restores_only_the_changed_auto_number(string scenario, bool expected)
    {
        using var inputs = new Inputs();
        var probe = Run(scenario, Path.GetDirectoryName(inputs.Nodes)!);
        var meta = probe["reply"]!["Meta"]!;
        Assert.Equal(1, (int?)probe["calls"]);
        Assert.Equal("AutoNumber=" + expected, (string?)Assert.Single(probe["Setters"]!.AsArray()));
        Assert.True((bool?)meta["propertiesPreserved"]);
        Assert.True((bool?)meta["numberRestored"]);
        Assert.Equal(expected, (bool?)meta["importedBlock"]?["autoNumber"]);
        Assert.Equal(42, (int?)meta["importedBlock"]?["number"]);
    }

    [Theory]
    [InlineData("sd-scoped-new-number", 1)]
    [InlineData("sd-scoped-wrong-number", 42)]
    [InlineData("sd-scoped-number", 150)]
    [InlineData("sd-scoped-unrequested-auto", 150)]
    [InlineData("sd-scoped-optional", 42)]
    public void Scoped_import_reports_number_and_attribute_outcomes(string scenario, int number)
    {
        using var inputs = new Inputs();
        var probe = Run(scenario, Path.GetDirectoryName(inputs.Nodes)!);
        var meta = probe["reply"]!["Meta"]!;
        Assert.Equal(1, (int?)probe["calls"]);
        Assert.True((bool?)meta["writeReturned"]);
        Assert.Equal(number, (int?)meta["importedBlock"]?["number"]);
        Assert.Empty(probe["Setters"]!.AsArray());
        if (scenario is "sd-scoped-wrong-number" or "sd-scoped-unrequested-auto")
        {
            Assert.Equal("BlockPropertiesNotPreserved", (string?)meta["status"]);
            Assert.False((bool?)meta["operationSuccess"]);
            var diff = Assert.Single(meta["propertyDifferences"]!.AsArray())!;
            Assert.Equal(scenario == "sd-scoped-wrong-number" ? "Number" : "AutoNumber", (string?)diff["name"]);
        }
        else
        {
            Assert.Empty(meta["propertyDifferences"]!.AsArray());
            Assert.Null(meta["status"]);
            Assert.Contains("existence verified", (string?)probe["reply"]?["Message"]);
            if (scenario == "sd-scoped-new-number")
            {
                string warning = (string)meta["warning"]!;
                Assert.Contains("150", warning);
                Assert.Contains("assigned 1", warning);
                var response = new Host.ResponseMessage { Message = (string)probe["reply"]!["Message"]!, Meta = meta.DeepClone().AsObject() };
                var result = Host.OfflineContracts.Map("ManagePlcBlockDocuments", response, true, true).StructuredContent!;
                Assert.True((bool?)result["ok"]);
                Assert.Equal(1, (int?)result["data"]?["importedBlock"]?["number"]);
                Assert.Contains(result["meta"]!["warnings"]!.AsArray(), item => (string?)item?["message"] == warning);
            }
            else if (scenario == "sd-scoped-optional")
            {
                Assert.Null(meta["propertiesPreserved"]);
                Assert.Contains("VendorValue", meta["notCompared"]!.ToJsonString());
            }
            else Assert.True((bool?)meta["propertiesPreserved"]);
            if (scenario == "sd-scoped-number") Assert.False((bool?)meta["importedBlock"]?["autoNumber"]);
            if (scenario == "sd-scoped-optional") Assert.Equal("System.Object", (string?)meta["importedBlock"]?["attributes"]?["VendorValue"]);
        }
    }

    [Theory]
    [InlineData("opc-cleaned", false, 0)]
    [InlineData("opc-retained", true, 1)]
    [InlineData("opc-no-create", false, 0)]
    public void Opc_import_reports_the_verified_cleanup_on_net48(string scenario, bool unknown, int remaining)
    {
        using var input = new Inputs();
        var probe = Run(scenario, input.Nodes);
        Assert.Equal(unknown ? "unknown" : "failed", PublicImportOutcome(probe, "opc", input.Nodes));
        Assert.Equal(unknown, (bool?)probe["reply"]?["OutcomeUnknown"]);
        Assert.Equal(unknown, (bool?)probe["reply"]?["Meta"]?["mayHaveChanged"]);
        Assert.Equal(remaining, (int?)probe["remaining"]);
        var message = (string)probe["reply"]!["Message"]!;
        if (scenario == "opc-no-create") Assert.Contains("Create(string)", message);
        else Assert.Contains("OPC UA import interrupted", message);
        Assert.Equal(scenario == "opc-cleaned", message.Contains("removed again"));
    }

    [Theory, InlineData("opc-create-refused", false, 0), InlineData("opc-create-interrupted", true, 1)]
    public void Opc_creation_refusal_requires_a_fresh_unchanged_inventory_on_net48(string scenario, bool unknown, int count)
    {
        using var input = new Inputs();
        var probe = Run(scenario, input.Nodes);
        Assert.Equal(unknown ? "unknown" : "failed", PublicImportOutcome(probe, "opc", input.Nodes));
        Assert.Equal(unknown, (bool?)probe["reply"]?["OutcomeUnknown"]);
        Assert.Equal(unknown, (bool?)probe["reply"]?["Meta"]?["mayHaveChanged"]);
        Assert.Equal(count, (int?)probe["remaining"]);
        Assert.Contains("creation interrupted", (string?)probe["reply"]?["Message"]);
    }

    [Theory]
    [InlineData("alarm-class", true)][InlineData("alarm-class-missing", false)]
    [InlineData("alarm-text", true)][InlineData("alarm-text-missing", false)]
    public void Alarm_import_distinguishes_admission_from_provider_failure_on_net48(string scenario, bool issued)
    {
        using var inputs = new Inputs();
        string file = scenario.StartsWith("alarm-class") ? inputs.Classes : inputs.Workbook;
        var probe = Run(scenario, issued ? file : file + ".missing");
        Assert.Equal(issued ? 1 : 0, (int?)probe["calls"]);
        Assert.Equal(issued, (bool?)probe["reply"]?["Meta"]?["mayHaveChanged"]);
        Assert.Equal(issued, (bool?)probe["reply"]?["OutcomeUnknown"]);
    }

    public static TheoryData<string, string, bool> ImportFailures
    {
        get
        {
            var cases = new TheoryData<string, string, bool>();
            foreach (var site in new[] { "alarm-class", "alarm-text", "opc-existing" })
            {
                foreach (var refusal in new[] { "licence", "products", "security", "multiuser" }) cases.Add(site, refusal, false);
                foreach (var failure in new[] { "malformed", "native-malformed", "lost", "abort", "disposed" }) cases.Add(site, failure, true);
            }
            cases.Add("opc-cleaned", "security", false);
            cases.Add("opc-retained", "security", true);
            return cases;
        }
    }

    [Theory, MemberData(nameof(ImportFailures))]
    public void Issued_imports_distinguish_refusals_from_uncertain_failures_on_net48(string site, string failure, bool unknown)
    {
        using var inputs = new Inputs();
        var file = site switch { "alarm-class" => inputs.Classes, "alarm-text" => inputs.Workbook, _ => inputs.Nodes };
        var probe = Run(site + "/" + failure, file);
        Assert.Equal(unknown ? "unknown" : "failed", PublicImportOutcome(probe, site, file));
        Assert.Equal(unknown, (bool?)probe["reply"]?["OutcomeUnknown"]);
        Assert.Equal(unknown, (bool?)probe["reply"]?["MayHaveChanged"]);
        Assert.Equal(unknown, (bool?)probe["reply"]?["Meta"]?["mayHaveChanged"]);
        Assert.Equal(unknown, (bool?)probe["reply"]?["Meta"]?["writeOutcomeUnknown"]);
        Assert.Equal(1, (int?)probe["calls"]);
        Assert.Equal(site is "opc-existing" or "opc-retained" ? 1 : 0, (int?)probe["remaining"]);
    }

    [Theory, InlineData("alarm-class", "AlarmClassExportImportResultState"), InlineData("alarm-text", "TextListXlsxResultState")]
    public void Alarm_provider_returned_error_retains_native_result_evidence_on_net48(string site, string stateType)
    {
        using var inputs = new Inputs();
        var file = site == "alarm-class" ? inputs.Classes : inputs.Workbook;
        var probe = Run(site + "/error-result", file);
        Assert.Equal("unknown", PublicImportOutcome(probe, site, file));
        var meta = probe["reply"]!["Meta"]!;
        Assert.Equal("Error", (string?)meta["nativeState"]);
        Assert.True((bool?)meta["nativeResultReturned"]);
        Assert.True((bool?)meta["mayHaveChanged"]);
        Assert.Equal("Siemens.Engineering.SW.Alarm." + stateType, (string?)meta["nativeStateType"]);
        Assert.Equal(1, (int?)probe["calls"]);
        Assert.Contains("State=Error", (string?)probe["reply"]?["Message"]);
        if (site == "alarm-class")
        {
            Assert.Equal(1, (int?)meta["errorCount"]);
            Assert.Equal("Error", (string?)Assert.Single(meta["messages"]!.AsArray())!["state"]);
        }
        else Assert.False((bool?)meta["success"]);
    }

    [Theory, InlineData("dyn-create", 0), InlineData("dyn-entry", 1)]
    public void Dynamization_remains_after_the_scalar_is_restored_on_net48(string scenario, int entries)
    {
        var probe = Run(scenario, "");
        Assert.Equal(1, (int?)probe["remaining"]);
        Assert.Equal(0, (int?)probe["offset"]);
        Assert.Equal(entries, (int?)probe["entries"]);
        Assert.True((bool?)probe["meta"]?["writeOutcomeUnknown"], probe.ToJsonString());
        Assert.False((bool?)probe["meta"]?["rollbackCompleted"]);
    }

    [Theory, InlineData("mapping-create"), InlineData("mapping-update")]
    public void Mapping_rule_retains_the_target_after_scalar_rollback_on_net48(string scenario)
    {
        var probe = Run(scenario, "");
        Assert.Equal(1, (int?)probe["remaining"]);
        Assert.Equal(0, (int?)probe["begin"]);
        Assert.Equal("Device/module", (string?)probe["target"]);
        Assert.True((bool?)probe["meta"]?["writeOutcomeUnknown"], probe.ToJsonString());
        Assert.False((bool?)probe["meta"]?["rollbackCompleted"]);
    }

    [Theory, InlineData("dyn-refused"), InlineData("mapping-refused"), InlineData("target-refused")]
    public void Verified_native_refusals_keep_a_definite_outcome_on_net48(string scenario)
    {
        var probe = Run(scenario, "");
        Assert.Equal(scenario == "target-refused" ? 1 : 0, (int?)probe["remaining"]);
        Assert.False((bool?)probe["meta"]?["mayHaveChanged"] ?? false, probe.ToJsonString());
        Assert.False((bool?)probe["meta"]?["writeOutcomeUnknown"] ?? false);
        Assert.Contains("refused", (string?)probe["message"], StringComparison.OrdinalIgnoreCase);
    }

    private static string? PublicImportOutcome(JsonNode probe, string site, string file)
    {
        using var release = Host.HardwareContract.UseRelease("19");
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.PlcB5PortService((_, _) => probe["reply"]!.DeepClone(), () => "C:/fixture.ap19");
        var tool = site switch { "alarm-class" => "ImportAlarmClasses", "alarm-text" => "ImportAlarmTextLists", _ => "ImportOpcUaInterface" };
        var result = Host.EngineeringToolContract.Run(tool, true, () => tool switch {
            "ImportAlarmClasses" => service.ImportAlarmClasses("PLC", file),
            "ImportAlarmTextLists" => service.ImportAlarmTextLists("PLC", file),
            _ => service.ImportOpcUaInterface("PLC", file)
        });
        return (string?)result.StructuredContent?["meta"]?["outcome"];
    }

    private static JsonNode Run(string scenario, string file)
        => SafetyLoginFrameworkTests.RunFixture("native-writes", scenario, new JsonObject { ["file"] = file })["probe"]!;

    private sealed class Inputs : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("native-import-");
        internal string Nodes => Path.Combine(root.FullName, "Model.xml");
        internal string Classes => Path.Combine(root.FullName, "classes.dat");
        internal string Workbook => Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "tests", "FoundationHost", "TiaMcp.ToolPipeline.Tests", "Fixtures", "TableExchange", "inline-text.xlsx");
        internal Inputs()
        {
            File.WriteAllText(Nodes, "<UANodeSet xmlns='http://opcfoundation.org/UA/2011/03/UANodeSet.xsd'/>");
            // The fault is at provider dispatch, before the provider decodes its proprietary DAT payload.
            File.WriteAllBytes(Classes, Array.Empty<byte>());
        }
        public void Dispose() => root.Delete(true);
    }
}
