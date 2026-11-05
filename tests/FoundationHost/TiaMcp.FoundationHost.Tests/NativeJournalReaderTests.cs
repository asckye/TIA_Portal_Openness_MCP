using System.Text.Json.Nodes;
using TiaMcp.Logic.ModelContextProtocol;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class NativeJournalReaderTests
{
    [Fact]
    public void Filters_by_request_id_and_since()
    {
        string root = Path.Combine(Path.GetTempPath(), "journal-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllLines(Path.Combine(root, "calls-fixture.jsonl.previous"), new[] {
                Row("wanted", "2026-11-01T00:00:00Z"), Row("wanted", "2026-11-02T00:00:00Z") });
            File.WriteAllLines(Path.Combine(root, "calls-fixture.jsonl"), new[] {
                Row("other", "2026-11-04T00:00:00Z"), Row("wanted", "2026-11-03T00:00:00Z"),
                Row("wanted", "2026-11-04T00:00:00Z") });
            var result = NativeJournalReader.Read(root, 2, "wanted", new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
            Assert.Equal(3, (int?)result["matchedRecords"]);
            Assert.Equal(2, result["records"]!.AsArray().Count);
            Assert.Equal("2026-11-03T00:00:00Z", (string?)result["records"]?[0]?["utc"]);
            Assert.All(result["records"]!.AsArray(), row => Assert.Equal("wanted", (string?)row?["id"]));
            Assert.Equal(5, (int?)NativeJournalReader.Read(root, 1)["matchedRecords"]);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Row(string id, string utc) => new JsonObject { ["id"] = id, ["utc"] = utc }.ToJsonString();
}
