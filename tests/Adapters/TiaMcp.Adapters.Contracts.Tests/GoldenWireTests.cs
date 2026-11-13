using ModelContextProtocol;
using Newtonsoft.Json;
using System.Text.Json;
using Xunit;

public sealed class GoldenWireTests
{
    [Theory]
    [InlineData("Failure")]
    [InlineData("PartialSuccess")]
    [InlineData("987654")]
    [InlineData(null)]
    public void NativeDocumentEvidenceRoundTripsThroughBothCodecs(string? state)
    {
        var value = new TiaMcp.Adapters.PlcDocumentExportResult { NativeResult = new TiaMcp.Adapters.Contracts.NativeResultEvidence {
            EnumType = TiaMcp.Adapters.Contracts.NativeResultStates.Documents, State = state } };
        var worker = JsonConvert.DeserializeObject<TiaMcp.Adapters.PlcDocumentExportResult>(JsonConvert.SerializeObject(value))!;
        var host = System.Text.Json.JsonSerializer.Deserialize<TiaMcp.Adapters.PlcDocumentExportResult>(
            System.Text.Json.JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions), McpJsonUtilities.DefaultOptions)!;
        foreach (var restored in new[] { worker, host })
        {
            Assert.Equal(value.NativeResult.EnumType, restored.NativeResult!.EnumType);
            Assert.Equal(state, restored.NativeResult.State);
        }
    }
    public static IEnumerable<object[]> Cases() => GoldenSamples.All().SelectMany(sample =>
        new[] { new object[] { sample.Name, "worker" }, new object[] { sample.Name, "host" } });

    [Theory]
    [MemberData(nameof(Cases))]
    public void ExactJsonIsUnchanged(string name, string codec)
    {
        var sample = GoldenSamples.All().Single(s => s.Name == name).Value;
        // The worker baseline uses Newtonsoft defaults and the host baseline uses the MCP SDK options.
        var actual = codec == "worker" ? JsonConvert.SerializeObject(sample)
            : System.Text.Json.JsonSerializer.Serialize(sample, McpJsonUtilities.DefaultOptions);
        // P7-07c adds optional failure evidence to these two worker replies.
        // Keep the frozen bytes for every pre-existing member unchanged.
        if (name.StartsWith("PlcDocumentExportResult.", StringComparison.Ordinal)
            || name.StartsWith("PlcBatchDocumentExportResult.", StringComparison.Ordinal))
        {
            if (codec == "worker")
            {
                var extended = Newtonsoft.Json.Linq.JObject.Parse(actual);
                extended.Remove("NativeResult");
                actual = extended.ToString(Newtonsoft.Json.Formatting.None);
            }
            else
            {
                var extended = System.Text.Json.Nodes.JsonNode.Parse(actual)!.AsObject();
                extended.Remove("NativeResult"); extended.Remove("nativeResult");
                actual = extended.ToJsonString(McpJsonUtilities.DefaultOptions);
            }
        }
        if (Environment.GetEnvironmentVariable("TIA_MCP_UPDATE_GOLDEN") == "1") UpdateGolden(name, codec, actual);
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GoldenWire.json")));
        Assert.Equal(golden.RootElement.GetProperty(name).GetProperty(codec).GetString(), actual);
    }

    private static void UpdateGolden(string name, string codec, string actual)
    {
        string path = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "tests", "Adapters", "TiaMcp.Adapters.Contracts.Tests", "GoldenWire.json");
        var golden = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        golden[name]![codec] = actual;
        string json = golden.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "GoldenWire.json"), json, new System.Text.UTF8Encoding(false));
    }
}
