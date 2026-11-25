using Siemens.Engineering;
using Siemens.Engineering.SW.Tags;
using TiaMcp.Adapters.Native.Plc;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class TagWriteRefusalTests
{
    public static IEnumerable<object[]> Cases() =>
        from tool in new[] { "tag", "constant", "table", "import" }
        from fault in new[] { "unchanged", "changed", "different-case", "unreadable", "timeout", "abort", "disposed" }
        select new object[] { tool, fault };

    [Theory, MemberData(nameof(Cases))]
    public void Refusal_requires_a_fresh_exact_read(string tool, string fault)
    {
        var group = new PlcTagTableGroup();
        var table = new PlcTagTable();
        int writes = 0;
        void Inject<T>(NamedObjects<T> items) where T : PlcObject, new() => items.OnCreate = item => {
            writes++;
            if (fault == "changed") items.Add(item);
            if (fault == "different-case") { item.Name = item.Name.ToUpperInvariant(); items.Add(item); }
            if (fault == "unreadable") items.Unreadable = true;
            if (fault == "abort") throw new EngineeringUserAbortException();
            if (fault == "timeout") throw new TimeoutException();
            if (fault == "disposed") throw new ObjectDisposedException("Table");
            throw new EngineeringException();
        };
        Inject(group.TagTables);
        Inject(table.Tags);
        Inject(table.UserConstants);
        var error = Record.Exception(() => {
            switch (tool)
            {
                case "tag": PlcTagWrites.CreateTag(table, "Ready", "Bool", "%M0.0"); break;
                case "constant": PlcTagWrites.CreateConstant(table, "Ready", "Bool", "true"); break;
                case "table": PlcTagWrites.CreateTable(group, "Signals"); break;
                default: Import(group.TagTables); break;
            }
        });
        Assert.NotNull(error);
        Assert.Equal(1, writes);
        Assert.Equal(fault == "unchanged", error.Data["foundationKnownNoMutation"] is true);
    }

    private sealed class DerivedAbort : EngineeringUserAbortException { }
    private sealed class DerivedDisposal : EngineeringObjectDisposedException { }
    private sealed class DerivedFatal : NonRecoverableException { }
    private sealed class DerivedManagedDisposal : ObjectDisposedException
    {
        internal DerivedManagedDisposal() : base("Table") { }
    }

    [Theory]
    [InlineData("abort")]
    [InlineData("disposed")]
    [InlineData("managed-disposed")]
    [InlineData("fatal")]
    public void A_wrapped_native_interruption_subclass_preserves_the_unknown_write(string fault)
    {
        Exception cause = fault switch {
            "abort" => new DerivedAbort(),
            "disposed" => new DerivedDisposal(),
            "managed-disposed" => new DerivedManagedDisposal(),
            _ => new DerivedFatal()
        };
        var table = new PlcTagTable();
        table.Tags.OnCreate = _ => throw new InvalidOperationException("Create interrupted", cause);
        var error = Assert.Throws<InvalidOperationException>(() => PlcTagWrites.CreateTag(table, "Ready", "Bool", "%M0.0"));
        Assert.False(error.Data.Contains("foundationKnownNoMutation"));
    }

    [Theory]
    [InlineData(true, "Signals")]
    [InlineData(false, null)]
    public void Overwrite_or_unproven_single_name_cannot_settle_an_import(bool overwrite, string? name)
    {
        var tables = new PlcTagTableComposition { OnCreate = _ => throw new EngineeringException() };
        var failure = Assert.Throws<EngineeringException>(() => Import(tables, overwrite ? ImportOptions.Override : ImportOptions.None, name));
        Assert.False(failure.Data.Contains("verifiedAbsent"));
    }

    internal static void Import(PlcTagTableComposition tables, ImportOptions options = ImportOptions.None, string? name = "Signals")
    {
        var file = new FileInfo(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N") + ".xml"));
        File.WriteAllText(file.FullName, "<Document><Engineering version=\"V20\"/><SW.Tags.PlcTagTable ID=\"0\">" +
            "<AttributeList><Name>Signals</Name></AttributeList><ObjectList><SW.Tags.PlcTag ID=\"1\" CompositionName=\"Tags\">" +
            "<AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.0</LogicalAddress><Name>Ready</Name></AttributeList>" +
            "</SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>");
        try { PlcTagWrites.Import(tables, file, options, name, null); }
        finally { file.Delete(); }
    }
}
