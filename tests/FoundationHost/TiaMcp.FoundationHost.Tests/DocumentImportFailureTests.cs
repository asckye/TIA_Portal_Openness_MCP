using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class DocumentImportFailureTests
{
    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void Native_syntax_lines_reach_the_error(string release)
    {
        var arguments = new JsonObject {
            ["softwarePath"] = "PLC", ["groupPath"] = "", ["importPath"] = @"C:\Documents", ["fileNameWithoutExtension"] = "Example",
            ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = @"C:\Project\Example.ap21",
            ["expectedPlanHash"] = new string('b', 64)
        };
        var payload = DocumentImportTests.Payload(arguments);
        payload["Release"] = release;
        payload["ProjectFile"] = (string?)arguments["expectedProjectFile"];
        if (release == "20") payload["ResourceSha256"] = new string('c', 64);
        payload["PlanHash"] = DocumentImportContract.PlanHash(payload.AsObject());
        arguments["expectedPlanHash"] = (string?)payload["PlanHash"];
        payload["Status"] = "failed";
        payload["Error"] = "native-import-failed-target-absent";
        payload["Attempted"] = true;
        payload["Executed"] = true;
        payload["NativeState"] = "Failure";
        payload["Messages"] = new JsonArray("Importing from file 'x'...",
            "Line number 4: Syntax Error : Syntax error ':=' expecting ;",
            "Import failed with error 'Program block, file Content has some discrepancies'.");
        DocumentImportContract.Validate(payload, arguments);
        var definition = FoundationTools.Definitions.Single(tool => tool.Name == "ImportPlcBlockDocuments");
        var result = FoundationV4Result.Worker(release, definition, "syntax-error", arguments, payload, payload).StructuredContent!;
        Assert.Equal("failed", (string?)result["meta"]!["outcome"]);
        Assert.Contains("Line number 4", (string?)result["error"]!["details"]!["nativeMessage"]);
        Assert.Contains("Line number 4", (string?)result["error"]!["message"]);
        Assert.DoesNotContain("Importing from file", (string?)result["error"]!["details"]!["nativeMessage"]);
    }
}
