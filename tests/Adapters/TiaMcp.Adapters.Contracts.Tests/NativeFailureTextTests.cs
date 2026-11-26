using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class NativeFailureTextTests
{
    [Fact]
    public void Missing_optional_assembly_reports_installation_and_licence_without_target_refusal()
    {
        var error = Assert.Throws<NotSupportedException>(() => Hardware.HardwareOfficialService.Require(
            new object(), "Fixture.MissingService", "Siemens.Engineering.MissingOption"));
        Assert.Equal("NotSupportedOnVersion", StepFailureStatus.Of(error));
        Assert.Equal("installation", error.Data["unsupportedBy"]);
        var engine = new PlcFoundationEngine("17");
        engine.ConfigureFixture();
        var reply = engine.RunHmiStep("GetSivarcRuleTree", _ => throw error, requiresProject: false);
        Assert.Equal("installation", reply.Meta["unsupportedBy"]);
        var refusal = TiaMcp.Logic.V4.UnsupportedCapabilityDetails.ErrorFor("17", "GetSivarcRuleTree", null,
            error.Message, (string?)reply.Meta["unsupportedBy"]);
        Assert.Contains("installation", refusal.Message);
        Assert.DoesNotContain("unavailable on TIA", refusal.Message);
        Assert.Contains("licence", error.Message);
    }

    [Fact]
    public void Certificate_template_keeps_each_requested_ip_only_once()
    {
        var names = new[] { "192.0.2.1", "2001:db8::1", "192.0.2.2", "192.0.2.2" }
            .Select(ip => new CertificateSubjectName { Type = "IP", Value = ip });
        var missing = TiaMcp.Adapters.Native.Security.CertificateNames.Missing(new[] { ("IP", "192.0.2.1"), ("IP", "2001:0db8::1") }, names);
        Assert.Equal("192.0.2.2", Assert.Single(missing).Value);
    }

    [Fact]
    public void The_log_excerpt_byte_limit_preserves_complete_UTF8_characters()
    {
        string deviceName = new string('\u8f74', 2000);
        var excerpt = NativeFailureText.BoundedLines(new[] { "X", deviceName, "past the byte budget" }, text => text);
        Assert.Equal(new[] { "X", deviceName.Substring(0, 1364) }, excerpt);
        Assert.Equal(4094, System.Text.Encoding.UTF8.GetByteCount(string.Join("\n", excerpt)));
    }

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
    public void A_single_digit_credential_preserves_digits_inside_compiler_locations()
        => Assert.Equal("Line 000011: MonKey=••••; operand DB1_1", NativeFailureText.Compose(
            "Line 000011: MonKey=1; operand DB1_1", null, Array.Empty<string>()));

    [Theory]
    [InlineData("x")]
    [InlineData("🔑")]
    public void Single_character_secrets_are_masked_at_each_word_boundary(string secret)
    {
        string message = $"{secret}; password='{secret}'; ({secret}), Bearer {secret}; ID_{secret}2; {secret}";
        Assert.Equal($"••••; password='••••'; (••••), Bearer ••••; ID_{secret}2; ••••",
            NativeFailureText.Compose(message, null, Array.Empty<string>()));
    }

    [Fact]
    public void A_repeated_password_is_masked_literally_before_and_after_its_assignment()
        => Assert.Equal("Refused ••••; password='••••'; retry (••••); code V3x17", NativeFailureText.Compose(
            "Refused V3.17; password='V3.17'; retry (V3.17); code V3x17", null, Array.Empty<string>()));

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
    public void Standalone_login_loss_reaches_the_native_step_connection_handler()
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine("19");
        engine.ConfigureFixture();
        var reply = engine.RunHmiStep("ManagePlcSafety", meta => {
            meta["mayHaveChanged"] = true;
            SafetyLoginFailure.Attempt(() => throw new ObjectDisposedException("safety connection"),
                () => throw new InvalidOperationException("Unexpected readback"), () => throw new InvalidOperationException("Unexpected logoff"));
            return "unexpected login";
        }, false);
        Assert.Equal(true, reply.Meta["connectionUnavailable"]);
        Assert.True(reply.OutcomeUnknown);
    }

    [Fact]
    public void Certificate_failure_keeps_the_outer_reason_inner_reason_and_engineering_details()
    {
        var engine = new TiaMcp.Adapters.PlcFoundationEngine("20");
        engine.ConfigureFixture();
        var native = new Siemens.Engineering.EngineeringException("Store refused the request", new Exception("Firmware diagnostic"));
        native.MessageData.Text = native.Message;
        native.MessageData.DetailText = "Template unavailable";
        native.DetailMessageData = new[] { new Siemens.Engineering.ExceptionMessageData { Text = "WebServer", DetailText = "Unsupported usage" } };
        var error = new InvalidOperationException("Certificate template lookup", native);
        var reply = engine.RunHmiStep("ManagePlcCertificate", meta => { meta["phase"] = "get-certificate-template"; throw error; }, false);
        Assert.Equal("Certificate template lookup; Store refused the request; Template unavailable; WebServer; Unsupported usage; Firmware diagnostic",
            reply.Meta["nativeMessage"]);
        var sensitiveInner = new Exception("Firmware diagnostic; password=inner-secret");
        var wrapped = new InvalidOperationException("Certificate template lookup", sensitiveInner);
        var redacted = engine.RunHmiStep("ManagePlcCertificate", meta => { meta["phase"] = "get-certificate-template"; throw wrapped; }, false);
        var message = Assert.IsType<string>(redacted.Meta["nativeMessage"]);
        Assert.Contains("Firmware diagnostic", message);
        Assert.DoesNotContain("inner-secret", message);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Certificate_template_failure_probes_device_facts_only_when_TIA_supplies_no_detail(bool hasDetail)
    {
        var engine = new PlcFoundationEngine("17");
        engine.ConfigureFixture();
        var error = new Siemens.Engineering.EngineeringException("Error when calling GetCertificateTemplate");
        error.MessageData.Text = error.Message;
        error.MessageData.DetailText = hasDetail ? "Unsupported usage" : "";
        int probes = 0;
        var reply = engine.RunHmiStep("ManagePlcCertificate", meta => {
            meta["phase"] = "get-certificate-template";
            return PlcFoundationEngine.ReadCertificateTemplate<string>(meta, () => throw error,
                () => { probes++; return "V2.9"; }, () => { probes++; return false; }, () => { probes++; return 3; });
        }, false);
        Assert.Equal(hasDetail ? 0 : 3, probes);
        if (hasDetail) Assert.Equal(error.Message + "; Unsupported usage", reply.Meta["nativeMessage"]);
        else
        {
            Assert.Equal("V2.9", reply.Meta["firmwareVersion"]);
            Assert.Equal(false, reply.Meta["enableGlobalCertificatesStore"]);
            Assert.Equal(3, reply.Meta["certificateCount"]);
            Assert.Contains("TIA returned no further reason", (string?)reply.Meta["nativeMessage"]);
            Assert.Contains("S7-1500 FW V2.9", (string?)reply.Meta["nativeMessage"]);
            Assert.Contains("certificate manager", (string?)reply.Meta["nativeMessage"]);
            Assert.Contains("TIA V17 and later", (string?)reply.Meta["nativeMessage"]);
        }
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
