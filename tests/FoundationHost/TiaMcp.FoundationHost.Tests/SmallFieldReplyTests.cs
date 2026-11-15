extern alias enginehost;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcWorker;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Services = enginehost::TiaMcpServer.Siemens.Services;

public sealed class SmallFieldReplyTests
{
    private static readonly string[] Releases = ["14sp1", "15.1", "16", "17", "18", "19", "20", "21"];

    public static IEnumerable<object[]> DeletionReports => from release in Releases
        from tool in new[] { "DeletePlcTagTable", "DeletePlcBlock", "DeletePlcType" }
        from scenario in new[] { "preview", "deleted", "unverified" }
        select new object[] { release, tool, scenario };

    [Theory, MemberData(nameof(DeletionReports))]
    public void Deletion_reports_use_plain_warnings_and_space_before_tag_counts(string release, string tool, string scenario)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        bool preview = scenario == "preview";
        var service = new Services.PlcOrganisationPortService((operation, arguments) => {
            Assert.Equal("plc-organisation.ExecutePlcOrganisation", operation);
            Assert.Equal(preview, (bool)arguments["dryRun"]!);
            return JsonNode.Parse(WorkerJson.Serialize(new PlcSoftwareReply { Data = new() {
                ["resolvedTagTablePath"] = "Group/Table", ["tagCount"] = 2,
                ["deleted"] = !preview, ["verifiedAbsent"] = scenario == "deleted",
                ["crossReferenceQueried"] = true, ["crossReferenceAvailable"] = false } }));
        }, () => true, () => "C:/Project/Example.ap21");
        var tools = new Host.PlcOrganisationTools(service);
        var report = tool switch {
            "DeletePlcTagTable" => tools.DeletePlcTagTable("PLC_A", "Group/Table", preview),
            "DeletePlcBlock" => tools.DeletePlcBlock("PLC_A", "Block", preview),
            _ => tools.DeletePlcType("PLC_A", "Type", preview)
        };

        Assert.Equal(scenario != "unverified", report.Ok);
        if (tool == "DeletePlcTagTable") Assert.Contains("Tag table 'Group/Table' (2 tag(s))", report.Message);
        if (preview) Assert.Contains("Warning: Cross-references could not be retrieved.", report.Message);
        if (scenario == "unverified") Assert.StartsWith("Warning: UNVERIFIED:", report.Message);
        if (!preview && tool == "DeletePlcTagTable")
            Assert.Contains("Warning: Compilation cannot detect HMI symbol-binding failures; verify screen tags separately",
                report.Meta!["nextActions"]!.AsArray().Select(action => (string?)action));
        Assert.DoesNotContain("\u26a0", report.Message);
        Assert.DoesNotContain("\u26a0", report.Meta!.ToJsonString());
    }

    public static IEnumerable<object[]> FamilyConditions()
    {
        var families = new Dictionary<string, string> {
            ["ExportPlcProDiagInfo"] = "prodiag",
            ["ManagePlcSupervision"] = "prodiag",
            ["GetClassicHmiScreenTree"] = "classic-hmi", ["ManageClassicHmiScreenObject"] = "classic-hmi",
            ["ManageClassicHmiFolder"] = "classic-hmi", ["ManageClassicHmiGraphic"] = "classic-hmi",
            ["ListClassicHmiScripts"] = "classic-hmi", ["ManageClassicHmiScript"] = "classic-hmi",
            ["ManageClassicHmiCycle"] = "classic-hmi", ["GetClassicHmiGlobalization"] = "classic-hmi",
            ["ManageClassicHmiTextGraphicList"] = "classic-hmi",
            ["ListClassicHmiFaceplates"] = "classic-hmi", ["GetMotionAxisConfiguration"] = "motion",
            ["ManageMotionAxis"] = "motion", ["ExchangeMotionCamData"] = "motion", ["ConfigureMotionHardwareConnection"] = "motion"
        };
        foreach (string release in Releases)
        foreach (var family in families)
        {
            if (family.Key == "ExportPlcProDiagInfo" && release is "14sp1" or "15.1" or "16") continue;
            if (family.Key == "ManagePlcSupervision" && release is "14sp1" or "15.1" or "16") continue;
            if (family.Key == "ExchangeMotionCamData" && release == "14sp1") continue;
            foreach (bool beforeWrite in new[] { false, true })
                yield return new object[] { release, family.Key, family.Value, beforeWrite };
        }
    }

    [Theory, MemberData(nameof(FamilyConditions))]
    public void Precondition_identifies_the_tools_family(string release, string tool, string family, bool beforeWrite)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var response = new Host.ResponseMessage { Message = "Synthetic precondition refusal.", Meta = new JsonObject {
            ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false } };
        if (!beforeWrite) response.Meta["v4FailureCode"] = "PRECONDITION_FAILED";
        var body = Host.ClassicMotionToolContract.Map(tool, response, false, true).StructuredContent!;

        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]!["code"]);
        Assert.Equal(family + (beforeWrite ? "-before-write" : ""), (string?)body["error"]!["details"]!["condition"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]!["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
    }
}
