using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using Xunit;

public sealed class BehaviorCapabilityTests
{
    private static readonly string[] Releases = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };
    public static IEnumerable<object[]> Policies() => Releases.SelectMany(release =>
        BehaviorCapabilities.Table(typeof(FoundationV4Tool).Assembly, release).Select(row => new object[] { release, (string)row!["family"]! }));

    [Theory]
    [MemberData(nameof(Policies))]
    public void DefaultCatalogHostLogicAndNativeSelectionsAgree(string release, string family)
    {
        var row = BehaviorCapabilities.Table(typeof(FoundationV4Tool).Assembly, release).Single(r => (string?)r!["family"] == family)!;
        Assert.Equal("current", (string?)row["state"]); Assert.Equal("NOT RUN", (string?)row["l5"]);
        Assert.Equal(BehaviorPolicy.Current, BehaviorCapabilities.Released(release, family));
        foreach (var assembly in new[] { typeof(FoundationV4Tool).Assembly, typeof(BehaviorCapabilities).Assembly, typeof(CandidatePolicy).Assembly })
        {
            Assert.Equal(BehaviorPolicy.Current, BehaviorCapabilities.Select(assembly, release, family));
            Assert.False(CandidatePolicy.Enabled(assembly, release, family));
        }
        // The worker host injects the PLC read mode. Read actual product metadata
        // and the injection members without loading Siemens code.
        var productRoot = Environment.GetEnvironmentVariable("TIA_MCP_TEST_PRODUCT_ROOT");
        if (productRoot == null) return;
        string dir = release == "14sp1" ? "V14Sp1" : release == "15.1" ? "V15_1" : "V" + release;
        string adapter = Path.Combine(productRoot, "src", "Adapters", dir, "bin", release, "Release", "net48", "TiaMcp.Adapter." + release + ".dll");
        Assert.True(File.Exists(adapter), adapter);
        var paths = new[] { adapter, Path.Combine(productRoot, "src", "PlcWorker", "bin", release, "Release", "net48", "TiaMcp.PlcWorker." + release + ".exe") };
        foreach (var path in paths)
        {
            Assert.True(File.Exists(path), path);
            using var input = File.OpenRead(path); using var pe = new PEReader(input);
            var reader = pe.GetMetadataReader();
            if (path == adapter)
                Assert.Contains(reader.PropertyDefinitions.Select(reader.GetPropertyDefinition), p => reader.GetString(p.Name) == "SourceCandidateEnabled");
            else
                Assert.Contains(reader.MemberReferences.Select(reader.GetMemberReference), m => reader.GetString(m.Name) == "set_SourceCandidateEnabled");
            var setting = reader.GetAssemblyDefinition().GetCustomAttributes().Select(handle => reader.GetCustomAttribute(handle))
                .Select(attribute => reader.GetBlobBytes(attribute.Value))
                .Where(blob => blob.Length > 3 && blob[0] == 1 && blob[1] == 0)
                .Select(blob => ReadPolicy(blob)).SingleOrDefault(value => value != null);
            Assert.DoesNotContain(family + ":safe-v4", BehaviorCapabilities.TestFamilies(setting));
        }
    }

    private static string? ReadPolicy(byte[] blob)
    {
        // AssemblyMetadataAttribute's two SerStrings; ignore unrelated attributes.
        if (blob.Length < 20 || blob[2] != 16 || System.Text.Encoding.UTF8.GetString(blob, 3, 16) != BehaviorCapabilities.TestProperty) return null;
        int length = blob[19];
        return length == 255 ? null : System.Text.Encoding.UTF8.GetString(blob, 20, length);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task InventoryDisclosesEveryFoundationAdmissionAndEnvelopeOutcome(string release, string family)
    {
        var row = BehaviorCapabilities.Table(typeof(FoundationV4Tool).Assembly, release).Single(r => (string?)r!["family"] == family)!;
        var worker = new CandidateWorkerFixture();
        foreach (string name in row["entries"]!.AsArray().Select(e => (string)e!))
        {
            foreach (var outcome in new[] { Outcome.Succeeded, Outcome.RejectedBeforeOperation, Outcome.ReadFailed, Outcome.Failed, Outcome.Partial, Outcome.Unknown })
            {
                var execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                    : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed ? Execution.ReadOnly : Execution.Completed;
                Error? error = outcome == Outcome.Succeeded ? null : outcome == Outcome.Unknown
                    ? new Error("Unknown fixture outcome.", new OutcomeUnknownDetails("fixture", new Dictionary<string, JsonElement>()))
                    : outcome == Outcome.Partial ? new Error("Partial fixture outcome.", new PartialFailureDetails(1, 1, 0))
                    : new Error("Rejected fixture outcome.", new InvalidArgumentDetails("arguments", Array.Empty<string>()));
                var meta = new Meta(DateTimeOffset.UtcNow, release, name, "inventory", outcome, execution, outcome == Outcome.Unknown,
                    BehaviorPolicy.NotApplicable, outcome == Outcome.Unknown ? Completeness.Unknown : outcome == Outcome.Partial ? Completeness.Partial : Completeness.Complete,
                    null, Array.Empty<Warning>());
                var body = BehaviorCapabilities.Disclose(Envelope.Create(new JsonObject(), error, meta));
                Assert.Equal(BehaviorPolicy.Current, body.Meta.BehaviorPolicy);
                Assert.Single(body.Meta.Warnings, w => w.Code == WarningCode.UnverifiedBehavior);
                Assert.Equal(outcome, body.Meta.Outcome); Assert.Equal(execution, body.Meta.Execution);
                Assert.Equal(BehaviorPolicy.Current, V4Json.Deserialize<Envelope>(McpResult.From(body).Content[0].Text).Meta.BehaviorPolicy);
            }
            if (release is "20" or "21") continue;
            var definition = FoundationTools.Definitions.Single(d => FoundationV4Tool.Name(d.Name) == name);
            var tool = new FoundationV4Tool(new FoundationTool(definition, worker), release);
            Assert.Contains("behaviorPolicy=current", tool.ProtocolTool.Description);
            Assert.Contains("V4 native acceptance is pending", tool.ProtocolTool.Description);
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"Sentinel\":1,\"sentinel\":2}") } };
            var result = await tool.InvokeAsync(request);
            var rejected = V4Json.Deserialize<Envelope>(((TextContentBlock)result.Content.Single()).Text);
            Assert.Equal(Outcome.RejectedBeforeOperation, rejected.Meta.Outcome); Assert.Equal(BehaviorPolicy.Current, rejected.Meta.BehaviorPolicy);
            Assert.Contains(rejected.Meta.Warnings, w => w.Code == WarningCode.UnverifiedBehavior);
            Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        }
        Assert.Equal(0, worker.Calls);
    }
}
