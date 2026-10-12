using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class CodeDesignTests
{
    [Fact]
    public void Static_contract_lists_reject_mutation_and_keep_their_values()
    {
        var collections = new[]
        {
            WatchTableEntryXml.ReadOnlyEntryAttributes, CompileContract.Entries, FallbackContract.Entries,
            PlcExportContract.Entries, PlcImportContract.Entries, SaveCloseContract.Entries,
            SessionCandidateContract.Entries, SourceContract.Entries, OfflineAnalysisLogic.DefaultMarkers,
            OfflineAnalysisLogic.DefaultExtensions, OfflineAnalysisLogic.NormalizationRules,
        };
        foreach (var collection in collections)
        {
            string first = collection[0];
            Assert.False(collection is string[]);
            var mutable = Assert.IsAssignableFrom<IList<string>>(collection);
            Assert.True(mutable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutable[0] = "changed");
            Assert.Equal(first, collection[0]);
        }
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("fr-FR")]
    public void Machine_release_parsing_and_xml_addresses_ignore_current_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Null(HardwareCapabilities.Unsupported("21", "ManageCommunicationConnection"));
            Assert.Equal("CommunicationConnections requires V21.",
                HardwareCapabilities.Unsupported("20", "ManageCommunicationConnection"));
            Assert.Equal("Name", WatchTableEntryXml.AttributeFor("\u00ad%I0.0"));
            var parse = typeof(HmiTemplateLayoutAnalyzer).GetMethod("GetJsonInt", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Equal(2, parse.Invoke(null, new object?[] { JsonValue.Create("1.6"), 800 }));
        }
        finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preconditions_keep_the_message_parameter_and_failure_mapping(bool argument)
    {
        var error = new AdapterPreconditionException("Rejected.", "input", argument);
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(new ArgumentException("Rejected.", "input").Message, error.Message);
        Assert.Equal("input", HostFailurePolicy.Parameter(error));
        Assert.Equal(argument ? HostFailureKind.Argument : HostFailureKind.Precondition,
            HostFailurePolicy.Classify(error, false, false));
    }

    [Fact]
    public void Renamed_exception_types_keep_error_payloads_and_channel_semantics()
    {
        var error = new Error("Rejected.", new InvalidArgumentDetails("input", Array.Empty<string>()));
        var rejected = new DeviceCreationRejectionException(error);
        Assert.Same(error, rejected.Error);
        Assert.Equal(error.Message, rejected.Message);
        Assert.EndsWith("Exception", rejected.GetType().Name, StringComparison.Ordinal);
    }
    [Fact]
    public void Absent_lineage_uses_a_Try_signal_with_a_non_null_output()
    {
        string root = System.IO.Path.Combine(AppContext.BaseDirectory, "absent-lineage-" + Guid.NewGuid().ToString("N"));
        var store = new TiaMcp.Logic.Generation.GenerationRunStore(root);
        Assert.False(store.TryGetLineage("Line.ap21", "21", out var lineage));
        Assert.NotNull(lineage);
        Assert.Empty(lineage);
        Assert.False(System.IO.Directory.Exists(root));
    }

}
