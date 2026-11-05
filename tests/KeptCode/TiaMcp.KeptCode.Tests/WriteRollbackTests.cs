using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using Xunit;
using Xunit.Abstractions;

namespace TiaMcp.Engine.Tests;

public sealed class WriteRollbackTests(ITestOutputHelper output)
{
    [Fact]
    public void Informational_skeleton_step_keeps_created_objects_and_reports_success()
    {
        var software = new Software(); var meta = new HmiObject();
        EngineeringWriteJournal.Run(meta, () => {
            HmiExchangePolicy.EnsureTagTable(software, "Signals", () => { });
            var steps = new HmiArray(new HmiObject { ["step"] = "create", ["ok"] = true },
                new HmiObject { ["step"] = "dyn:LAMP_Run", ["ok"] = false, ["informational"] = true });
            HmiExchangePolicy.CompleteSteps(meta, steps);
            return "skeleton";
        });
        Assert.Single(software.TagTables);
        Assert.Equal(true, meta["success"]);
        Assert.Equal(true, meta["operationSuccess"]);
        Assert.False(meta.ContainsKey("rollbackCompleted"));
    }

    [Fact]
    public void Refused_property_and_attribute_sets_do_not_repeat_the_refused_write_during_undo()
    {
        var target = new Refused(); var meta = new HmiObject { ["mayHaveChanged"] = true };
        Assert.ThrowsAny<Exception>(() => EngineeringWriteJournal.Run(meta, () => {
            EngineeringWriteJournal.Property(target, typeof(Refused).GetProperty(nameof(Refused.Value))!);
            target.Value = "after"; return "unreachable";
        }));
        Assert.Equal(1, target.Attempts);
        Assert.Contains("Hooked writes only", (string)meta["rollbackScope"]!);
        Assert.Equal(true, meta["rollbackCompleted"]);
        Assert.Equal(false, meta["mayHaveChanged"]);
        Assert.Empty((List<object?>)meta["cleanupRequired"]!);
        meta = new HmiObject { ["mayHaveChanged"] = true };
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            EngineeringWriteJournal.Attribute(target, "Value", () => target.Value, value => target.Value = (string)value!);
            target.Value = "after"; return "unreachable";
        }));
        Assert.Equal(2, target.Attempts);
        Assert.Equal(true, meta["rollbackCompleted"]);
        Assert.Equal(false, meta["mayHaveChanged"]);
        Assert.Empty((List<object?>)meta["cleanupRequired"]!);
    }

    [Fact]
    public void Create_results_are_journaled_without_enumerating_the_collection()
    {
        var collection = new NoEnumerationCollection(); var meta = new HmiObject();
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            PlcGroupOperations.Call(collection, "Create", new[] { typeof(string) }, "Created");
            throw new InvalidOperationException("later step failed");
        }));
        Assert.True(collection.Result.Deleted);
        Assert.Equal(true, meta["rollbackCompleted"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deleted_created_objects_exclude_fresh_proxy_and_child_property_readbacks(bool child)
    {
        // Create and Find can return different native proxies; nested properties have their own proxies.
        var state = new ProxyState(); var parent = new ProxyRoot();
        var created = new ProxyValue(state, parent, "Start");
        var readback = new ProxyValue(state, parent, "Start");
        var target = child ? new ProxyValue(state, readback, "Style") : readback;
        var meta = new HmiObject();
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            EngineeringWriteJournal.Created(created, "/HMI/Start", () => state.Deleted);
            EngineeringWriteJournal.Property(target, typeof(ProxyValue).GetProperty(nameof(ProxyValue.Value))!);
            target.Value = 2;
            throw new InvalidOperationException("later step failed");
        }));
        Assert.True(state.Deleted);
        Assert.Equal(0, state.ReadsAfterDelete);
        Assert.Equal(true, meta["rollbackCompleted"]);
        Assert.Empty((List<object?>)meta["cleanupRequired"]!);
    }

    [Fact]
    public void Read_failures_have_no_rollback_fields()
    {
        var meta = new HmiObject();
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => throw new InvalidOperationException("read refused")));
        Assert.Empty(meta);
    }

    [Fact]
    public void Undo_reacquires_access_after_the_operation_access_unwinds()
    {
        var target = new AccessProtected(); var meta = new HmiObject(); int acquired = 0;
        IDisposable Access() { acquired++; target.Access = true; return new AccessLease(() => target.Access = false); }
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            using var ownAccess = Access();
            EngineeringWriteJournal.Property(target, typeof(AccessProtected).GetProperty(nameof(AccessProtected.Value))!);
            target.Value = "after";
            throw new InvalidOperationException("later write failed");
        }, Access));
        Assert.Equal(2, acquired);
        Assert.Equal("before", target.Value);
        Assert.False(target.Access);
        Assert.Equal(true, meta["rollbackCompleted"]);
    }

    [Fact]
    public void An_anonymous_creation_that_throws_before_returning_cannot_be_reported_as_restored()
    {
        var parts = new List<object>(); var meta = new HmiObject();
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            bool tracked = false;
            EngineeringWriteJournal.Record("/HMI/Screens/Main/Start/PressedStateTags/StartPB: reconcile unconfirmed anonymous part creation",
                () => { if (!tracked) throw new NotSupportedException("Exact new object unavailable."); });
            parts.Add(new object());
            throw new InvalidOperationException("Create inserted an anonymous part before throwing");
        }));
        Assert.Single(parts);
        Assert.Equal(false, meta["rollbackCompleted"]);
        Assert.Equal("unknown", meta["outcome"]);
        Assert.Equal(true, meta["writeOutcomeUnknown"]);
        var cleanup = (Dictionary<string, object?>)Assert.Single((List<object?>)meta["cleanupRequired"]!);
        Assert.Contains("/PressedStateTags/StartPB", (string)cleanup["target"]!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_opaque_import_cleanup_task_survives_the_outer_journal(bool hasResult)
    {
        var task = new Dictionary<string, object?> { ["target"] = "/HMI/Scripts/Start", ["restoreFrom"] = "evidence/original.xml" };
        var meta = new HmiObject { ["cleanupRequired"] = new List<object?> { task } };
        if (hasResult) meta["rollbackCompleted"] = false;
        EngineeringWriteJournal.Run(meta, () => { meta["operationSuccess"] = false; return "import not confirmed"; });
        Assert.Same(task, Assert.Single((List<object?>)meta["cleanupRequired"]!));
        Assert.Equal("unknown", meta["outcome"]);
        Assert.Equal(true, meta["writeOutcomeUnknown"]);
        Assert.Equal(false, meta["rollbackCompleted"]);
        Assert.Equal(true, meta["mayHaveChanged"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unresolved_child_cleanup_cannot_be_hidden_by_a_successful_parent(bool outerChanges)
    {
        var target = new Script { ScriptCode = "before" };
        var parent = new HmiObject();
        EngineeringWriteJournal.Run(parent, () => {
            if (outerChanges) {
                EngineeringWriteJournal.Property(target, typeof(Script).GetProperty(nameof(Script.ScriptCode))!);
                target.ScriptCode = "after";
            }
            var child = new HmiObject();
            try { EngineeringWriteJournal.Run(child, () => {
                EngineeringWriteJournal.Created(new NoDelete(), "/HMI/Screens/Overview/Start");
                throw new InvalidOperationException("child step failed");
            }); }
            catch (InvalidOperationException) { /* The parent must retain the child's pending cleanup even if its caller handles the exception. */ }
            return "parent action returned";
        });
        Assert.Equal("before", target.ScriptCode);
        Assert.Equal(false, parent["operationSuccess"]);
        Assert.Equal(false, parent["rollbackCompleted"]);
        Assert.Equal(true, parent["mayHaveChanged"]);
        Assert.Equal("unknown", parent["outcome"]);
        Assert.Equal(true, parent["writeOutcomeUnknown"]);
        var cleanup = (Dictionary<string, object?>)Assert.Single((List<object?>)parent["cleanupRequired"]!);
        Assert.Contains("/HMI/Screens/Overview/Start", (string)cleanup["target"]!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_network_readback_exception_keeps_exact_compensation_evidence_in_the_report(bool undoFails)
    {
        bool connected = false;
        var meta = new HmiObject();
        string report = EngineeringWriteJournal.RunReported(meta, () => {
            EngineeringWriteJournal.ConnectedNode("HMI/Nodes/PN", () => connected, () => {
                if (undoFails) throw new InvalidOperationException("disconnect unavailable");
                connected = false;
            });
            connected = true;
            throw new InvalidOperationException("readback failed after connect");
        }, error => error.GetBaseException().Message);
        var evidence = System.Text.Json.JsonSerializer.SerializeToNode(meta)!;
        Assert.Equal("readback failed after connect", report);
        Assert.False((bool?)evidence["operationSuccess"]);
        Assert.Equal(!undoFails, (bool?)evidence["rollbackCompleted"]);
        Assert.Equal(undoFails, connected);
        if (undoFails) Assert.Contains("HMI/Nodes/PN", (string)evidence["cleanupRequired"]![0]!["target"]!);
        else Assert.Empty(evidence["cleanupRequired"]!.AsArray());
    }

    [Fact]
    public void Existing_verified_import_checks_and_the_no_replay_assertion_still_run()
        => PlcEditingTests.Run((condition, message) => Assert.True(condition, message));

    [Fact]
    public void An_unconfirmed_native_block_import_retains_an_exact_original_backup_cleanup_target()
    {
        string xml = (string)typeof(PlcEditingTests).GetField("Xml", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        string directory = Path
            .Combine(Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "refactor", "verified-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string candidate = xml.Replace("Original", "Changed");
            void Export(string path) => File.WriteAllText(path, xml);
            var preview = new JsonObject();
            TiaMcpServer.ModelContextProtocol.PlcVerifiedImport
                .Execute(candidate, "PLC/Main", directory, true, "", Export, _ => throw new InvalidOperationException(), () => { }, preview);
            var failed = new JsonObject(); int imports = 0;
            Assert
                .Throws<InvalidOperationException>(() => TiaMcpServer.ModelContextProtocol.PlcVerifiedImport
                    .Execute(candidate, "PLC/Main", directory, false, (string)preview["token"]!, Export,
                _ => { imports++; throw new InvalidOperationException("native outcome unconfirmed"); }, () => { }, failed));
            Assert.Equal(1, imports);
            var cleanup = Assert.Single(failed["cleanupRequired"]!.AsArray());
            Assert.Equal("PLC/Main", (string?)cleanup!["target"]);
            Assert.Equal(xml, File.ReadAllText((string)cleanup["restoreFrom"]!));
        }
        finally
        {
            string expected = Path.GetFullPath(Path.Combine(Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build")) + Path
                .DirectorySeparatorChar;
            Assert.StartsWith(expected, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Network_failure_disconnects_only_new_attachments_and_deletes_only_a_new_subnet(bool createSubnet)
    {
        var log = new List<string>(); var nodes = new[] { new Node("PLC", log), new Node("HMI", log) };
        var subnets = new List<Subnet>(); var target = new Subnet("PN", subnets, nodes, log);
        if (!createSubnet) { subnets.Add(target); nodes[0].ConnectToSubnet(target); log.Clear(); }
        var meta = new HmiObject();
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            if (createSubnet) {
                EngineeringWriteJournal.Creating(() => subnets.SingleOrDefault(s => s.Name == "PN"), "Project.Subnets/PN"); subnets.Add(target); }
            if (nodes[0].ConnectedSubnet == null) {
                EngineeringWriteJournal.ConnectedNode("PLC/Nodes/PN", () => nodes[0].ConnectedSubnet != null, nodes[0].DisconnectFromSubnet); nodes[0]
                    .ConnectToSubnet(target);
            }
            EngineeringWriteJournal.ConnectedNode("HMI/Nodes/PN", () => nodes[1].ConnectedSubnet != null, nodes[1].DisconnectFromSubnet);
            nodes[1].ConnectToSubnet(target);
            throw new InvalidOperationException("second node readback failed");
        }));
        Assert.Null(nodes[1].ConnectedSubnet);
        if (createSubnet) {
            Assert.Null(nodes[0].ConnectedSubnet); Assert.Empty(subnets); Assert.Equal(new[] { "disconnect:HMI", "disconnect:PLC", "delete:PN" }, log); }
        else { Assert.Same(target, nodes[0].ConnectedSubnet); Assert.Same(target, Assert.Single(subnets)); Assert.Equal(new[] { "disconnect:HMI" }, log); }
        Assert.Equal(true, meta["rollbackCompleted"]);
    }

    [Fact]
    public void Nested_screen_properties_are_restored_after_a_later_design_step_fails()
    {
        var target = new Nested(); var meta = new HmiObject();
        var edits = UnifiedUiModelLogic
            .PrepareNested(target.GetType(), new HmiObject { ["Script"] = new HmiObject { ["ScriptCode"] = "after", ["Async"] = true } });
        Assert
            .Throws<InvalidOperationException>(() => EngineeringWriteJournal
                .Run(meta, () => { UnifiedUiModelLogic.ApplyNested(target, edits, meta); throw new InvalidOperationException("later design item failed"); }));
        Assert.Equal("before", target.Script.ScriptCode);
        Assert.False(target.Script.Async);
        Assert.Equal(true, meta["rollbackCompleted"]);
    }

    [Fact]
    public void Multilingual_design_edits_restore_the_exact_original_HTML_in_each_culture()
    {
        var target = new TextOwner(); var meta = new HmiObject();
        string[] before = target.Text.Items.Select(item => item.Text).ToArray();
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            Assert.Equal(true, UnifiedMultilingualText.WriteDetailed(target, "Text", "English", "en-US")["verified"]);
            Assert.Equal(true, UnifiedMultilingualText.WriteDetailed(target, "Text", "中文", "zh-CN")["verified"]);
            throw new InvalidOperationException("later design item failed");
        }));
        Assert.Equal(before, target.Text.Items.Select(item => item.Text));
        Assert.Equal(true, meta["rollbackCompleted"]);
    }

    [Fact]
    public void Hardware_optional_and_safety_scalar_path_restores_the_whole_batch()
    {
        var tag = new Tag(new Tags()) { DataType = "Int", Connection = "Original" };
        var meta = new Dictionary<string, object?>();
        Assert.ThrowsAny<Exception>(() => HardwareScalarEvidence.Apply(tag, new[] {
            (typeof(Tag).GetProperty(nameof(Tag.DataType))!, (object?)"Bool"),
            (typeof(Tag).GetProperty(nameof(Tag.Connection))!, (object?)"Fail") }.ToList(), meta));
        Assert.Equal("Int", tag.DataType);
        Assert.Equal("Original", tag.Connection);
        Assert.Equal(true, meta["rollbackCompleted"]);
    }

    [Fact]
    public void Nested_group_creation_removes_new_children_in_reverse_order_and_keeps_existing_groups()
    {
        var root = new Group("root"); var existing = root.Create("Existing");
        var error = Assert.Throws<TiaMcp.Adapters.Contracts.PlcSoftwareException>(() => PlcGroupCreation.Execute(root, "Existing/New/Fail", false,
            group => group.Children, group => group.Name, (group, name) => group.Create(name)));
        Assert.Same(existing, Assert.Single(root.Children));
        Assert.Empty(existing.Children);
        var evidence = Assert.IsType<Dictionary<string, object?>>(error.Data["nativeResultEvidence"]);
        Assert.Equal(true, evidence["rollbackCompleted"]);
    }

    [Fact]
    public void Coupled_setters_cannot_make_rollback_report_a_false_success()
    {
        var target = new Coupled(); var meta = new HmiObject();
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            HmiExchangePolicy.WriteScalars(target, new Dictionary<string, object?> { ["A"] = 2, ["B"] = 8 }, () => { });
            throw new InvalidOperationException("later step failed");
        }));
        Assert.Equal(1, target.A);
        Assert.Equal(false, meta["rollbackCompleted"]);
        Assert.Equal("unknown", meta["outcome"]);
        Assert.Equal(true, meta["writeOutcomeUnknown"]);
        var cleanup = ((List<object?>)meta["cleanupRequired"]!).Cast<Dictionary<string, object?>>().Single(row => ((string)row["target"]!).Contains(".B:"));
        Assert.Equal("5", cleanup["previousValue"]);
    }

    [Fact]
    public void Managed_journal_overhead_is_reported_without_a_timing_threshold()
    {
        const int iterations = 10000;
        var meta = new Dictionary<string, object?>();
        string Action() { meta["probe"] = "readback"; return (string)meta["probe"]!; }
        for (int i = 0; i < iterations; i++) EngineeringWriteJournal.Run(meta, Action);
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) Action();
        double before = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        for (int i = 0; i < iterations; i++) EngineeringWriteJournal.Run(meta, Action);
        double after = timer.Elapsed.TotalMilliseconds;
        output.WriteLine("Managed owner-thread action, {0} calls: before={1:F3} ms; journal={2:F3} ms. No native TIA calls.", iterations, before, after);
    }

    [Fact]
    public void Tag_binding_failure_deletes_only_the_table_and_tag_created_by_this_call()
    {
        var software = new Software();
        var keep = software.TagTables.Create("Keep");
        var meta = new HmiObject();
        Assert.ThrowsAny<Exception>(() => EngineeringWriteJournal.Run(meta, () => {
            var table = HmiExchangePolicy.EnsureTagTable(software, "Signals", () => { });
            var tag = HmiExchangePolicy.EnsureTag(software, table, "Start", () => { });
            HmiExchangePolicy.WriteScalars(tag, new Dictionary<string, object?> { ["DataType"] = "Bool", ["Connection"] = "Fail" }, () => { });
            return "unreachable";
        }));
        Assert.Same(keep, Assert.Single(software.TagTables));
        Assert.Empty(software.Tags);
        Assert.Equal(true, meta["rollbackCompleted"]);
        Assert.Empty((List<object?>)meta["cleanupRequired"]!);
    }

    [Fact]
    public void Existing_tag_values_are_restored_even_when_the_throwing_setter_already_changed_them()
    {
        var software = new Software();
        var table = software.TagTables.Create("Signals");
        var tag = software.Tags.Create("Start", table.Name);
        tag.DataType = "Int"; tag.Connection = "Original";
        var meta = new HmiObject();
        Assert.ThrowsAny<Exception>(() => EngineeringWriteJournal.Run(meta, () => {
            HmiExchangePolicy.EnsureTagTable(software, "Signals", () => { });
            HmiExchangePolicy.EnsureTag(software, table, "Start", () => { });
            var changes = UnifiedRequestScalar.Prepare(tag.GetType(), new HmiObject { ["DataType"] = "Bool", ["Connection"] = "Fail" });
            UnifiedRequestScalar.Apply(tag, changes, meta);
            return "unreachable";
        }));
        Assert.Same(tag, Assert.Single(software.Tags));
        Assert.Equal("Int", tag.DataType);
        Assert.Equal("Original", tag.Connection);
        Assert.Equal(true, meta["rollbackCompleted"]);
    }

    [Fact]
    public void A_create_that_throws_after_insertion_is_also_undone()
    {
        var software = new Software(); software.Tags.ThrowAfterCreate = true;
        var table = software.TagTables.Create("Signals");
        var meta = new HmiObject();
        Assert.ThrowsAny<Exception>(() => EngineeringWriteJournal.Run(meta, () => {
            HmiExchangePolicy.EnsureTag(software, table, "Start", () => { });
            return "unreachable";
        }));
        Assert.Empty(software.Tags);
        Assert.Same(table, Assert.Single(software.TagTables));
        Assert.Equal(true, meta["rollbackCompleted"]);
    }

    [Fact]
    public void Failed_delete_or_restore_lists_the_exact_remaining_cleanup_and_continues_undo()
    {
        var meta = new HmiObject();
        var scalar = new Script { ScriptCode = "before" };
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            EngineeringWriteJournal.Property(scalar, typeof(Script).GetProperty(nameof(Script.ScriptCode))!);
            scalar.ScriptCode = "after";
            EngineeringWriteJournal.Created(new NoDelete(), "/HMI/Screens/Overview/Start");
            throw new InvalidOperationException("second step failed");
        }));
        Assert.Equal("before", scalar.ScriptCode);
        Assert.Equal(false, meta["rollbackCompleted"]);
        Assert.Equal("unknown", meta["outcome"]);
        Assert.Equal(true, meta["writeOutcomeUnknown"]);
        var cleanup = Assert.IsType<Dictionary<string, object?>>(Assert.Single((List<object?>)meta["cleanupRequired"]!));
        Assert.Contains("/HMI/Screens/Overview/Start", (string)cleanup["target"]!);
        Assert.Equal("NotSupportedException", cleanup["reason"]);
    }

    [Theory]
    [InlineData("ScriptCode")]
    [InlineData("GlobalDefinitionAreaScriptCode")]
    [InlineData("Async")]
    public void Script_and_nested_property_batches_restore_prior_values(string failure)
    {
        var script = new Script { ScriptCode = "before", GlobalDefinitionAreaScriptCode = "globals" };
        var meta = new HmiObject();
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            HmiExchangePolicy.UpdateScript(script, "after", "new globals", true, () => { });
            throw new InvalidOperationException(failure);
        }));
        Assert.Equal("before", script.ScriptCode);
        Assert.Equal("globals", script.GlobalDefinitionAreaScriptCode);
        Assert.False(script.Async);
        Assert.Equal(true, meta["rollbackCompleted"]);
    }

    [Fact]
    public void Nested_success_is_undone_when_the_outer_multi_step_write_fails()
    {
        var script = new Script { ScriptCode = "before" };
        var meta = new HmiObject();
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            EngineeringWriteJournal.Run(new HmiObject(), () => {
                HmiExchangePolicy.UpdateScript(script, "after", null, null, () => { });
                return "inner completed";
            });
            throw new InvalidOperationException("outer failed");
        }));
        Assert.Equal("before", script.ScriptCode);
        Assert.Equal(true, meta["rollbackCompleted"]);
    }

    [Fact]
    public void Success_commits_without_undo_and_an_unavailable_undo_lock_reports_cleanup()
    {
        var script = new Script();
        EngineeringWriteJournal.Run(new HmiObject(), () => { HmiExchangePolicy.UpdateScript(script, "committed", null, null, () => { }); return "ok"; });
        Assert.Equal("committed", script.ScriptCode);
        var meta = new HmiObject();
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            HmiExchangePolicy.UpdateScript(script, "pending", null, null, () => { });
            throw new InvalidOperationException("failed");
        }, () => throw new InvalidOperationException("exclusive access unavailable")));
        Assert.Equal("pending", script.ScriptCode);
        Assert.Equal("unknown", meta["outcome"]);
        Assert.Equal(true, meta["writeOutcomeUnknown"]);
        Assert.NotEmpty((List<object?>)meta["cleanupRequired"]!);
    }

    public sealed class ProxyState { public int Value = 1; public bool Deleted; public int ReadsAfterDelete; }
    public sealed class ProxyRoot { public string Name => "HMI"; }
    public sealed class ProxyValue(ProxyState state, object parent, string name)
    {
        public string Name => name;
        public object Parent => parent;
        public int Value
        {
            get { if (state.Deleted) { state.ReadsAfterDelete++; throw new InvalidOperationException("Deleted native proxy."); } return state.Value; }
            set => state.Value = value;
        }
        public void Delete() => state.Deleted = true;
    }
    public sealed class NoEnumerationCollection : System.Collections.IEnumerable
    {
        public ReturnedObject Result { get; } = new();
        public ReturnedObject Create(string name) => Result;
        public ReturnedObject? Find(string name) => Result.Deleted ? null : Result;
        public System.Collections.IEnumerator GetEnumerator() => throw new InvalidOperationException("Create journaling must not enumerate.");
    }
    public sealed class ReturnedObject
    {
        public string Name => "Created";
        public bool Deleted { get; private set; }
        public void Delete() => Deleted = true;
    }
    public sealed class Refused
    {
        public int Attempts { get; private set; }
        public string Value { get => "before"; set { Attempts++; throw new InvalidOperationException("read only"); } }
    }
    public sealed class AccessProtected
    {
        public bool Access;
        private string value = "before";
        public string Value { get => value; set { if (!Access) throw new InvalidOperationException("edit access required"); this.value = value; } }
    }
    private sealed class AccessLease(Action release) : IDisposable { public void Dispose() => release(); }

    public sealed class Software
    {
        public Software() { TagTables = new Tables(this); Tags = new Tags(); }
        public Tables TagTables { get; }
        public Tags Tags { get; }
    }
    public sealed class Tables : List<Table>
    {
        private readonly Software owner;
        public Tables(Software owner) => this.owner = owner;
        public Table Create(string name) { var table = new Table(this) { Name = name, Parent = owner }; Add(table); return table; }
    }
    public sealed class Table
    {
        private readonly Tables owner;
        public Table(Tables owner) => this.owner = owner;
        public string Name { get; set; } = "";
        public Software Parent { get; set; } = null!;
        public void Delete() { Assert.DoesNotContain(Parent.Tags, t => t.TagTableName == Name); owner.Remove(this); }
    }
    public sealed class Tags : List<Tag>
    {
        public bool ThrowAfterCreate;
        public Tag Create(string name, string table) {
            var tag = new Tag(this) { Name = name, TagTableName = table }; Add(tag); if (ThrowAfterCreate)
                throw new InvalidOperationException("native insertion failed late"); return tag;
        }
    }
    public sealed class Tag
    {
        private readonly Tags owner;
        private string connection = "";
        public Tag(Tags owner) => this.owner = owner;
        public string Name { get; set; } = "";
        public string TagTableName { get; set; } = "";
        public string DataType { get; set; } = "";
        public string Connection {
            get => connection; set { connection = value; if (value == "Fail") throw new InvalidOperationException("binding failed after mutation"); } }
        public void Delete() => owner.Remove(this);
    }
    public sealed class Script
    {
        public string ScriptCode { get; set; } = "";
        public string GlobalDefinitionAreaScriptCode { get; set; } = "";
        public bool Async { get; set; }
    }
    private sealed class NoDelete { }
    public sealed class Coupled
    {
        private int a = 1;
        public int A { get => a; set { a = value; B = value * 10; } }
        public int B { get; set; } = 5;
    }
    public sealed class Group
    {
        private readonly Group? parent;
        public Group(string name, Group? parent = null) { Name = name; this.parent = parent; }
        public string Name { get; }
        public List<Group> Children { get; } = new();
        public Group Create(string name) {
            var group = new Group(name, this); Children.Add(group); if (name == "Fail") throw new InvalidOperationException("late create failure");
            return group;
        }
        public void Delete() { Assert.Empty(Children); parent!.Children.Remove(this); }
    }
    public sealed class Node(string name, List<string> log)
    {
        public Subnet? ConnectedSubnet { get; private set; }
        public void ConnectToSubnet(Subnet subnet) => ConnectedSubnet = subnet;
        public void DisconnectFromSubnet() { ConnectedSubnet = null; log.Add("disconnect:" + name); }
    }
    public sealed class Subnet(string name, List<Subnet> siblings, Node[] nodes, List<string> log)
    {
        public string Name { get; } = name;
        public void Delete() {
            Assert.DoesNotContain(nodes, node => ReferenceEquals(node.ConnectedSubnet, this)); siblings.Remove(this); log.Add("delete:" + Name); }
    }
    public sealed class Nested { public Script Script { get; } = new() { ScriptCode = "before" }; }
    public sealed class TextOwner { public MultilingualText Text { get; } = new(); }
    public sealed class MultilingualText { public TextItem[] Items { get; } = new[] { new TextItem("en-US"), new TextItem("zh-CN") }; }
    public sealed class TextItem(string culture)
    {
        public Language Language { get; } = new(culture);
        public string Text { get; set; } = "<body><p>Original " + culture + "</p></body>";
    }
    public sealed class Language(string culture) { public CultureInfo Culture { get; } = CultureInfo.GetCultureInfo(culture); }
}
