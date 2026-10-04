using ModelContextProtocol;
using Newtonsoft.Json;
using System.Text.Json;
using Xunit;

public sealed class GoldenWireTests
{
    public static IEnumerable<object[]> Cases() => GoldenSamples.All().SelectMany(sample =>
        new[] { new object[] { sample.Name, "worker" }, new object[] { sample.Name, "host" } });

    [Theory]
    [MemberData(nameof(Cases))]
    public void ExactJsonIsUnchanged(string name, string codec)
    {
        var sample = GoldenSamples.All().Single(s => s.Name == name).Value;
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GoldenWire.json")));
        // Frozen pre-P2-04b baseline: the worker used Newtonsoft defaults;
        // LegacyHost uses the MCP SDK's STJ options. Do not regenerate these bytes.
        var actual = codec == "worker" ? JsonConvert.SerializeObject(sample)
            : System.Text.Json.JsonSerializer.Serialize(sample, McpJsonUtilities.DefaultOptions);
        Assert.Equal(golden.RootElement.GetProperty(name).GetProperty(codec).GetString(), actual);
    }
}
