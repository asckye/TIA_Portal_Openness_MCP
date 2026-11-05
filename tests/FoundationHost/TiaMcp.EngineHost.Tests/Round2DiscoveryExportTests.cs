using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

public sealed partial class PipelineTests
{
    [Theory]
    [InlineData("SaveProject", "project")]
    [InlineData("WritePlcSclSourceFile", "mojibake")]
    public async Task Multiple_description_words_need_a_name_or_alias_hit(string name, string capability)
    {
        using var fixture = new Fixture(all: true);
        var descriptor = fixture.Catalog.All[name];
        const string description = "uniqueincidentalneedle secondneedle";
        descriptor.Tool.Description = description;
        ((IDictionary<string, ToolDescriptor>)fixture.Catalog.All)[descriptor.Name] = new ToolDescriptor(descriptor.Name,
            description, descriptor.Tool, descriptor.Classification, descriptor.Signature, descriptor.Parameters,
            descriptor.DryRun, descriptor.CandidateFamily, descriptor.Execution);
        Assert.Single(McpServer.FindTools("uniqueincidentalneedle").Items!.Where((_, i) => i % 3 == 0));
        Assert.Empty(McpServer.FindTools(description).Items ?? Array.Empty<string>());
        Assert.Single(McpServer.FindTools(capability + " uniqueincidentalneedle").Items!.Where((_, i) => i % 3 == 0));
        async Task<JsonArray> Search(string query)
        {
            var result = await fixture.Call("FindTools", JsonSerializer.Serialize(new { query }));
            Assert.False(result.IsError ?? false);
            return result.StructuredContent!["data"]!["items"]!.AsArray();
        }
        Assert.Equal(3, (await Search("uniqueincidentalneedle")).Count);
        Assert.Empty(await Search(description));
        Assert.Equal(3, (await Search(capability + " uniqueincidentalneedle")).Count);
        Assert.Empty(fixture.Worker.Calls);
    }

    [Fact]
    public void Single_word_name_matches_rank_above_description_matches()
    {
        using var fixture = new Fixture(all: true);
        var descriptor = fixture.Catalog.All["GetSessionState"];
        descriptor.Tool.Description = "project";
        ((IDictionary<string, ToolDescriptor>)fixture.Catalog.All)[descriptor.Name] = new ToolDescriptor(descriptor.Name,
            "project", descriptor.Tool, descriptor.Classification, descriptor.Signature, descriptor.Parameters,
            descriptor.DryRun, descriptor.CandidateFamily, descriptor.Execution);
        var matches = McpServer.FindTools("project", limit: 100).Items!.Where((_, i) => i % 3 == 0).ToArray();
        int named = Array.FindIndex(matches, line => line.StartsWith("SaveProject("));
        int described = Array.FindIndex(matches, line => line.StartsWith("GetSessionState("));
        Assert.True(named >= 0 && described > named);
    }

    [Theory]
    [InlineData("pdf")] [InlineData("sinamics")] [InlineData("know-how")]
    public void Single_description_words_from_the_real_inventory_are_discoverable(string query)
    {
        using var fixture = new Fixture(all: true);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "manifest"))) root = root.Parent;
        var rows = JsonNode.Parse(File.ReadAllText(Path.Combine(root!.FullName, "manifest/tools-list.json")))!["tools"]!.AsArray();
        var expected = new HashSet<string>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            string name = (string)row["name"]!;
            string description = (string?)row["description"] ?? "";
            if (!fixture.Catalog.All.TryGetValue(name, out var descriptor)) continue;
            descriptor.Tool.Description = description;
            ((IDictionary<string, ToolDescriptor>)fixture.Catalog.All)[name] = new ToolDescriptor(name, description,
                descriptor.Tool, descriptor.Classification, descriptor.Signature, descriptor.Parameters, descriptor.DryRun,
                descriptor.CandidateFamily, descriptor.Execution);
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase) && description.Contains(query, StringComparison.OrdinalIgnoreCase)) expected.Add(name);
        }
        Assert.NotEmpty(expected);
        var matches = McpServer.FindTools(query, limit: 100).Items!.Where((_, i) => i % 3 == 0).ToArray();
        Assert.All(expected, name => Assert.Contains(matches, line => line.StartsWith(name + "(", StringComparison.Ordinal)));
        Assert.Empty(McpServer.FindTools(query + " nonexistent-capability", limit: 100).Items ?? Array.Empty<string>());
    }

    [Theory]
    [InlineData("20", "CallTool")] [InlineData("21", "CallTool")]
    [InlineData("20", "DecodePlcSimaticMl")] [InlineData("21", "DecodePlcSimaticMl")]
    [InlineData("20", "GetExportContent")] [InlineData("21", "GetExportContent")]
    public void Usage_appends_call_guidance_after_the_default_no_replay_guidance(string release, string name)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        var usage = ToolUsageCatalog.Describe(name, release, "plc-foundation", "", schema);
        using var stream = typeof(ToolUsageCatalog).Assembly.GetManifestResourceStream("TiaMcp.ToolUsage.json")!;
        using var reader = new StreamReader(stream);
        var defaults = JsonNode.Parse(reader.ReadToEnd())!["resultReading"]!["default"]!;
        var common = defaults.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        var actual = usage["resultReading"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(common, actual.Take(common.Length));
        Assert.True(actual.Length > common.Length);
        Assert.Contains(actual, line => line.Contains("unknown", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(actual, line => line.Contains("replay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Completed_result_stays_readable_when_the_export_cache_evicts_during_rendering()
    {
        using var fixture = new Fixture();
        var clock = typeof(ExportStore).GetField("NowUtc", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = clock.GetValue(null);
        int reads = 0;
        bool evicted = false;
        clock.SetValue(null, (Func<DateTime>)(() => {
            if (++reads == 3)
            {
                evicted = true;
                for (int i = 0; i < 33; i++) ExportStore.Put("pressure", "", "other");
            }
            return DateTime.UtcNow;
        }));
        try
        {
            fixture.Worker.Payload = new string('x', 60000);
            var result = await fixture.Call("GetSessionState", "{}");
            ExportStore.Put("pressure-after-render", "", "other");
            Assert.True(evicted);
            Assert.False(result.IsError ?? false);
            Assert.Equal("succeeded", (string?)result.StructuredContent!["meta"]!["outcome"]);
            Assert.NotEmpty((string)result.StructuredContent["data"]!["text"]!);
            Assert.Equal("GetExportContent", (string?)result.StructuredContent["data"]!["continuation"]!["name"]);
            Assert.Single(fixture.Worker.Calls);
        }
        finally { clock.SetValue(null, previous); }
    }

    [Fact]
    public async Task Export_content_fitting_retains_the_entry_after_lookup()
    {
        using var fixture = new Fixture(all: true);
        fixture.Worker.Payload = new string('x', 60000);
        var first = await fixture.Call("GetSessionState", "{}");
        string id = (string)first.StructuredContent!["data"]!["export"]!["id"]!;
        var clock = typeof(ExportStore).GetField("NowUtc", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = clock.GetValue(null);
        int reads = 0;
        bool evicted = false;
        clock.SetValue(null, (Func<DateTime>)(() => {
            if (++reads == 2)
            {
                evicted = true;
                for (int i = 0; i < 33; i++) ExportStore.Put("pressure", "", "other");
            }
            return DateTime.UtcNow;
        }));
        try
        {
            var result = await fixture.Call("GetExportContent", new JsonObject { ["exportId"] = id, ["offset"] = 0 }.ToJsonString());
            ExportStore.Put("pressure-after-render", "", "other");
            Assert.True(evicted);
            Assert.False(result.IsError ?? false);
            Assert.Equal(id, (string?)result.StructuredContent!["data"]!["export"]!["id"]);
            Assert.NotEmpty((string)result.StructuredContent["data"]!["text"]!);
            Assert.Single(fixture.Worker.Calls);
        }
        finally { clock.SetValue(null, previous); }
    }
}
