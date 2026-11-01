using TiaMcp.Adapters.Native.Hmi;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class HmiB3OwnershipTests
{
    public class FaceplateLibraryType { }
    public sealed class NewVersionFaceplate : FaceplateLibraryType { }
    [Fact]
    public void Classic_library_matching_uses_type_name_across_API_versions_and_derived_types()
    {
        Assert.True(ClassicHmiLogic.LibraryTypeMatches(typeof(FaceplateLibraryType), "Old.Api.FaceplateLibraryType"));
        Assert.True(ClassicHmiLogic.LibraryTypeMatches(typeof(NewVersionFaceplate), "New.Api.FaceplateLibraryType"));
        Assert.False(ClassicHmiLogic.LibraryTypeMatches(typeof(Folder), "New.Api.FaceplateLibraryType"));
    }
    public sealed class Folder
    {
        public string Name { get; set; } = "";
        public Tables TagTables { get; } = new();
        public Folder[] TagTableGroups { get; set; } = [];
        public Folder[] Groups { get; set; } = [];
    }
    public sealed class Table { public string Name { get; set; } = ""; public object Parent { get; set; } = null!; }
    public sealed class Tables : List<Table>
    {
        public object CreatedParent = null!;
        public Table Create(string name) { var table = new Table { Name = name, Parent = CreatedParent }; Add(table); return table; }
    }
    [Fact]
    public void A_unique_plain_name_keeps_its_resolved_nested_parent_without_root_fallback()
    {
        var folder = new Folder { Name = "Nested" };
        var root = new Folder { TagTableGroups = [folder] };
        var table = new Table { Name = "Table", Parent = folder };
        folder.TagTables.Add(table);
        Assert.Same(table, HmiExchangePolicy.EnsureTagTable(root, "Table", () => throw new Exception("Must not create.")));
        table.Parent = root;
        Assert.Throws<InvalidOperationException>(() => HmiExchangePolicy.EnsureTagTable(root, "Table", () => throw new Exception("Must not create.")));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Existing_and_created_tables_must_belong_to_the_exact_requested_parent(bool existing, bool correct)
    {
        var nested = new Folder { Name = "Group" };
        var root = new Folder { TagTableGroups = [nested] };
        object parent = correct ? nested : root;
        nested.TagTables.CreatedParent = parent;
        if (existing) nested.TagTables.Add(new Table { Name = "Table", Parent = parent });
        int writes = 0;
        if (correct)
        {
            var result = HmiExchangePolicy.EnsureTagTable(root, "/Group/Table", () => writes++);
            Assert.Same(nested, ((Table)result).Parent);
        }
        else Assert.Throws<InvalidOperationException>(() => HmiExchangePolicy.EnsureTagTable(root, "/Group/Table", () => writes++));
        Assert.Equal(existing ? 0 : 1, writes);
    }
}
