using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaOpenness.Core;
using TiaOpenness.Tests;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class EngineToolResultTests
{
    public static IEnumerable<object[]> Supplemental() => V4Fixtures.Supplemental().Select(e => new object[] { e.GetRawText() });

    [Theory, MemberData(nameof(Supplemental))]
    public void EveryErrorCodeAndOutcomeRetainsItsDetails(string json)
    {
        var original = JsonNode.Parse(json)!;
        var result = EngineToolResult.Read(json);
        Assert.Equal(original["ok"]!.GetValue<bool>(), result.Envelope.Ok);
        Assert.True(JsonNode.DeepEquals(original["data"], JsonNode.Parse(result.Data!.Value.GetRawText())));
        Assert.Equal((string?)original["error"]?["code"], result.ErrorCode);
        if (result.Error != null)
            Assert.True(JsonNode.DeepEquals(original["error"]!["details"], JsonNode.Parse(result.ErrorDetails!.Value.GetRawText())));
        Assert.Equal(result.Envelope.Ok, result.IsCompleteSuccess);
        if (result.Meta.Outcome == Outcome.Partial || result.Meta.Outcome == Outcome.Unknown)
        {
            Assert.False(result.IsCompleteSuccess);
            Assert.Equal((string)original["meta"]!["outcome"]!, result.DisplayOutcome);
        }
    }

    [Fact]
    public void SupplementalCoverageIsTheEntireClosedErrorSet()
    {
        var covered = V4Fixtures.Supplemental().Select(e => EngineToolResult.Read(e.GetRawText()))
            .Where(r => r.Error != null).Select(r => r.Error.Code).OrderBy(c => c).ToArray();
        Assert.Equal(Enum.GetValues<ErrorCode>().OrderBy(c => c), covered);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void RealSnapshotEnvelopesPassThroughTheStudioReader(string release)
    {
        var assembly = typeof(EngineToolResultTests).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + release + ".json")))!;
        using var snapshot = JsonDocument.Parse(stream);
        int read = 0;
        foreach (var call in snapshot.RootElement.GetProperty("calls").EnumerateArray())
        {
            if (!call.TryGetProperty("response", out var response) || !response.TryGetProperty("result", out var wire)
                || !wire.TryGetProperty("structuredContent", out var envelope) || !envelope.TryGetProperty("schemaVersion", out _)) continue;
            var transport = JsonNode.Parse(wire.GetRawText())!;
            Hydrate(transport);
            // Snapshots store parsed text JSON. Restore its transport string without
            // changing business fields; only the documented clock/ID masks are filled.
            var text = transport["content"]![0]!["text"]!;
            transport["content"]![0]!["text"] = text.ToJsonString();
            var result = EngineToolResult.ReadMcpResult(transport.ToJsonString());
            var expected = transport["structuredContent"]!;
            Assert.Equal((string)expected["meta"]!["tool"]!, result.Meta.Tool);
            Assert.Equal((bool)expected["ok"]!, result.Envelope.Ok);
            Assert.True(JsonNode.DeepEquals(expected["data"], result.Data.HasValue ? JsonNode.Parse(result.Data.Value.GetRawText()) : null));
            Assert.Equal((string?)expected["error"]?["code"], result.ErrorCode);
            Assert.True(JsonNode.DeepEquals(expected["meta"]!["paging"], JsonNode.Parse(V4Json.Serialize(result.Paging))));
            Assert.True(JsonNode.DeepEquals(expected["meta"]!["warnings"], JsonNode.Parse(V4Json.Serialize(result.Warnings))));
            read++;
        }
        Assert.True(read > 10, "The test must consume real envelopes, not digest summaries.");
    }

    private static void Hydrate(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    if (text == "<string:timestamp>") obj[pair.Key] = "2026-10-03T00:00:00Z";
                    if (text == "<string:requestId>") obj[pair.Key] = "snapshot-request";
                }
                else if (pair.Value != null) Hydrate(pair.Value);
            }
        }
        else if (node is JsonArray array) foreach (var child in array) if (child != null) Hydrate(child);
    }

    [Fact]
    public void PagingAndWarningsDoNotTurnIncompleteDataIntoSuccess()
    {
        var source = JsonNode.Parse(V4Fixtures.Supplemental().First().GetRawText())!;
        source["meta"]!["completeness"] = "partial";
        source["meta"]!["warnings"] = JsonNode.Parse("""[{"code":"INCOMPLETE_DATA","message":"Missing fields.","details":{"field":"name"}}]""");
        source["meta"]!["paging"] = JsonNode.Parse("""{"mode":"offset","offset":0,"limit":10,"nextOffset":10,"cursor":null,"nextCursor":null,"total":20,"complete":false}""");
        var result = EngineToolResult.Read(source.ToJsonString());
        Assert.Equal("partial", result.DisplayOutcome);
        Assert.False(result.IsCompleteSuccess);
        Assert.Equal(10, result.Paging.NextOffset);
        Assert.Single(result.Warnings);
        source["meta"]!["completeness"] = "unknown";
        Assert.Equal("unknown", EngineToolResult.Read(source.ToJsonString()).DisplayOutcome);
    }

    [Fact]
    public void LegacyAndInconsistentTransportAreRejected()
    {
        Assert.ThrowsAny<Exception>(() => EngineToolResult.Read("{\"ok\":true,\"message\":\"success\"}"));
        var json = V4Fixtures.Supplemental().First().GetRawText();
        var wire = new JsonObject { ["structuredContent"] = JsonNode.Parse(json), ["isError"] = true };
        Assert.Throws<JsonException>(() => EngineToolResult.ReadMcpResult(wire.ToJsonString()));
        wire["isError"] = false;
        wire["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = V4Fixtures.Supplemental().Last().GetRawText() });
        Assert.Throws<JsonException>(() => EngineToolResult.ReadMcpResult(wire.ToJsonString()));
        var root = JsonNode.Parse(json)!;
        root["meta"]!["outcome"] = "partial";
        Assert.ThrowsAny<Exception>(() => EngineToolResult.Read(root.ToJsonString()));
    }
}
