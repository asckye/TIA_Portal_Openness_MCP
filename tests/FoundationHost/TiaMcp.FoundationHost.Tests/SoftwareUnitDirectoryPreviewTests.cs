extern alias enginehost;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;
using TiaMcpServer.ModelContextProtocol;
using Xunit;
using Organisation = enginehost::TiaMcpServer.Siemens.Services.PlcOrganisationPortService;
using HostContract = enginehost::TiaMcpServer.ModelContextProtocol.HardwareContract;

public sealed class SoftwareUnitDirectoryPreviewTests
{
    [Fact]
    public void MixedProgramTagOptionsRemainUnsupportedAcrossTheActualWorkerFailureMapping()
    {
        string directory = Path.GetFullPath(Path.Combine("bin-build", "su1-mixed-fixtures", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        PlcTagTableXmlBuilder.BuildDocumentForRelease("Tags", new[] { new PlcTagDefinition("Ready", "Bool", "%M0.0") }, "21")
            .Save(Path.Combine(directory, "Tags.xml"));
        var request = new PlcBatchImportRequest(software:"devices/PLC/CPU", directory:directory, program:true) {
            Release="21", Project=@"C:\Fixture.ap21", SwImportOptions=new[] { "IgnoreMissingReferencedObjects" } };
        int imports = 0;
        var refusal = Assert.Throws<AdapterPreconditionException>(() => PlcBatchImportPolicy.Run(request, Array.Empty<PlcBatchImportObject>(),
            () => { }, (_, target) => { imports++; return new[] { target }; }));
        Assert.Equal(PlcImportOptionPolicy.UnsupportedCondition, refusal.Condition);
        Assert.Equal(0, imports);
        var classification = WorkerFailurePolicy.Classify(refusal, true, false);
        Assert.Equal(ChannelOutcome.RejectedBeforeNative, classification.Outcome);
        var worker = WorkerOperationException.FromChannelFailure(new ChannelFailureException(refusal.Message, classification.Code,
            classification.Outcome, WorkerJson.Evidence(refusal)));
        var result = FoundationV4Result.Failure("21", "ImportPlcProgramFromDirectory", "test", true, true, null, worker).StructuredContent!;
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result["error"]?["code"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
        Assert.False((bool?)result["meta"]?["requiresSessionReset"]);
        var unknown = new WorkerOperationException("unknown", -32603, "unknown", WorkerJson.Evidence(refusal));
        var uncertain = FoundationV4Result.Failure("21", "ImportPlcProgramFromDirectory", "test", true, true, null, unknown).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)uncertain["error"]?["code"]);
        Assert.True((bool?)uncertain["meta"]?["requiresSessionReset"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectoryPreviewForwardsTheExactUnitAndCultureWhileDefaultRootExecutionKeepsItsPath(bool preview)
    {
        using var release = HostContract.UseRelease("21");
        string directory = Path.GetFullPath(Path.Combine("bin-build", "su1-tag-fixtures", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        PlcTagTableXmlBuilder.BuildDocumentForRelease("Tags", new[] { new PlcTagDefinition("Ready", "Bool", "%M0.0") }, "21")
            .Save(Path.Combine(directory, "Tags.xml"));
        const string software = "devices/PLC/CPU";
        var calls = new List<(string Operation, JsonObject Arguments)>();
        var service = new Organisation((operation, arguments) => {
            calls.Add((operation, (JsonObject)arguments.DeepClone()));
            return operation == "ImportTagTables" ? new JsonObject { ["Executed"]=false }
                : new JsonObject { ["Message"]=software,["Found"]=true };
        }, () => true, () => @"C:\Fixture.ap21");
        var result = preview
            ? service.ImportPlcTagTablesFromDirectory(software, "Nested", directory, unitName:"Unit A/B", importOptions:new[] { "SkipInactiveCultures" }, dryRun:true)
            : service.ImportPlcTagTablesFromDirectory(software, "", directory);
        Assert.Empty(result.Failed!);
        Assert.Equal(preview, (bool?)result.Meta?["dryRun"]);
        if (preview)
        {
            Assert.Empty(result.Imported!);
            var dispatched = Assert.Single(calls, call => call.Operation == "ImportTagTables").Arguments;
            Assert.Equal("Unit A/B", (string?)dispatched["unitName"]);
            Assert.Equal("Nested", (string?)dispatched["folderPath"]);
            Assert.Equal("SkipInactiveCultures", (string?)dispatched["importOptions"]?[0]);
            Assert.True((bool?)dispatched["dryRun"]);
            Assert.Single(result.Meta!["previewFiles"]!.AsArray());
            Assert.DoesNotContain(calls, call => (string?)call.Arguments["request"]?["Action"] == "importTagTable");
        }
        else
        {
            Assert.Equal("Tags", Assert.Single(result.Imported!));
            Assert.DoesNotContain(calls, call => call.Operation == "ImportTagTables");
            Assert.Single(calls, call => (string?)call.Arguments["request"]?["Action"] == "importTagTable");
        }
    }
}
