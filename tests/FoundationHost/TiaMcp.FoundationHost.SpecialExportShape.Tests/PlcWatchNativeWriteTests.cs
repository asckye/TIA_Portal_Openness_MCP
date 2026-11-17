using Siemens.Engineering;
using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Engineering;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

public sealed class PlcWatchNativeWriteTests
{
    [Fact]
    public void Create_splits_before_decoding_and_reads_count_and_Find_on_fresh_navigation()
    {
        using var fixture = new Fixture();
        var group = fixture.Group("Group/Sub");
        PlcWatchTableComposition? fresh = null;
        group.StoredWatchTables.AfterCreate = _ => group.ReadWatchTables = () => {
            fresh = new PlcWatchTableComposition(); fresh.AddRange(group.StoredWatchTables); return fresh;
        };
        int navigationBefore = fixture.Session.Software.WatchGroupReads;
        var reply = fixture.Adapter.ManagePlcTableEntries("PLC", "watch", "Group%2FSub/Table%2FOne", "createTable", dryRun: false);
        Assert.Equal("Table/One", Assert.Single(group.StoredWatchTables).Name);
        Assert.Equal(1, group.StoredWatchTables.CreateCalls);
        Assert.NotNull(fresh); Assert.NotSame(group.StoredWatchTables, fresh); Assert.Equal(1, fresh.FindCalls);
        Assert.True(fixture.Session.Software.WatchGroupReads >= navigationBefore + 3);
        Assert.Equal(0, reply.Meta!["tableCountBefore"]); Assert.Equal(1, reply.Meta["tableCountAfter"]);
        Assert.Equal(true, reply.Meta["readbackVerified"]); Assert.Equal("Group%2FSub/Table%2FOne", reply.Meta["tablePath"]);
    }

    [Theory]
    [InlineData("watch")]
    [InlineData("force")]
    public void Create_refuses_a_same_group_watch_or_force_name_clash(string kind)
    {
        using var fixture = new Fixture(); var group = fixture.Group("G/1");
        if (kind == "watch") group.WatchTables.Add(new PlcWatchTable { Name = "T/1" });
        else group.ForceTables.Add(new PlcForceTable { Name = "T/1" });
        Assert.Throws<ArgumentException>(() => fixture.Adapter.ManagePlcTableEntries("PLC", "watch", "G%2F1/T%2F1", "createTable", dryRun: false));
        Assert.Equal(0, group.StoredWatchTables.CreateCalls);
        Assert.Equal(kind == "watch" ? 1 : 0, group.StoredWatchTables.Count);
    }

    [Fact]
    public void Create_preview_writes_nothing_and_only_the_target_group_reserves_the_name()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G");
        fixture.Root.ForceTables.Add(new PlcForceTable { Name = "T" });
        fixture.Adapter.ManagePlcTableEntries("PLC", "watch", "G/T", "createTable");
        Assert.Empty(group.StoredWatchTables); Assert.Equal(0, group.StoredWatchTables.CreateCalls);
        fixture.Adapter.ManagePlcTableEntries("PLC", "watch", "G/T", "createTable", dryRun: false);
        Assert.Equal("T", Assert.Single(group.StoredWatchTables).Name);
    }

    [Fact]
    public void Creating_is_registered_before_Create_can_insert_then_throw()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G"); PlcWatchTable? inserted = null;
        group.StoredWatchTables.AfterCreate = table => { inserted = table; throw new IOException("Create threw after inserting."); };
        Assert.Throws<IOException>(() => fixture.Adapter.ManagePlcTableEntries("PLC", "watch", "G/T", "createTable", dryRun: false));
        Assert.Equal(1, group.StoredWatchTables.CreateCalls); Assert.Empty(group.StoredWatchTables);
        Assert.NotNull(inserted); Assert.Equal(1, inserted.DeleteCalls);
        Assert.Equal(true, fixture.Session.LastMeta["rollbackCompleted"]);
        Assert.Contains("G/T", Assert.IsType<string>(Assert.Single((List<object?>)fixture.Session.LastMeta["restoredChanges"]!)));
    }

    [Fact]
    public void Failed_fresh_count_readback_rolls_back_the_created_table()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G"); PlcWatchTable? inserted = null;
        group.StoredWatchTables.AfterCreate = table => {
            inserted = table;
            group.ReadWatchTables = () => {
                var snapshot = new PlcWatchTableComposition { ReadCount = () => 0 }; snapshot.AddRange(group.StoredWatchTables); return snapshot;
            };
        };
        Assert.Throws<InvalidOperationException>(() => fixture.Adapter.ManagePlcTableEntries("PLC", "watch", "G/T", "createTable", dryRun: false));
        Assert.Empty(group.StoredWatchTables); Assert.NotNull(inserted); Assert.Equal(1, inserted.DeleteCalls);
        Assert.Equal(false, fixture.Session.LastMeta["readbackVerified"]);
        Assert.Equal(true, fixture.Session.LastMeta["rollbackCompleted"]); Assert.Equal(false, fixture.Session.LastMeta["mayHaveChanged"]);
    }

    [Fact]
    public void Force_import_calls_the_force_composition_and_retains_an_Unrestored_record()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G/1");
        string file = fixture.Document("Force", "F"); var outer = new Dictionary<string, object?>();
        Assert.Throws<IOException>(() => EngineeringWriteJournal.Run(outer, () => {
            var reply = fixture.Adapter.ImportPlcWatchTableOffline("PLC", file, "G%2F1", false, "force");
            Assert.Equal(true, reply.Meta!["readbackVerified"]); throw new IOException("Later step failed.");
        }));
        Assert.Equal(1, group.ForceTables.ImportCalls); Assert.Equal(ImportOptions.None, group.ForceTables.LastImportOptions);
        Assert.Equal(0, group.StoredWatchTables.ImportCalls); Assert.Equal("F", Assert.Single(group.ForceTables).Name);
        Assert.Equal(true, outer["unrestoredWrite"]); Assert.Equal(false, outer["rollbackCompleted"]);
        var cleanup = (Dictionary<string, object?>)Assert.Single((List<object?>)outer["cleanupRequired"]!)!;
        Assert.Contains("force table import", (string?)cleanup["target"]);
    }

    [Fact]
    public void Force_import_rejects_a_returned_name_that_does_not_match_fresh_Find()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G");
        group.ForceTables.FindOverride = _ => new PlcForceTable { Name = "Different" };
        Assert.Throws<InvalidOperationException>(() => fixture.Adapter.ImportPlcWatchTableOffline("PLC", fixture.Document("Force", "F"), "G", false, "force"));
        Assert.Equal(1, group.ForceTables.ImportCalls); Assert.Equal(false, fixture.Session.LastMeta["readbackVerified"]);
        Assert.Equal(false, fixture.Session.LastMeta["dataComplete"]); Assert.Equal(true, fixture.Session.LastMeta["unrestoredWrite"]);
    }

    [Fact]
    public void Watch_import_that_inserts_then_throws_is_deleted_through_fresh_group_navigation()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G/1");
        var fresh = new PlcWatchAndForceTableUserGroup { Name = group.Name }; PlcWatchTable? inserted = null;
        var failure = new IOException("Import threw after inserting.");
        group.StoredWatchTables.AfterImport = tables => {
            inserted = Assert.Single(tables);
            group.StoredWatchTables.Clear(); fresh.StoredWatchTables.Add(inserted);
            inserted.Deleting = () => fresh.StoredWatchTables.Remove(inserted);
            fixture.Root.Groups.Clear(); fixture.Root.Groups.Add(fresh);
            throw failure;
        };
        Assert.Same(failure, Assert.Throws<IOException>(() => fixture.Adapter.ImportPlcWatchTableOffline(
            "PLC", fixture.Document("Watch", "W/1"), "G%2F1", false)));
        Assert.NotNull(inserted); Assert.Equal(1, inserted.DeleteCalls); Assert.Empty(fresh.StoredWatchTables);
        Assert.Equal(true, fixture.Session.LastMeta["rollbackCompleted"]);
        Assert.Equal("restored", fixture.Session.LastMeta["rollbackOutcome"]);
        Assert.Equal(false, fixture.Session.LastMeta["mayHaveChanged"]); Assert.Equal(false, fixture.Session.LastMeta["unrestoredWrite"]);
        Assert.Empty((List<object?>)fixture.Session.LastMeta["cleanupRequired"]!);
        Assert.Contains("G%2F1/W%2F1", Assert.IsType<string>(Assert.Single((List<object?>)fixture.Session.LastMeta["restoredChanges"]!)));
    }

    [Fact]
    public void Every_validated_watch_name_is_registered_before_import_can_throw()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G");
        string file = fixture.Document("Watch", "First");
        var xml = System.Xml.Linq.XDocument.Load(file);
        var second = new System.Xml.Linq.XElement(xml.Root!.Elements().Last()); second.SetAttributeValue("ID", "1");
        second.Element("AttributeList")!.Element("Name")!.Value = "Second"; xml.Root.Add(second); xml.Save(file);
        PlcWatchTable[] inserted = Array.Empty<PlcWatchTable>();
        group.StoredWatchTables.AfterImport = tables => { inserted = tables.ToArray(); throw new IOException("Import failed after both inserts."); };
        Assert.Throws<IOException>(() => fixture.Adapter.ImportPlcWatchTableOffline("PLC", file, "G", false));
        Assert.Equal(2, inserted.Length); Assert.All(inserted, table => Assert.Equal(1, table.DeleteCalls));
        Assert.Empty(group.StoredWatchTables); Assert.Equal(true, fixture.Session.LastMeta["rollbackCompleted"]);
        Assert.Equal(false, fixture.Session.LastMeta["mayHaveChanged"]);
        Assert.Equal(2, ((List<object?>)fixture.Session.LastMeta["restoredChanges"]!).Count);
    }

    [Fact]
    public void Existing_watch_table_is_refused_before_registering_a_creation_inverse()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G"); var existing = new PlcWatchTable { Name = "W" };
        group.StoredWatchTables.Add(existing);
        Assert.Throws<AdapterPreconditionException>(() => fixture.Adapter.ImportPlcWatchTableOffline("PLC", fixture.Document("Watch", "W"), "G", false));
        Assert.Equal(0, group.StoredWatchTables.ImportCalls); Assert.Equal(0, existing.DeleteCalls);
        Assert.Same(existing, Assert.Single(group.StoredWatchTables)); Assert.Equal(false, fixture.Session.LastMeta["mayHaveChanged"]);
    }

    [Fact]
    public void Failed_watch_import_rollback_reports_the_table_that_could_not_be_deleted()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G"); PlcWatchTable? inserted = null;
        var failure = new IOException("Import threw after inserting.");
        group.StoredWatchTables.AfterImport = tables => {
            inserted = Assert.Single(tables); inserted.Deleting = () => throw new IOException("Delete failed."); throw failure;
        };
        Assert.Same(failure, Assert.Throws<IOException>(() => fixture.Adapter.ImportPlcWatchTableOffline("PLC", fixture.Document("Watch", "W"), "G", false)));
        Assert.NotNull(inserted); Assert.Equal(1, inserted.DeleteCalls); Assert.Same(inserted, Assert.Single(group.StoredWatchTables));
        Assert.Equal(false, fixture.Session.LastMeta["rollbackCompleted"]); Assert.Equal(true, fixture.Session.LastMeta["mayHaveChanged"]);
        Assert.NotEmpty((List<object?>)fixture.Session.LastMeta["cleanupRequired"]!);
    }

    [Fact]
    public void Imported_watch_tables_have_a_verified_Delete_inverse()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G"); var outer = new Dictionary<string, object?>();
        PlcWatchTable? imported = null;
        Assert.Throws<IOException>(() => EngineeringWriteJournal.Run(outer, () => {
            fixture.Adapter.ImportPlcWatchTableOffline("PLC", fixture.Document("Watch", "W"), "G", false);
            imported = Assert.Single(group.StoredWatchTables); throw new IOException("Later step failed.");
        }));
        Assert.NotNull(imported); Assert.Equal(1, imported.DeleteCalls); Assert.Empty(group.StoredWatchTables);
        Assert.Equal(true, outer["rollbackCompleted"]); Assert.Equal(false, outer["unrestoredWrite"]);
        Assert.Empty((List<object?>)outer["cleanupRequired"]!);
    }

    [Fact]
    public void Failed_watch_import_readback_deletes_the_imported_tables()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G"); PlcWatchTable? imported = null;
        group.StoredWatchTables.AfterImport = tables => { imported = tables.Single(); group.StoredWatchTables.FindOverride = _ => null; };
        Assert.Throws<InvalidOperationException>(() => fixture.Adapter.ImportPlcWatchTableOffline("PLC", fixture.Document("Watch", "W"), "G", false));
        Assert.NotNull(imported); Assert.Equal(1, imported.DeleteCalls); Assert.Empty(group.StoredWatchTables);
        Assert.Equal(true, fixture.Session.LastMeta["rollbackCompleted"]); Assert.Equal(false, fixture.Session.LastMeta["unrestoredWrite"]);
    }

    [Fact]
    public void Force_export_previews_without_writes_then_publishes_a_nonempty_temporary_file()
    {
        using var fixture = new Fixture(); var group = fixture.Group("G"); var table = new PlcForceTable { Name = "F" }; group.ForceTables.Add(table);
        var preview = fixture.Adapter.ExportPlcWatchTablesToDirectory("PLC", fixture.Output, tableKind: "force");
        Assert.False(Directory.Exists(fixture.Output)); Assert.Equal(0, table.ExportCalls); Assert.Equal(0, fixture.Session.ExactCalls);
        var result = fixture.Adapter.ExportPlcWatchTablesToDirectory("PLC", fixture.Output, tableKind: "force", dryRun: false,
            expectedPlanHash: (string)preview.Meta!["planHash"]!);
        string path = Assert.Single(result.Imported!);
        Assert.Equal(Path.Combine(fixture.Output, "G", "F.xml"), path); Assert.True(File.Exists(path));
        Assert.NotNull(table.LastExportFile); Assert.NotEqual(path, table.LastExportFile.FullName);
        Assert.Equal(Path.GetDirectoryName(path), table.LastExportFile.DirectoryName);
        Assert.StartsWith(".tia-export-", table.LastExportFile.Name); Assert.False(File.Exists(table.LastExportFile.FullName));
        Assert.Equal(ExportOptions.WithDefaults, table.LastExportOptions); Assert.Empty(result.Failed!);
        Assert.Equal(1, WatchTableImportValidation.Validate(path, "force"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Force_export_refuses_empty_files_and_a_destination_created_during_native_export(bool empty)
    {
        using var fixture = new Fixture(); var table = new PlcForceTable { Name = "F" }; fixture.Root.ForceTables.Add(table);
        string output = Path.Combine(fixture.Output, "F.xml");
        table.Exporting = file => {
            File.WriteAllText(file.FullName, empty ? "" : "native XML");
            if (!empty) File.WriteAllText(output, "existing destination");
        };
        var preview = fixture.Adapter.ExportPlcWatchTablesToDirectory("PLC", fixture.Output, tableKind: "force");
        var result = fixture.Adapter.ExportPlcWatchTablesToDirectory("PLC", fixture.Output, tableKind: "force", dryRun: false,
            expectedPlanHash: (string)preview.Meta!["planHash"]!);
        Assert.Empty(result.Imported!); Assert.Single(result.Failed!); Assert.Equal(false, result.Meta!["operationSuccess"]);
        Assert.NotNull(table.LastExportFile); Assert.True(File.Exists(table.LastExportFile.FullName));
        if (empty) { Assert.Equal(0, new FileInfo(table.LastExportFile.FullName).Length); Assert.False(File.Exists(output)); }
        else Assert.Equal("existing destination", File.ReadAllText(output));
    }

    [Fact]
    public void Force_export_checks_the_plan_hash_before_the_exact_offline_PLC_lookup()
    {
        using var fixture = new Fixture(); var table = new PlcForceTable { Name = "F" }; fixture.Root.ForceTables.Add(table);
        Assert.Throws<AdapterPreconditionException>(() => fixture.Adapter.ExportPlcWatchTablesToDirectory("PLC", fixture.Output,
            tableKind: "force", dryRun: false, expectedPlanHash: "stale"));
        Assert.Equal(0, fixture.Session.ExactCalls); Assert.Equal(0, table.ExportCalls); Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public void Raw_watch_directory_dry_run_creates_no_directory_or_export()
    {
        using var fixture = new Fixture(); var table = new PlcWatchTable { Name = "W" }; fixture.Root.WatchTables.Add(table);
        var result = fixture.Adapter.ExportPlcWatchTablesToDirectory("PLC", fixture.Output);
        Assert.Empty(result.Failed!); Assert.Empty(result.Imported!); Assert.Equal(0, table.ExportCalls);
        Assert.False(Directory.Exists(fixture.Output));
        var applied = fixture.Adapter.ExportPlcWatchTablesToDirectory("PLC", fixture.Output, dryRun: false);
        Assert.Single(applied.Imported!); Assert.Empty(applied.Failed!);
        Assert.Equal(1, table.ExportCalls); Assert.True(Directory.Exists(fixture.Output));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build",
            "watch-native-" + Guid.NewGuid().ToString("N"));
        internal WatchShapeSession Session { get; } = new();
        internal PlcOrganisationAdapter Adapter { get; }
        internal PlcWatchAndForceTableSystemGroup Root => Session.Software.WatchAndForceTableGroup;
        internal string Output => Path.Combine(root, "output");
        internal Fixture() { Directory.CreateDirectory(root); Adapter = new(Session); }
        internal PlcWatchAndForceTableUserGroup Group(string name)
        { var group = new PlcWatchAndForceTableUserGroup { Name = name }; Root.Groups.Add(group); return group; }
        internal string Document(string kind, string name)
        {
            string path = Path.Combine(root, kind + ".xml");
            File.WriteAllText(path, "<Document><Engineering version=\"V21\"/><SW.WatchAndForceTables.Plc" + kind
                + "Table ID=\"0\"><AttributeList><Name>" + name + "</Name></AttributeList></SW.WatchAndForceTables.Plc" + kind + "Table></Document>");
            return path;
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
