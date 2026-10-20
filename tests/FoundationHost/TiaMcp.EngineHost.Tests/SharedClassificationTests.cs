extern alias foundationhost;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;
using Xunit;
using Foundation = foundationhost::TiaMcp.FoundationHost;

// A shared host implementation takes its tool's reviewed classification (ToolMetadata), as a declaration does.
// Batch admission and the outcome of a failure depend on it; before P8-04k3b the host synthesized
// READ/OFFLINE as batch-readable and WRITE/FILE as batch-previewable, so a failing GetSessionState read was
// reported as an unknown outcome on every release whose catalog serves the shared implementation.
public sealed class SharedClassificationTests
{
    public static IEnumerable<object[]> Releases()
        => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(key => new object[] { key });

    [Theory, MemberData(nameof(Releases))]
    public void Every_shared_implementation_carries_its_reviewed_classification(string release)
    {
        string previous = EngineHostConfiguration.ReleaseKey;
        EngineHostConfiguration.ReleaseKey = release;
        try
        {
            using var worker = new MetadataWorker();
            var shared = Foundation.LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
            var catalog = new FoundationCatalog(release, shared);
            int compared = 0;
            foreach (var tool in shared.Select(tool => tool.ProtocolTool.Name).Where(catalog.IncludingUnavailable.ContainsKey))
            {
                var reviewed = ToolMetadata.Find(tool);
                Assert.True(reviewed != null, tool + " has no reviewed classification");
                var actual = catalog.IncludingUnavailable[tool].Classification!;
                Assert.Equal((reviewed!.Layer, reviewed.Domain, reviewed.Operation, reviewed.BatchRead, reviewed.BatchWrite),
                    (actual.Level, actual.Domain, actual.Operation, actual.BatchRead, actual.BatchWrite));
                compared++;
            }
            Assert.True(compared > 200, "Too few shared implementations compared: " + compared);
            Assert.True(catalog.IncludingUnavailable["GetSessionState"].Classification!.BatchRead);
        }
        finally { EngineHostConfiguration.ReleaseKey = previous; }
    }

    // One row per class of tool: a session read the review admits to read batches, a read and a file export the
    // review keeps out of batches, a write the review admits to preview batches, and a Workbench read.
    [Theory]
    [InlineData("GetSessionState", "SESSION", true, false)]
    [InlineData("ListPlcBlocks", "READ", false, false)]
    [InlineData("ExportPlcBlock", "FILE", false, false)]
    [InlineData("CreateProject", "WRITE", false, false)]
    [InlineData("DeletePlcBlock", "WRITE", false, true)]
    [InlineData("GetWorkbenchState", "READ", true, false)]
    public void Shared_classification_follows_the_reviewed_row_for_each_class_of_tool(string name, string operation, bool batchRead, bool batchWrite)
    {
        var reviewed = ToolMetadata.Find(name)!;
        Assert.Equal((operation, batchRead, batchWrite), (reviewed.Operation, reviewed.BatchRead, reviewed.BatchWrite));
        var actual = SharedToolCatalog.SharedClassification(new Tool { Name = name, Description = "fixture" });
        Assert.Equal((reviewed.Layer, reviewed.Domain, operation, batchRead, batchWrite), (actual.Level, actual.Domain, actual.Operation, actual.BatchRead, actual.BatchWrite));
    }

    [Fact]
    public void A_tool_without_a_reviewed_row_keeps_the_operation_defaults()
    {
        Assert.Null(ToolMetadata.Find("ListSharedClassificationFixture"));
        var actual = SharedToolCatalog.SharedClassification(new Tool { Name = "ListSharedClassificationFixture", Description = "Lists fixture rows." });
        var operation = ToolTaxonomy.OperationOf("ListSharedClassificationFixture", "Lists fixture rows.").Operation;
        Assert.Equal(("L1", operation, operation is "READ" or "OFFLINE", operation is "WRITE" or "FILE"), (actual.Level, actual.Operation, actual.BatchRead, actual.BatchWrite));
    }

    // The engine reported a failing read as INTERNAL_ERROR/read-failed; the host now does the same for its shared
    // implementations, and a failing write stays an unknown outcome.
    [Theory]
    [InlineData("21", "GetSessionState", "INTERNAL_ERROR", "read-failed", "read-only")]
    [InlineData("17", "GetSessionState", "INTERNAL_ERROR", "read-failed", "read-only")]
    [InlineData("21", "ListPlcBlocks", "INTERNAL_ERROR", "read-failed", "read-only")]
    [InlineData("21", "CreateProject", "OUTCOME_UNKNOWN", "unknown", "unknown")]
    public void A_failed_shared_call_reports_the_reviewed_read_or_write_outcome(string release, string tool, string code, string outcome, string execution)
    {
        string previous = EngineHostConfiguration.ReleaseKey;
        EngineHostConfiguration.ReleaseKey = release;
        try
        {
            using var worker = new MetadataWorker();
            var shared = Foundation.LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
            using var session = EngineHostConfiguration.Enter(new EngineSessionContext(new SessionWorker()) { Catalog = new FoundationCatalog(release, shared) });
            var result = McpServer.TargetFailure(tool, new InvalidOperationException("fixture failure"), issued: true).StructuredContent!;
            Assert.Equal((code, outcome, execution), ((string?)result["error"]?["code"], (string?)result["meta"]?["outcome"], (string?)result["meta"]?["execution"]));
        }
        finally { EngineHostConfiguration.ReleaseKey = previous; }
    }

    private sealed class MetadataWorker : Foundation.IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
            => throw new InvalidOperationException("Building the catalog must not dispatch a worker operation.");
        public void Dispose() { }
    }

    private sealed class SessionWorker : IEngineWorker
    {
        public bool Faulted => false;
        public JsonNode? Binding => null;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => throw new InvalidOperationException("Unexpected worker observation.");
        public Task<JsonObject> Status(CancellationToken token) => throw new InvalidOperationException("Unexpected worker observation.");
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("Unexpected worker restart.");
        public Task<IDisposable> Acquire(CancellationToken token) => throw new InvalidOperationException("Unexpected worker acquisition.");
        public Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("Unexpected worker dispatch.");
        public void Dispose() { }
    }
}
