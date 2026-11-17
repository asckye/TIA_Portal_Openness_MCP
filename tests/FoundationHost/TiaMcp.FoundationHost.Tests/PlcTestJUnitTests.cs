using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Logic.Testing;
using Xunit;

public sealed class PlcTestJUnitTests
{
    [Theory]
    [InlineData(@"Load C:\private\runtime.dll succeeded; keep this reason", "Load runtime.dll succeeded; keep this reason")]
    [InlineData(@"Load C:/private/runtime.dll succeeded", "Load runtime.dll succeeded")]
    [InlineData(@"Load \\server\share\runtime.dll failed", "Load runtime.dll failed")]
    [InlineData("Load /private/lib/runtime.so failed", "Load runtime.so failed")]
    [InlineData("Load 'C:\\Program Files\\Siemens\\runtime.dll' failed", "Load 'runtime.dll' failed")]
    [InlineData(@"Load C:\Program Files\Siemens\runtime.dll failed while opening /tmp/other.so",
        "Load runtime.dll failed while opening other.so")]
    [InlineData("Load /private/lib/runtime.so failed while opening /tmp/other.so", "Load runtime.so failed while opening other.so")]
    public void Evidence_scrubbing_preserves_the_filename_and_text_after_the_path(string input, string expected)
        => Assert.Equal(expected, PlcTestEvidence.Scrub(input));

    [Fact]
    public void An_error_verdict_without_an_error_object_has_an_error_code_in_the_record_and_JUnit()
    {
        var evidence = PlcTestEvidence.Normalize(new JsonObject { ["data"] = new JsonObject() }, "error");
        Assert.Equal("TEST_ERROR", (string?)evidence["code"]);
        var run = new PlcTestRunRecord { Status = "completed" };
        run.Cases.Add(new PlcTestCaseResult { Id = "native", Verdict = "error", Evidence = evidence });
        Assert.Equal("TEST_ERROR", Assert.Single(XDocument.Parse(PlcTestJUnitWriter.Write(run)).Descendants("error")).Attribute("type")!.Value);
    }

    [Theory]
    [InlineData("\"System.InvalidOperationException: synthetic failure\"", "TEST_ERROR")]
    [InlineData("{\"code\":\"NATIVE_OPERATION_FAILED\",\"message\":\"synthetic failure\"}", "NATIVE_OPERATION_FAILED")]
    [InlineData("{\"code\":{\"malformed\":true}}", "TEST_ERROR")]
    public void Legacy_error_strings_and_objects_never_break_JUnit(string error, string type)
    {
        var run = new PlcTestRunRecord { Status = "completed" };
        run.Cases.Add(new PlcTestCaseResult { Id = "native", Kind = "testSuite", Verdict = "error", Message = "Synthetic failure",
            Evidence = new JsonObject { ["error"] = JsonNode.Parse(error), ["result"] = new JsonObject {
                ["state"] = new JsonArray(), ["messages"] = new JsonArray("bad", null, new JsonObject {
                    ["state"] = new JsonArray(), ["path"] = new JsonObject(), ["description"] = new JsonArray() }) } } });
        var xml = XDocument.Parse(PlcTestJUnitWriter.Write(run));
        Assert.Equal(type, Assert.Single(xml.Descendants("error")).Attribute("type")!.Value);
    }

    [Fact]
    public void Normalized_evidence_and_malformed_step_values_are_safe_to_render()
    {
        var run = new PlcTestRunRecord { Status = "completed" };
        run.Cases.Add(new PlcTestCaseResult { Id = "native", Verdict = "error", Message = "Synthetic failure",
            Evidence = new JsonObject { ["code"] = "TEST_ERROR", ["message"] = "Synthetic failure",
                ["details"] = new JsonObject { ["steps"] = new JsonArray("bad", null, new JsonObject {
                    ["ok"] = false, ["error"] = new JsonObject { ["message"] = "failure" },
                    ["checks"] = new JsonArray("bad", new JsonObject { ["ok"] = "bad" }) }) } } });
        Assert.Single(XDocument.Parse(PlcTestJUnitWriter.Write(run)).Descendants("error"));
    }
}
