using System.Text.Json.Nodes;
using ModelContextProtocol;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class SecondaryProjectAbsenceTests
{
    private const string File = @"C:\Reference\Line1.ap21";
    private static JsonObject Request(bool preview) => new() { ["dryRun"] = preview, ["secondaryProjectFile"] = File,
        ["expectedProjectFile"] = File };
    private static JsonObject Reply() => new() { ["Executed"] = false, ["ProjectFile"] = File, ["SecondaryProjectGone"] = true };

    [Theory, InlineData(false), InlineData(true)]
    public void Confirmed_absence_is_valid_for_preview_and_execution_and_never_claims_execution(bool preview)
    {
        var raw = Reply();
        WorkerProtocol.ValidateExchangeResult("CloseProject", Request(preview), raw);
        var envelope = V17ProjectEnvelope.Wrap("CloseProject", "ProjectMutation", raw, preview);
        string Wire(string name) => McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name) ?? name;
        var meta = envelope[Wire("Meta")]!;
        Assert.False((bool?)meta["executed"]);
        Assert.Equal(preview, (bool?)meta["dryRun"]);
        Assert.True((bool?)meta[Wire("SecondaryProjectGone")]);
        Assert.Contains("no operation executed", (string?)envelope[Wire("Message")]);
        var definition = FoundationTools.ForRelease(FoundationTools.Definitions.Single(d => d.Name == "CloseProject"), "21");
        var body = FoundationV4Result.Worker("21", definition, "absent", Request(preview), raw, envelope).StructuredContent!;
        Assert.True((bool?)body["ok"]);
        Assert.False((bool?)body["data"]?["executed"]);
        Assert.True((bool?)body["data"]?["secondaryProjectGone"]);
    }

    [Theory]
    [InlineData("executed")]
    [InlineData("missing-execution")]
    [InlineData("wrong-file")]
    [InlineData("missing-file")]
    [InlineData("relative-file")]
    [InlineData("wrong-expected")]
    [InlineData("missing-expected")]
    [InlineData("no-secondary")]
    [InlineData("string-marker")]
    [InlineData("null-marker")]
    [InlineData("false-marker")]
    [InlineData("missing-marker")]
    public void Invalid_absence_claims_keep_protocol_rejection(string fault)
    {
        var request = Request(false);
        var reply = Reply();
        switch (fault)
        {
            case "executed": reply["Executed"] = true; break;
            case "missing-execution": reply.Remove("Executed"); break;
            case "wrong-file": reply["ProjectFile"] = @"C:\Other\Line1.ap21"; break;
            case "missing-file": reply.Remove("ProjectFile"); break;
            case "relative-file": reply["ProjectFile"] = "Line1.ap21"; break;
            case "wrong-expected": request["expectedProjectFile"] = @"C:\Other\Line1.ap21"; break;
            case "missing-expected": request.Remove("expectedProjectFile"); break;
            case "no-secondary": request.Remove("secondaryProjectFile"); break;
            case "string-marker": reply["SecondaryProjectGone"] = "true"; break;
            case "null-marker": reply["SecondaryProjectGone"] = null; break;
            case "false-marker": reply["SecondaryProjectGone"] = false; break;
            case "missing-marker": reply.Remove("SecondaryProjectGone"); break;
        }
        Assert.Throws<IOException>(() => WorkerProtocol.ValidateExchangeResult("CloseProject", request, reply));
    }

    [Fact]
    public void Existing_mutation_protocol_checks_still_run_unchanged()
        => AdditionalMutationResultTests.Run((passed, message) => Assert.True(passed, message));

    [Theory, InlineData("OpenProject"), InlineData("SaveProject"), InlineData("CreateProject")]
    public void Absence_marker_does_not_allow_other_mutations_to_claim_non_execution(string operation)
        => Assert.Throws<IOException>(() => WorkerProtocol.ValidateExchangeResult(operation, Request(false), Reply()));
}
