using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Native.Engineering;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class ProvenWriteJournalTests
{
    [Fact]
    public void Creating_with_nothing_created_restores_and_clears_the_change_mark()
    {
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal) { ["mayHaveChanged"] = false };
        int finds = 0;
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            EngineeringWriteJournal.Creating(() => { finds++; return null; }, "PLC/TechnologicalObjects/Axis_Fixture");
            meta["mayHaveChanged"] = true;
            throw new InvalidOperationException("The technology object type/version was refused.");
        }));
        Assert.Equal(1, finds);
        Assert.Equal("restored", meta["rollbackOutcome"]);
        Assert.Equal(true, meta["rollbackCompleted"]);
        Assert.Equal(false, meta["mayHaveChanged"]);
        Assert.Empty(Assert.IsType<List<object?>>(meta["cleanupRequired"]));
    }

    [Fact]
    public void Failed_undo_on_an_object_the_rollback_deletes_is_restored()
    {
        var target = new RefusingUndo();
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal);
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            EngineeringWriteJournal.Created(target, "/Tag_Fixture", () => target.Deleted);
            Change(target);
            throw new InvalidOperationException("Later binding failed.");
        }));
        Assert.True(target.Deleted);
        Assert.Equal(1, target.UndoAttempts);
        Assert.Equal("restored", meta["rollbackOutcome"]);
        Assert.Equal(true, meta["rollbackCompleted"]);
        Assert.Equal(false, meta["mayHaveChanged"]);
        Assert.Empty(Assert.IsType<List<object?>>(meta["cleanupRequired"]));
    }

    [Fact]
    public void Failed_undo_on_a_surviving_object_stays_unknown()
    {
        var target = new RefusingUndo();
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal);
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            Change(target);
            throw new InvalidOperationException("Later binding failed.");
        }));
        Assert.False(target.Deleted);
        Assert.Equal("unknown", meta["rollbackOutcome"]);
        Assert.Equal(false, meta["rollbackCompleted"]);
        Assert.NotEmpty(Assert.IsType<List<object?>>(meta["cleanupRequired"]));
    }

    [Fact]
    public void Failed_property_undo_on_a_surviving_dotted_sibling_keeps_its_cleanup()
    {
        var created = new RefusingUndo();
        var survivor = new RefusingUndo("Tag_Fixture.1");
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal);
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            EngineeringWriteJournal.Created(created, "/Tag_Fixture", () => created.Deleted);
            Change(survivor);
            throw new InvalidOperationException("Later binding failed.");
        }));
        Assert.True(created.Deleted);
        Assert.False(survivor.Deleted);
        Assert.Equal(1, survivor.UndoAttempts);
        Assert.Equal("unknown", meta["rollbackOutcome"]);
        var cleanup = Assert.IsType<List<object?>>(meta["cleanupRequired"]);
        Assert.Contains(cleanup, item => item is Dictionary<string, object?> row
            && Equals(row["target"], "/Tag_Fixture.1.Value: restore previous value")
            && Equals(row["reason"], nameof(InvalidOperationException)));
    }

    [Fact]
    public void Failed_import_undo_on_a_surviving_dotted_sibling_stays_unknown()
    {
        var created = new RefusingUndo();
        var survivor = new RefusingUndo("Tag_Fixture.1");
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal);
        const string inputPath = "C:/fixture/import.xml";
        Assert.Throws<InvalidOperationException>(() => EngineeringWriteJournal.Run(meta, () => {
            EngineeringWriteJournal.Created(created, "/Tag_Fixture", () => created.Deleted);
            EngineeringWriteJournal.BeforeCall(survivor, typeof(RefusingUndo).GetMethod(nameof(RefusingUndo.Import))!, new object?[] { inputPath });
            survivor.Import(inputPath);
            throw new InvalidOperationException("Later import step failed.");
        }));
        Assert.True(created.Deleted);
        Assert.False(survivor.Deleted);
        Assert.Equal(inputPath, survivor.ImportedPath);
        Assert.Equal("unknown", meta["rollbackOutcome"]);
        Assert.Equal(false, meta["rollbackCompleted"]);
        var cleanup = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(meta["cleanupRequired"])));
        Assert.StartsWith("/Tag_Fixture.1/Import(", Assert.IsType<string>(cleanup["target"]), StringComparison.Ordinal);
        Assert.Equal(nameof(NotSupportedException), cleanup["reason"]);
    }

    private static void Change(RefusingUndo target)
    {
        EngineeringWriteJournal.Property(target, typeof(RefusingUndo).GetProperty(nameof(RefusingUndo.Value))!);
        target.Value = "after";
        target.Refuse = true;
    }

    public sealed class RefusingUndo(string name = "Tag_Fixture")
    {
        private string _value = "before";
        public string Name => name;
        public bool Refuse { get; set; }
        public bool Deleted { get; private set; }
        public int UndoAttempts { get; private set; }
        public string? ImportedPath { get; private set; }
        public string Value
        {
            get => _value;
            set
            {
                if (Refuse) { UndoAttempts++; throw new InvalidOperationException("The property restore was refused."); }
                _value = value;
            }
        }
        public void Delete() => Deleted = true;
        public void Import(string filePath) => ImportedPath = filePath;
    }
}
