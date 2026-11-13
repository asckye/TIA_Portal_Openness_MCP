using System.Text;
using System.Text.Json.Nodes;
using System.Reflection;
using Siemens.Engineering;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class PortalDialogTests
{
    public static IEnumerable<object[]> NativeConfirmations()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
            foreach (var row in new[] {
                (ConfirmationChoices.Ok, ConfirmationResult.Ok, true),
                (ConfirmationChoices.Yes | ConfirmationChoices.No, ConfirmationResult.No, true),
                (ConfirmationChoices.Yes, ConfirmationResult.None, false) })
                yield return new object[] { release, row.Item1, row.Item2, row.Item3 };
    }

    [Theory, MemberData(nameof(NativeConfirmations))]
    public void OnPortalConfirmation_answers_on_the_callback_thread_without_Openness_calls(
        string release, ConfirmationChoices choices, ConfirmationResult answer, bool handled)
    {
        var engine = new PlcFoundationEngine(release);
        engine.ClearCalls();
        engine.Connection.EngineeringCall = () => throw new InvalidOperationException("Confirmation called Openness.");
        var args = new ConfirmationEventArgs { Choices = choices, Caption = "Confirm", Text = "Choose", DetailText = "Detail" };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var handler = typeof(PlcFoundationEngine).GetMethod("OnPortalConfirmation", flags);
        Assert.NotNull(handler);
        handler.Invoke(engine, new object[] { engine.Connection, args });
        Assert.Equal(answer, args.Result);
        Assert.Equal(handled, args.IsHandled);
        Assert.Empty(engine.NativeCalls);
        var records = (List<Dictionary<string, object?>>)typeof(PlcFoundationEngine).GetField("portalDialogs", flags)!.GetValue(engine)!;
        var record = Assert.Single(PortalDialogPolicy.Snapshot(records));
        Assert.Equal(handled ? answer.ToString() : "unanswered", record["answer"]);
        Assert.Equal("Choose", record["text"]);
    }

    [Theory]
    [InlineData(new[] { "Ok" }, "Ok")]
    [InlineData(new[] { "Yes" }, null)]
    [InlineData(new[] { "Yes", "No" }, "No")]
    [InlineData(new[] { "Ok", "Cancel" }, "Cancel")]
    [InlineData(new[] { "Yes", "YesToAll" }, null)]
    [InlineData(new[] { "Yes", "Ok" }, null)]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "Abort", "No" }, "Abort")]
    [InlineData(new[] { "YesToAll", "NoToAll" }, "NoToAll")]
    public void Confirmation_applies_the_approved_answer(string[] choices, string? answer)
    {
        Assert.Equal(answer, PortalDialogPolicy.Answer(choices));
        Assert.DoesNotContain(PortalDialogPolicy.Answer(choices), new[] { "Yes", "YesToAll" });
    }

    [Fact]
    public void Notification_is_marked_handled()
    {
        var records = new List<Dictionary<string, object?>>();
        var args = new NotificationEventArgs { Caption = "Notice", Text = "Exported", DetailText = "Detail" };
        PortalDialogHandlers.Notification(args, records);
        Assert.True(args.IsHandled);
        var record = Assert.Single(records);
        Assert.Equal("Notification", record["event"]);
        Assert.Equal("Exported", record["text"]);
        Assert.Equal("handled", record["answer"]);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("中文\r\n\"\\\u0001")]
    [InlineData("😀")]
    public void Dialog_report_is_one_valid_json_line_under_two_KiB(string fragment)
    {
        string large = string.Concat(Enumerable.Repeat(fragment, 2048));
        var record = new Dictionary<string, object?> { ["event"] = "Confirmation", ["utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["caption"] = large, ["text"] = large, ["detailText"] = large, ["answer"] = "unanswered", ["rule"] = large,
            ["choices"] = new[] { "Ok", "Cancel", "Abort", "No", "NoToAll", "Yes", "YesToAll", "Retry", "Ignore" } };
        string line = PortalDialogPolicy.Report(record);
        Assert.StartsWith("TIA_MCP_DIALOG ", line);
        Assert.True(Encoding.UTF8.GetByteCount(line) <= 2048);
        Assert.DoesNotContain('\n', line);
        var body = JsonNode.Parse(line["TIA_MCP_DIALOG ".Length..])!;
        Assert.Equal("unanswered", (string?)body["answer"]);
        Assert.Equal("Confirmation", (string?)body["event"]);
        Assert.Equal(9, body["choices"]!.AsArray().Count);
        Assert.NotEmpty((string)body["text"]!);
    }
}
