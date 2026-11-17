extern alias plcpathfixture;
extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;
using PathEngine = plcpathfixture::TiaMcp.Adapters.PlcFoundationEngine;
using Engine = toolpipeline::TiaMcp.FoundationHost;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;

public sealed class PlcObjectPathHostTests
{
    public static IEnumerable<object[]> Releases() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Theory, MemberData(nameof(Releases))]
    public void Shared_exchange_path_checks_still_hold(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        ExchangeContractTests.Run((passed, message) => Assert.True(passed, message));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Existing_block_and_type_info_selection_accepts_tree_encoded_names(string release)
    {
        var engine = new PathEngine(release);
        foreach (string name in new[] { "A/B", "中文块" })
        {
            string path = engine.TreePath("G", name);
            var inventory = new[] { (Path: path, Name: name) };
            foreach (string parameter in new[] { "blockPath", "typePath" })
            {
                string canonical = TiaMcp.Adapters.PlcExchangePolicy.ObjectPath(path, false, parameter);
                var selected = TiaMcp.Adapters.PlcExchangePolicy.Exact(inventory, item => item.Path, canonical, parameter);
                Assert.Equal(name, selected.Name);
            }
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public void Exchange_paths_preserve_encoded_backslashes_and_refuse_raw_backslashes(string release)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        Assert.Equal("G%5CH/A%5CB", TiaMcp.Adapters.PlcExchangePolicy.ObjectPath("G%5CH/A%5CB"));
        Assert.Equal("G%5CH/A%255CB", TiaMcp.Adapters.PlcExchangePolicy.ObjectPath("G%5CH/A%255CB"));
        Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Adapters.PlcExchangePolicy.ObjectPath("G\\H/A%5CB"));
        Assert.Throws<AdapterPreconditionException>(() => TiaMcp.Adapters.PlcExchangePolicy.ObjectPath("G%5CH/A\\B"));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Missing_logic_read_has_the_B49_declared_target(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var catalog = new Engine.FoundationCatalog(release, LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false));
        using var context = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(worker) { Catalog = catalog });
        using var selected = Host.HardwareContract.UseRelease(release);
        var engine = new PathEngine(release);
        var service = CreateService(engine, release);
        var result = new Host.PlcReadModifyTools(service).DescribeBlockLogicV4("PLC", "G/Missing").StructuredContent!;
        Assert.Equal("NOT_FOUND", (string?)result["error"]?["code"]);
        Assert.Equal("/blockPath", (string?)result["error"]?["details"]?["target"]);
        Assert.Equal("rejected-before-operation", (string?)result["meta"]?["outcome"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Invalid_and_ambiguous_reads_preserve_parameter_and_canonical_candidates(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var catalog = new Engine.FoundationCatalog(release, LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false));
        using var context = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(worker) { Catalog = catalog });
        using var selected = Host.HardwareContract.UseRelease(release);
        var engine = new PathEngine(release);
        var group = new plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlockUserGroup { Name = "G", Parent = engine.Plc.BlockGroup };
        engine.Plc.BlockGroup.Groups.Add(group);
        group.Blocks.Add(new plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlock { Name = "A/B/C", Parent = group });
        var child = new plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlockUserGroup { Name = "A", Parent = group };
        group.Groups.Add(child);
        child.Blocks.Add(new plcpathfixture::Siemens.Engineering.SW.Blocks.PlcBlock { Name = "B/C", Parent = child });
        var tools = new Host.PlcReadModifyTools(CreateService(engine, release));
        foreach (string path in new[] { "G//B", "G/A/B/C" })
        {
            var result = tools.DescribeBlockLogicV4("PLC", path).StructuredContent!;
            Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
            Assert.Equal("/blockPath", (string?)result["error"]?["details"]?["parameter"]);
            if (path == "G/A/B/C")
            {
                Assert.Contains("G/A%2FB%2FC", (string?)result["error"]?["message"], StringComparison.Ordinal);
                Assert.Contains("G/A/B%2FC", (string?)result["error"]?["message"], StringComparison.Ordinal);
            }
        }
    }

    private static Services.PlcOrganisationPortService CreateService(PathEngine engine, string release)
        => new((_, arguments) => {
            var request = JsonSerializer.Deserialize<PlcSoftwareRequest>(arguments["request"]!.ToJsonString())!;
            try
            {
                engine.DescribePlcBlockLogic(request.Path, request.FilePath);
                throw new InvalidOperationException("Missing target unexpectedly exported.");
            }
            catch (AdapterPreconditionException error)
            {
                return JsonSerializer.SerializeToNode(new PlcSoftwareReply {
                    Failure = new PlcSoftwareFailure {
                        Code = StepFailureStatus.Of(error), Message = error.Message,
                        Evidence = new Dictionary<string, object?> {
                            ["status"] = StepFailureStatus.Of(error), ["parameter"] = StepFailureStatus.Parameter(error),
                            ["nativeMessage"] = error.Message,
                            ["mayHaveChanged"] = false }
                    }
                });
            }
        }, () => true, () => "fixture.ap" + release);
}
