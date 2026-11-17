using TiaMcp.FoundationHost;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class ReadablePathDescriptionTests
{
    private static readonly HashSet<string> Tools = new(StringComparer.Ordinal) {
        "DescribeHmiScreen", "ImportHmiScreen", "ImportHmiScreensFromDirectory", "ImportHmiTagTable", "ImportHmiTagTablesFromDirectory",
        "ApplyUnifiedHmiLayout", "ApplyUnifiedHmiScreenDesign", "ApplyUnifiedHmiTheme", "BindUnifiedHmiButtonPressedTag",
        "BindUnifiedHmiTagDynamization", "DescribeUnifiedHmiButtonEventScript", "EnsureUnifiedHmiButtonAction",
        "EnsureUnifiedHmiButtonEventHandler", "EnsureUnifiedHmiDynamization", "EnsureUnifiedHmiScreen", "EnsureUnifiedHmiScreenItem",
        "EnsureUnifiedHmiTag", "EnsureUnifiedHmiTagTable", "SetUnifiedHmiButtonEventScriptCode", "SetUnifiedHmiRuntimeState",
        "ManageUnifiedHmiGroup", "GetUnifiedScreenBranch", "DescribeObject", "DescribeService", "GetObjectProperty", "InvokeObject",
        "InvokeService", "ListObjectChildren", "SetUnifiedRuntimeSettings"
    };

    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Published_hmi_path_descriptions_show_readable_and_legacy_input_rules(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false).Where(tool => Tools.Contains(tool.ProtocolTool.Name)).ToArray();
        Assert.NotEmpty(tools);
        foreach (var tool in tools)
        {
            string text = tool.ProtocolTool.Description + " " + tool.ProtocolTool.InputSchema.GetRawText();
            Assert.DoesNotContain("URI-escaped", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("readable", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("old encoded input", text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(0, worker.Calls);
    }
}
