using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class LowBetaFindingsSources
{
    private static readonly string Root = Repository.FindRoot(AppContext.BaseDirectory);
    private static string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

    [Fact]
    public void Connection_candidates_render_only_the_public_hardware_type()
    {
        string source = Read("src/Adapters/Native/Hardware/HardwareNetworkLegacy.Probes.cs");
        string candidates =
            source[..source.IndexOf("public HardwareProbeLines HardwareProbeHardwareHmiConnectionWhitelistedServices", StringComparison.Ordinal)];
        Assert.Contains("HardwarePublicType.Name(c.Target)", candidates);
        Assert.DoesNotContain("c.Target.GetType().FullName", candidates);
    }

    [Theory]
    [InlineData("src/Shared/HardwareAddressTools.cs")]
    [InlineData("src/Shared/ModulesTools.cs")]
    [InlineData("src/Adapters/Native/Plc/PlcOrganisationAdapter.Delete.cs")]
    public void Write_warnings_use_plain_text_and_separate_sentences(string path)
    {
        string source = Read(path);
        Assert.DoesNotContain("⚠", source);
        Assert.Contains("Warning:", source);
        if (path.EndsWith(".Delete.cs", StringComparison.Ordinal))
            Assert.Contains("the UDT is unused. ", source);
    }

    [Fact]
    public void Syslog_wrong_module_message_names_the_declared_item_path()
    {
        string source = Read("src/Adapters/Native/Security/SecurityDeepAdapter.cs");
        Assert.DoesNotContain("non-empty itemPathJson", source);
        Assert.Contains("HardwareServicesPolicy.Argument(\"itemPathJson\"", source);
    }

    [Fact]
    public void Generated_source_result_explains_the_ASCII_import_requirement()
    {
        string source = Read("src/Adapters/Native/Plc/PlcSoftwareEditing.cs");
        string generation = source[source.IndexOf("public HardwareAddressingReply GeneratePlcSourceFromBlocks", StringComparison.Ordinal)..];
        generation = generation[..generation.IndexOf("public HardwareAddressingReply UpdatePlcProgram", StringComparison.Ordinal)];
        Assert.Contains("re-save", generation);
        Assert.Contains("ASCII", generation);
        Assert.Contains("ImportPlcExternalSource", generation);
    }

    [Fact]
    public void Classic_HMI_worker_excludes_restored_rollbacks_from_unknown_writes()
    {
        string source = Read("src/Adapters/Native/Hmi/HmiWorker.cs");
        Assert.Contains("message.Meta.TryGetValue(\"rollbackCompleted\"", source);
        Assert.Contains("!restored && !partial", source);
        Assert.Contains("if (restored) reply.MayHaveChanged = false;", source);
    }

    [Fact]
    public void Graphic_selection_examples_teach_the_public_label()
    {
        foreach (string path in new[] { "reference/tool-examples/calls.json", "reference/tool-catalog/EngineSourceExamples.json" })
            Assert.DoesNotContain("ReadUnifiedGraphicSelection", Read(path));
    }
}
