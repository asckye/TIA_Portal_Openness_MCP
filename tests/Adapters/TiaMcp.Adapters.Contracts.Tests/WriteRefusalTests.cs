using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Security;
using TiaMcp.Adapters.Native.Vci;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class WriteRefusalTests
{
    [Theory, InlineData(false), InlineData(true)]
    public void Permission_refusal_checks_a_new_user_proxy_and_detects_unrestored_bits(bool changed)
    {
        const string name = "Operator";
        var fixture = HardwareWriteFailureTests.NativeFixture();
        var stale = new Siemens.Engineering.HW.WebserverUser { UserName = name, Bits = Siemens.Engineering.HW.WebserverUserPermissions.ReadTag };
        var native = new Siemens.Engineering.HW.WebserverUser { UserName = name, Bits = stale.Bits };
        stale.Set = value => {
            if (changed) native.Bits = value;
            throw new InvalidOperationException("Permissions refused.");
        };
        native.Set = value => throw new InvalidOperationException("Restore refused.");
        int reads = 0;
        fixture.Cpu.Services[typeof(Siemens.Engineering.HW.Features.WebserverUserManagement)] =
            new Siemens.Engineering.HW.Features.WebserverUserManagement {
                ReadUsers = () => new Siemens.Engineering.HW.WebserverUserComposition { reads++ == 0 ? stale : native }
            };
        var request = new DeviceUserRequest { Permissions = new[] { "ReadFiles" } };
        var adapter = new DeviceUsersAdapter(new SecuritySession(fixture.Engine, request.Faults), request);
        var reply = adapter.ManageDeviceUsers(new[] { "PLC" }, new[] { "CPU" }, "webserver", "setPermissions", name, dryRun: false);
        Assert.True(reads >= 2);
        Assert.Equal(changed, reply.OutcomeUnknown);
        Assert.Equal(changed, reply.MayHaveChanged);
        Assert.Equal("Permissions refused.", reply.Meta["nativeMessage"]);
    }

    private sealed class Area(string name)
    {
        public string Name => name;
        internal Action? DeleteNative;
        public void Delete() => DeleteNative?.Invoke();
    }

    [Theory, InlineData("refused"), InlineData("inserted"), InlineData("unrestored")]
    public void Transfer_area_create_refusal_checks_the_fresh_named_composition(string outcome)
    {
        var existing = new Area("area");
        var native = new List<Area> { existing };
        var stale = native.ToArray();
        int probes = 0;
        var reply = MutationEvidenceTests.RunStep("21", "ManageTransferArea", meta => {
            PlcFoundationEngine.JournalTransferAreaCreate(() => { probes++; return native.ToArray(); }, "Area");
            meta["mayHaveChanged"] = true;
            if (outcome != "refused")
            {
                var created = new Area("Area");
                created.DeleteNative = () => { if (outcome == "inserted") native.Remove(created); };
                native.Add(created);
            }
            throw new InvalidOperationException("TIA refused transfer area Create.");
        });
        Assert.True(probes > 0);
        Assert.Same(existing, Assert.Single(stale));
        Assert.Contains(existing, native);
        Assert.Equal(outcome == "unrestored", reply.OutcomeUnknown);
        Assert.Equal(outcome != "unrestored", (bool?)reply.Meta["rollbackCompleted"]);
        Assert.Equal("TIA refused transfer area Create.", reply.Meta["nativeMessage"]);
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    public void Syslog_create_checks_the_selected_CPU_in_preview_and_apply(bool enabled, bool dryRun)
    {
        var fixture = HardwareWriteFailureTests.NativeFixture();
        fixture.Syslog.EnableSystemLogging = enabled;
        var composition = fixture.Syslog.SysLogServerConfiguration;
        composition.CreateFailure = new InvalidOperationException("TIA refused server.");
        var session = new SecuritySession(fixture.Engine, new Dictionary<string, SecurityValidationFault>());
        var adapter = new SecurityDeepAdapter(session, new SecurityDeepRequest());
        var reply = adapter.ManageSyslogServers(scope: "plc", action: "createServer", devicePathJson: new[] { "PLC" }, itemPathJson: new[] { "CPU" },
            serverAddress: "192.0.2.1", serverPort: 514, dryRun: dryRun);
        Assert.Equal(enabled && !dryRun ? 1 : 0, composition.CreateCalls);
        Assert.False(reply.MayHaveChanged);
        Assert.False(reply.OutcomeUnknown);
        if (!enabled) Assert.Equal("system-logging-disabled", reply.Meta["precondition"]);
        else if (dryRun) Assert.Equal(true, reply.Meta["operationSuccess"]);
        else
        {
            Assert.Equal("restored", reply.Meta["rollbackOutcome"]);
            Assert.Equal("TIA refused server.", reply.Meta["nativeMessage"]);
        }
    }

    private static Dictionary<string, object?> TypeRow(string state) => new() {
        ["name"] = "Valve", ["guid"] = "123", ["versions"] = new List<object?> {
            new Dictionary<string, object?> { ["versionNumber"] = "1.0.0", ["guid"] = "456", ["state"] = state } }
    };

    [Theory, InlineData("unchanged"), InlineData("versionChanged"), InlineData("missing"), InlineData("readError")]
    public void Library_delete_refusal_compares_the_fresh_type_and_versions(string outcome)
    {
        var before = TypeRow("InWork");
        var reply = MutationEvidenceTests.RunStep("21", "ManageLibraryType", meta => {
            EngineeringWriteJournal.Deleting(() => {
                if (outcome == "missing") return null;
                var after = TypeRow(outcome == "versionChanged" ? "Committed" : "InWork");
                if (outcome == "readError") after["statusError"] = "Access refused";
                return after;
            }, before, "Valve");
            meta["mayHaveChanged"] = true;
            throw new InvalidOperationException("The type has an InWork version.");
        });
        Assert.Equal(outcome != "unchanged", reply.OutcomeUnknown);
        Assert.Equal(outcome != "unchanged", reply.MayHaveChanged);
        Assert.Equal("The type has an InWork version.", reply.Meta["nativeMessage"]);
    }

    [Fact]
    public void Tag_snapshot_exports_to_an_absolute_session_output_directory()
    {
        string? destination = null;
        var fingerprint = UnifiedExchangeLogic.ExportFingerprint(directory => {
            destination = directory.ToString();
            var file = new FileInfo(Path.Combine(directory.FullName, "Tags.hmi.yml"));
            File.WriteAllText(file.FullName, "Tags:\n  - Name: Counter\n");
            return new[] { file };
        });
        Assert.NotNull(destination);
        Assert.True(Path.IsPathFullyQualified(destination));
        Assert.Equal(Path.GetFullPath(TiaOpenness.Shared.DataLocations.Current.OutputRoot), Directory.GetParent(destination)!.FullName);
        Assert.NotEmpty(fingerprint);
        Assert.False(Directory.Exists(destination));
    }

    [Theory, InlineData("unchanged"), InlineData("overwritten"), InlineData("missingSideFile"), InlineData("beforeFailed"),
        InlineData("afterFailed"), InlineData("onlySideFiles")]
    public void Tag_import_needs_the_same_complete_table_export_after_a_native_failure(string outcome)
    {
        int exports = 0;
        var reply = MutationEvidenceTests.RunStep("21", "ExchangeUnifiedTags", meta => {
            Dictionary<string, string> Export() => UnifiedExchangeLogic.ExportFingerprint(directory => {
                bool after = ++exports > 1;
                if (outcome == (after ? "afterFailed" : "beforeFailed")) throw new IOException("Tag export failed.");
                var reported = new FileInfo(Path.Combine(directory.FullName, "Tags"));
                File.WriteAllText(reported.FullName + ".hmi.yml", "Tags:\n  - Name: Counter\n");
                if (!after || outcome != "missingSideFile")
                    File.WriteAllText(Path.Combine(directory.FullName, "NameData.yml"), "Counter: " + (after && outcome == "overwritten" ? "B" : "A"));
                return outcome == "onlySideFiles" ? new[] { new FileInfo(Path.Combine(directory.FullName, "NameData.yml")) } : new[] { reported };
            });
            UnifiedExchangeLogic.JournalTagImport(Export, new HmiObject());
            meta["mayHaveChanged"] = true;
            meta["tagCountBefore"] = 1; meta["tagCountAfter"] = 1;
            throw new InvalidOperationException("Tag import refused.");
        });
        Assert.Equal(outcome is "beforeFailed" or "onlySideFiles" ? 1 : 2, exports);
        Assert.Equal(outcome != "unchanged", reply.OutcomeUnknown);
        Assert.Equal(outcome != "unchanged", reply.MayHaveChanged);
        Assert.Equal("Tag import refused.", reply.Meta["nativeMessage"]);
    }

    public static HardwareAddressingReply RefusedAutomationMlImport()
    {
        var fixture = HardwareWriteFailureTests.NativeFixture("18");
        var provider = new Siemens.Engineering.Cax.CaxProvider {
            WriteLog = log => File.WriteAllText(log.FullName, "CAx import returned false. " + new string('x', 5000) + "end of native log")
        };
        fixture.Project.Services[typeof(Siemens.Engineering.Cax.CaxProvider)] = provider;
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aml-fixture-" + Guid.NewGuid().ToString("N")));
        try
        {
            var source = Path.Combine(directory.FullName, "device.aml");
            File.WriteAllText(source, """<CAEXFile SchemaVersion="2.15" FileName="device.aml"><InstanceHierarchy Name="Project"/></CAEXFile>""");
            var reply = fixture.Engine.ImportHardwareAml(source, Path.Combine(directory.FullName, "import.log"), confirmImport: true, dryRun: false);
            Assert.Equal(1, provider.ImportCalls);
            return reply;
        }
        finally { directory.Delete(true); }
    }

    [Theory, InlineData("queryRefused"), InlineData("success"), InlineData("mappingExists"), InlineData("absent"),
        InlineData("absent-unchanged"), InlineData("findThrows")]
    public void Workspace_export_tracks_native_dispatch_and_fresh_Find_after_a_throw(string result)
    {
        var attempts = new VciExportAttempts();
        var calls = new List<string>();
        var details = new List<string>();
        void Write()
        {
            calls.Add("Export");
            if (result != "success") throw new InvalidOperationException("Native export refused token=secretValue");
        }
        bool Find()
        {
            calls.Add("MappedObjects.Find");
            if (result == "findThrows") throw new IOException("Mapping query failed");
            return result == "mappingExists";
        }
        if (result == "queryRefused") attempts.QueryRefused(new InvalidOperationException("Unsupported format"));
        else if (result == "findThrows") Assert.False(attempts.Export(Write, Find, details.Add));
        else if (result == "absent-unchanged")
        {
            var workspace = Directory.CreateTempSubdirectory();
            try
            {
                var target = VciSyncTarget.Capture("Motor", () => workspace.FullName);
                Assert.False(attempts.Export(Write, Find, details.Add, () => target.Snapshot(workspace.FullName, checkWorkspaceWhenEmpty: true)));
            }
            finally { workspace.Delete(true); }
        }
        else Assert.Equal(result == "success", attempts.Export(Write, Find, details.Add));
        Assert.Equal(result == "findThrows", attempts.MappingProbeFailed);
        Assert.Equal(result == "queryRefused" ? 1 : 0, attempts.QueryRefusals);
        Assert.Equal(result == "queryRefused" ? 0 : 1, attempts.Issued);
        Assert.Equal(result is "absent" or "findThrows" ? 1 : 0, attempts.Unconfirmed);
        Assert.Equal(result == "mappingExists" ? 1 : 0, attempts.MappedAfterFailure);
        if (result != "queryRefused" && result != "success")
        {
            Assert.Equal(new[] { "Export", "MappedObjects.Find" }, calls);
            Assert.DoesNotContain("secretValue", attempts.NativeMessage);
        }
    }
}
