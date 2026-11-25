using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class BehaviorDisclosureTests
{
    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"),
        InlineData("20"), InlineData("21")]
    public void Ported_read_discloses_the_same_unaccepted_behavior_on_every_release(string release)
    {
        using var selected = HardwareContract.UseRelease(release);
        var service = new AddressService();
        var result = new AddressesTools(service).GetDeviceAddressingV4(new[] { "PLC_1" });
        var body = result.StructuredContent!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.Equal("current", (string?)body["meta"]?["behaviorPolicy"]);
        Assert.Equal("read-only", (string?)body["meta"]?["execution"]);
        Assert.Equal(release, (string?)body["meta"]?["releaseKey"]);
        Assert.Equal(BehaviorCapabilities.CurrentDisclosure,
            (string?)Assert.Single(body["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "UNVERIFIED_BEHAVIOR")?["message"]);
        Assert.Equal(1, service.Calls);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"),
        InlineData("20"), InlineData("21")]
    public void Every_unaccepted_entry_has_one_policy_warning_and_description_suffix(string release)
    {
        var catalog = new FoundationCatalog(release, Array.Empty<global::ModelContextProtocol.Server.McpServerTool>());
        foreach (var family in catalog.BehaviorCapabilities.OfType<JsonObject>())
        foreach (string entry in family["entries"]!.AsArray().Select(row => (string)row!))
        {
            var policy = BehaviorCapabilities.EntryPolicy(typeof(BehaviorDisclosureTests).Assembly, release, entry, BehaviorPolicy.NotApplicable);
            var result = McpResult.From(BehaviorCapabilities.Disclose(Result(release, entry)));
            var envelope = V4Json.Deserialize<Envelope>(result.Content.Single().Text);
            Assert.Equal(policy, envelope.Meta.BehaviorPolicy);
            Assert.Equal(Execution.ReadOnly, envelope.Meta.Execution);
            Assert.Equal("read-id", envelope.Meta.RequestId);
            Assert.Equal(result.Content.Single().Text, result.StructuredContent.GetRawText());
            if (policy == BehaviorPolicy.Current)
            {
                Assert.Equal(BehaviorCapabilities.CurrentDisclosure,
                    Assert.Single(envelope.Meta.Warnings, warning => warning.Code == WarningCode.UnverifiedBehavior).Message);
                if (catalog.Find(entry) is { } descriptor)
                {
                    Assert.EndsWith(BehaviorCapabilities.CurrentDisclosure, descriptor.Tool.Description, StringComparison.Ordinal);
                    Assert.Equal(descriptor.Tool.Description,
                        BehaviorCapabilities.Describe(release, entry, descriptor.Tool.Description));
                }
            }
            else Assert.DoesNotContain(envelope.Meta.Warnings, warning => warning.Code == WarningCode.UnverifiedBehavior);
        }
        Assert.Contains(catalog.BehaviorCapabilities, row => (string?)row?["family"] == "F19");
        Assert.Contains(catalog.BehaviorCapabilities, row => (string?)row?["family"] == "P6-FALLBACK"
            && row["entries"]!.AsArray().Count > 0);
    }

    [Fact]
    public void Disclosure_normalizes_legacy_wording_once_and_preserves_other_evidence()
    {
        var details = new Dictionary<string, JsonElement> { ["evidence"] = JsonSerializer.SerializeToElement("retained") };
        var envelope = Result("21", "GetDeviceAddressing", BehaviorPolicy.Current,
            new Warning(WarningCode.UnverifiedBehavior, "Previous native warning text.", details),
            new Warning(WarningCode.IncompleteData, "The observation is partial.", details));
        var disclosed = BehaviorCapabilities.Disclose(envelope);
        Assert.Equal(2, disclosed.Meta.Warnings.Count);
        Assert.Equal("Previous native warning text.", disclosed.Meta.Warnings[0].Message);
        Assert.Equal("retained", disclosed.Meta.Warnings[0].Details["evidence"].GetString());
        Assert.Same(envelope.Meta.Warnings[1], disclosed.Meta.Warnings[1]);
        Assert.Equal(envelope.Data?.GetRawText(), disclosed.Data?.GetRawText());
        Assert.Same(disclosed, BehaviorCapabilities.Disclose(disclosed));
    }

    [Fact]
    public void Batch_recovery_review_warning_survives_disclosure_and_duplicate_merging()
    {
        var batch = PlcBatchImportResultMapping.Result(new JsonObject { ["items"] = new JsonArray() },
            "21", "ImportPlcBlocksFromDirectory", "batch-review", preview: true);
        var original = Assert.Single(batch.Meta.Warnings, warning => warning.Code == WarningCode.UnverifiedBehavior);
        Assert.Equal("Native acceptance is pending; recovery exports and restoration evidence must be reviewed.", original.Message);
        Assert.Same(batch, BehaviorCapabilities.Disclose(batch));
        var meta = batch.Meta;
        var duplicate = new Envelope(4, batch.Ok, batch.Data, batch.Error, new Meta(meta.Timestamp, meta.ReleaseKey, meta.Tool, meta.RequestId,
            meta.Outcome, meta.Execution, meta.BehaviorPolicy, meta.Completeness, meta.Paging, new[] {
                new Warning(WarningCode.UnverifiedBehavior, BehaviorCapabilities.CurrentDisclosure,
                    new Dictionary<string, JsonElement> { ["genericEvidence"] = JsonSerializer.SerializeToElement("kept") }),
                original, new Warning(original.Code, original.Message,
                    new Dictionary<string, JsonElement> { ["recoveryEvidence"] = JsonSerializer.SerializeToElement("review") }) }));
        var disclosed = BehaviorCapabilities.Disclose(duplicate);
        var merged = Assert.Single(disclosed.Meta.Warnings, warning => warning.Code == WarningCode.UnverifiedBehavior);
        Assert.Equal(original.Message, merged.Message);
        Assert.Equal("kept", merged.Details["genericEvidence"].GetString());
        Assert.Equal("review", merged.Details["recoveryEvidence"].GetString());
        Assert.Same(disclosed, BehaviorCapabilities.Disclose(disclosed));
    }

    [Fact]
    public void Tools_outside_the_ledger_keep_their_policy_and_description()
    {
        var unrelated = Result("21", "UnrelatedFixture");
        Assert.Same(unrelated, BehaviorCapabilities.Disclose(unrelated));
        Assert.Equal("Offline fixture.", BehaviorCapabilities.Describe("21", "UnrelatedFixture", "Offline fixture."));
        var accepted = Result("21", "AcceptedFixture", BehaviorPolicy.SafeV4);
        Assert.Same(accepted, BehaviorCapabilities.Disclose(accepted));
    }

    private static Envelope Result(string release, string entry, BehaviorPolicy policy = BehaviorPolicy.NotApplicable, params Warning[] warnings)
        => Envelope.Create(new { evidence = "retained" }, null,
            new Meta(DateTimeOffset.UtcNow, release, entry, "read-id", Outcome.Succeeded, Execution.ReadOnly,
                policy, Completeness.Complete, null, warnings));

    private sealed class AddressService : IHardwareAddressingService
    {
        internal int Calls;
        public bool HasProject => true;
        public ResponseMessage ReadDeviceAddressing(string devicePathJson, string itemPathJson, int offset, int limit)
        {
            Calls++;
            return new ResponseMessage { Message = "Addressing observed.", Meta = new JsonObject {
                ["success"] = true, ["dataComplete"] = true, ["items"] = new JsonArray() } };
        }
        public IReadOnlyList<IoAddressInfo>? GetDeviceItemAddresses(string path) => throw new InvalidOperationException();
        public List<string> DescribeChildItemsWithAddresses(string path) => throw new InvalidOperationException();
        public IoAddressWriteReply SetDeviceItemStartAddress(string path, string ioType, int startAddress) => throw new InvalidOperationException();
        public ResponseMessage UpdateDeviceAddress(string devicePathJson, string itemPathJson, string ioType, int startAddress,
            string propertiesJson, string attributesJson, string softwarePath, string processImageObName, bool dryRun)
            => throw new InvalidOperationException();
        public JsonObject GetDeviceIpAddress(string path) => throw new InvalidOperationException();
    }
}
