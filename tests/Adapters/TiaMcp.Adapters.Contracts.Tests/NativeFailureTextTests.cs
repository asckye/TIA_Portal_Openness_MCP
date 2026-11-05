using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class NativeFailureTextTests
{
    [Fact]
    public void Qualified_key_error_name_is_redacted_before_composition()
        => Assert.DoesNotContain("hunter2", NativeFailureText.Compose("PasswordStore.KeyNotFoundError: hunter2", null,
            Array.Empty<string>()), StringComparison.Ordinal);

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("Bearer hunter2")]
    public void Credential_values_are_masked_before_their_marker_line_is_cut(string credential)
    {
        string text = NativeFailureText.Compose("Reason hunter2\n   at Namespace.Type.Method()\n" + credential, null, Array.Empty<string>());
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("Reason", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Private_key_is_masked_before_stack_lines_are_cut()
    {
        string text = NativeFailureText.Compose("Failure", "Reason -----BEGIN PRIVATE KEY-----\nkey-body-secret\n-----END PRIVATE KEY-----\n"
            + "   at Private.Native.Invoke()", Array.Empty<string>());
        Assert.DoesNotContain("key-body-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", text, StringComparison.Ordinal);
        Assert.Contains("Reason", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reason_starting_at_least_is_not_a_stack_frame()
        => Assert.Equal("Failure; at least one connection must exist", NativeFailureText.Compose("Failure",
            "at least one connection must exist\n   at Namespace.Type.Method()", Array.Empty<string>()));

    [Fact]
    public void Native_failure_text_drops_stack_frames_but_keeps_the_reason()
        => Assert.Equal("Call failed; Native reason", NativeFailureText.Compose("Call failed",
            "Native reason\n   at Private.Native.Invoke()\n   at Private.Host.Call()", Array.Empty<string>()));

    [Fact]
    public void Blank_message_data_keeps_the_base_exception_message()
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine("21");
        engine.ConfigureFixture();
        var error = new Siemens.Engineering.EngineeringException("Native base reason");
        error.MessageData.Text = "";
        error.MessageData.DetailText = "";
        var reply = engine.RunHmiStep("ImportLibraryTypeDocuments", _ => throw error, false);
        Assert.Equal("Native base reason", reply.Meta["nativeMessage"]);
    }

    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Native_step_keeps_the_engineering_detail(string release)
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine(release);
        engine.ConfigureFixture();
        var error = new Siemens.Engineering.EngineeringException("Error when calling method Import");
        error.MessageData.Text = error.Message;
        error.MessageData.DetailText = "Action is not supported within ExclusiveAccess.";
        var reply = engine.RunHmiStep("ImportLibraryTypeDocuments", _ => throw error, false);
        Assert.Equal("Error when calling method Import; Action is not supported within ExclusiveAccess.", reply.Meta["nativeMessage"]);
    }

    [Fact]
    public void Compose_retains_distinct_reasons_in_one_line()
        => Assert.Equal("Error when calling method; ExclusiveAccess is not supported; Inner reason",
            NativeFailureText.Compose("Error when calling method", "ExclusiveAccess is not supported",
                new[] { "", "Error when calling method", "Inner\r\nreason", null, "ExclusiveAccess is not supported" }));

    [Fact]
    public void Compose_omits_empty_texts()
        => Assert.Equal("", NativeFailureText.Compose(null, " ", new string?[] { null, "" }));
}
