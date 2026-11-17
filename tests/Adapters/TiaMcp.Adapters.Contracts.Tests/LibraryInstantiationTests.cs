using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
using Siemens.Engineering.Library.Types;
using TiaMcp.Adapters.Native.Plc;
using Xunit;
using LibraryEngine = TiaMcp.Adapters.PlcFoundationEngine;

namespace Siemens.Engineering.Library.Types
{
    // The actual PublicAPI enum on all eight releases; there is no Released member.
    public enum LibraryTypeVersionState { InWork, Committed }
}

namespace TiaMcp.Adapters.Contracts.Tests
{
    public sealed class LibraryInstantiationTests
    {
        [Fact]
        public void Committed_version_reaches_instantiation_and_in_work_version_is_refused()
        {
            var fixture = new Fixture("block");
            LibraryEngine.RequireCommittedLibraryVersion(LibraryTypeVersionState.Committed);
            fixture.Run(true, "");
            Assert.True(fixture.Meta.ContainsKey("targets"));
            Assert.Equal(0, fixture.CreateCalls);
            fixture.Run(false, "");
            Assert.Equal(1, fixture.CreateCalls);
            Assert.Equal(true, fixture.Meta["operationSuccess"]);
            Assert.Throws<NotSupportedException>(() => LibraryEngine.RequireCommittedLibraryVersion(LibraryTypeVersionState.InWork));
        }

        public static IEnumerable<object[]> WideCases =>
            from kind in new[] { "block", "tag", "tagTable", "device", "classicScreen", "classicTagTable" }
            from preview in new[] { false, true }
            select new object[] { kind, preview };

        [Theory, MemberData(nameof(WideCases))]
        public void Wider_clash_is_refused_before_create_in_preview_and_execution(string kind, bool preview)
        {
            foreach (string mode in new[] { "", "ThrowIfExists" })
            {
                var fixture = new Fixture(kind);
                fixture.Add(fixture.Other, "Motor");
                var error = Assert.Throws<PlcSoftwareException>(() => fixture.Run(preview, mode));
                Assert.Equal("ALREADY_EXISTS", error.Code);
                Assert.Equal(0, fixture.CreateCalls);
                var target = Assert.IsType<Dictionary<string, object?>>(((List<object?>)fixture.Meta["targets"]!).Single());
                Assert.Equal(true, target["exists"]);
                var clash = Assert.IsType<Dictionary<string, object?>>(((List<object?>)target["clashes"]!).Single());
                Assert.Equal(kind == "tag" ? "Other/Table/Motor" : "Other/Motor", clash["path"]);
                Assert.Equal(false, clash["inTargetContainer"]);
            }
        }

        [Theory, MemberData(nameof(WideCases))]
        public void Replace_refuses_an_existing_name_outside_the_destination(string kind, bool preview)
        {
            var fixture = new Fixture(kind);
            var existing = fixture.Add(fixture.Other, "Motor");
            Assert.Throws<PlcSoftwareException>(() => fixture.Run(preview, "Replace"));
            Assert.Equal(0, fixture.CreateCalls);
            Assert.Contains(existing, fixture.Other);
            Assert.Equal(0, existing.DeleteCalls);
        }

        [Theory]
        [InlineData(false)][InlineData(true)]
        public void Incomplete_readback_rolls_back_new_objects_in_the_entire_scope(bool outsideTarget)
        {
            var fixture = new Fixture("block");
            HardwareObject? created = null;
            fixture.Create = _ => created = fixture.Add(outsideTarget ? fixture.Other : fixture.Target, "Motor");
            fixture.Run(false, "", new[] { "Motor", "Missing" });
            Assert.Equal(false, fixture.Meta["operationSuccess"]);
            Assert.Equal(false, fixture.Meta["readbackComplete"]);
            Assert.Empty(fixture.Target);
            Assert.Empty(fixture.Other);
            Assert.Equal(1, created!.DeleteCalls);
            Assert.Equal(true, fixture.Meta["rollbackCompleted"]);
            Assert.Equal(new object?[] { "Motor" }, (List<object?>)fixture.Meta["createdNames"]!);
        }

        [Fact]
        public void Execution_rechecks_the_wider_scope_after_a_successful_preview()
        {
            var fixture = new Fixture("block");
            fixture.Run(true, "");
            fixture.Add(fixture.Other, "Motor");
            Assert.Throws<PlcSoftwareException>(() => fixture.Run(false, ""));
            Assert.Equal(0, fixture.CreateCalls);
        }

        [Fact]
        public void Rename_preserves_the_wider_existing_object_and_reads_the_new_name()
        {
            var fixture = new Fixture("block");
            var existing = fixture.Add(fixture.Other, "Motor");
            fixture.Create = _ => fixture.Add(fixture.Target, "Motor_1");
            fixture.Run(false, "Rename");
            Assert.Equal(true, fixture.Meta["operationSuccess"]);
            Assert.Equal("Motor_1", fixture.Target.Single().Name);
            Assert.Same(existing, fixture.Other.Single());
            Assert.Equal(0, existing.DeleteCalls);
        }

        [Fact]
        public void Throwing_create_rolls_back_partial_objects_outside_the_destination()
        {
            var fixture = new Fixture("block");
            HardwareObject? created = null;
            fixture.Create = _ => {
                created = fixture.Add(fixture.Other, "Motor");
                throw new InvalidOperationException("CreateFrom failed after inserting an object.");
            };
            Assert.Throws<InvalidOperationException>(() => fixture.Run(false, ""));
            Assert.Empty(fixture.Other);
            Assert.Equal(1, created!.DeleteCalls);
            Assert.Equal(true, fixture.Meta["rollbackCompleted"]);
        }

        [Fact]
        public void Failed_replace_keeps_the_replacement_and_reports_unrestored_cleanup()
        {
            var fixture = new Fixture("block");
            var original = fixture.Add(fixture.Target, "Motor");
            HardwareObject? replacement = null;
            fixture.Create = _ => {
                fixture.Target.Remove(original);
                return replacement = fixture.Add(fixture.Target, "Motor");
            };
            fixture.Run(false, "Replace", new[] { "Motor", "Missing" });
            Assert.Equal(false, fixture.Meta["readbackComplete"]);
            Assert.Same(replacement, fixture.Target.Single());
            Assert.Equal(0, replacement!.DeleteCalls);
            Assert.Equal(0, original.DeleteCalls);
            Assert.Equal(true, fixture.Meta["unrestoredWrite"]);
            Assert.Equal(false, fixture.Meta["rollbackCompleted"]);
            Assert.NotEmpty((List<object?>)fixture.Meta["cleanupRequired"]!);
        }

        [Fact]
        public void Rollback_labels_match_names_when_native_walk_order_differs_from_content_order()
        {
            var fixture = new Fixture("block");
            var deleted = new List<string>();
            fixture.Create = _ => {
                var second = fixture.Add(fixture.Target, "B"); var first = fixture.Add(fixture.Target, "A");
                second.Deleting = () => { deleted.Add("B"); fixture.Target.Remove(second); };
                first.Deleting = () => { deleted.Add("A"); fixture.Target.Remove(first); };
                return first;
            };
            fixture.Run(false, "", new[] { "A", "B", "Missing" });
            Assert.Equal(2, deleted.Count);
            var restored = ((List<object?>)fixture.Meta["restoredChanges"]!).Cast<string>()
                .Where(label => label.StartsWith("Target/", StringComparison.Ordinal)).ToArray();
            Assert.Equal(deleted.Select(name => "Target/" + name + ": delete object created by this call if present"), restored);
            Assert.Empty(fixture.Target);
            Assert.Equal(true, fixture.Meta["rollbackCompleted"]);
        }

        [Fact]
        public void Classic_folder_paths_decode_after_slash_splitting_and_refuse_raw_backslash()
        {
            Assert.Equal(new[] { "Folder/One", "Back\\slash" }, LibraryEngine.LibraryTargetFolderParts("Folder%2FOne/Back%5Cslash"));
            Assert.Throws<AdapterPreconditionException>(() => LibraryEngine.LibraryTargetFolderParts("Folder\\Child"));
        }

        private sealed class Group : HardwareObject
        {
            public NativeNamedList<HardwareObject> Objects { get; } = new();
            public NativeNamedList<Group> Groups { get; } = new();
            public NativeNamedList<Group> Folders => Groups;
            public NativeNamedList<HardwareObject> Blocks => Objects;
            public NativeNamedList<HardwareObject> TagTables => Objects;
            public NativeNamedList<HardwareObject> Screens => Objects;
            public NativeNamedList<HardwareObject> Devices => Objects;
        }
        private sealed class Table : HardwareObject
        {
            public NativeNamedList<HardwareObject> Tags { get; } = new();
            public NativeNamedList<HardwareObject> UserConstants { get; } = new();
        }
        private sealed class Fixture
        {
            private readonly string kind;
            private readonly Group root = new();
            private readonly string contents;
            private readonly string groups;
            internal readonly NativeNamedList<HardwareObject> Target;
            internal readonly NativeNamedList<HardwareObject> Other;
            internal readonly Dictionary<string, object?> Meta = new() { ["mayHaveChanged"] = false };
            internal Func<object, object>? Create;
            internal int CreateCalls;
            internal Fixture(string kind)
            {
                this.kind = kind;
                contents = kind switch { "block" => "Blocks", "device" => "Devices", "classicScreen" => "Screens", _ => "TagTables" };
                groups = kind.StartsWith("classic", StringComparison.Ordinal) ? "Folders" : "Groups";
                var target = new Group { Name = "Target" }; var other = new Group { Name = "Other" };
                root.Groups.Add(target); root.Groups.Add(other);
                if (kind == "tag")
                {
                    var targetTable = new Table { Name = "Table" }; var otherTable = new Table { Name = "Table" };
                    target.Objects.Add(targetTable); other.Objects.Add(otherTable);
                    Target = targetTable.Tags; Other = otherTable.Tags;
                }
                else { Target = target.Objects; Other = other.Objects; }
            }
            internal HardwareObject Add(NativeNamedList<HardwareObject> collection, string name)
            {
                var item = new HardwareObject { Name = name };
                item.Deleting = () => collection.Remove(item);
                collection.Add(item); return item;
            }
            internal string Run(bool preview, string mode, string[]? names = null)
                => LibraryEngine.InstantiateLibraryContentsCore(kind, "ExactSoftware", kind == "tag" ? "Target/Table" : "Target",
                    names ?? new[] { "Motor" }, mode, preview, Meta,
                    collection => { CreateCalls++; return Create != null ? Create(collection) : Add(Target, "Motor"); },
                    _ => Target, () => LibraryEngine.LibraryScopeObjects(root, contents, groups, tags: kind == "tag"),
                    () => new System.IO.MemoryStream(), () => new System.IO.MemoryStream());
        }
    }
}
