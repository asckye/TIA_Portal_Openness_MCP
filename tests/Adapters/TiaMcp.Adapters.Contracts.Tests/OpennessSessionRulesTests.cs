using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

// P8-81c (API audit 2026-10-19, F2/F4/F17): explicit refusal codes, UMAC wording and the confirmation-refusal policy.
public sealed class OpennessSessionRulesTests
{
    private const string Secret = "Umac-Secret-7f3!";

    public static IEnumerable<object[]> Codes => new[] { OpennessRefusals.AccessDenied, OpennessRefusals.MissingProducts,
        OpennessRefusals.LicenseNotFound, OpennessRefusals.MultiuserRefused, OpennessRefusals.UmacCredentialsRequired }.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_documented_refusal_is_an_explicit_non_poisoning_precondition(string code)
    {
        var native = new InvalidOperationException("native text\r\nsecond line");
        var refusal = OpennessRefusals.Exception(code, "attach", false, native);
        Assert.Equal(code, refusal.Condition);
        Assert.False(refusal.IsArgument);
        Assert.Null(refusal.ParamName);
        Assert.Same(native, refusal.InnerException);
        Assert.Contains("nothing was changed and this session stays usable", refusal.Message, StringComparison.Ordinal);
        Assert.EndsWith("TIA: native text second line", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("may hold files written before the refusal", OpennessRefusals.Message(code, "retrieve", false, null), StringComparison.Ordinal);
    }

    [Fact]
    public void Access_denied_names_the_firewall_steps_and_for_open_the_missing_umac_credentials()
    {
        string attach = OpennessRefusals.Message(OpennessRefusals.AccessDenied, "attach", false, null);
        Assert.Contains("HKLM\\SOFTWARE\\Siemens\\Automation\\Openness\\AllowList", attach, StringComparison.Ordinal);
        Assert.Contains("Yes to all", attach, StringComparison.Ordinal);
        Assert.DoesNotContain("umacUserName", attach, StringComparison.Ordinal);
        string open = OpennessRefusals.Message(OpennessRefusals.AccessDenied, "open", false, null);
        Assert.Contains("pass umacUserName and umacPassword", open, StringComparison.Ordinal);
        Assert.Contains("never logged or echoed", open, StringComparison.Ordinal);
        Assert.Contains("Check umacUserName, umacPassword and umacUserType", OpennessRefusals.Message(OpennessRefusals.AccessDenied, "retrieve", true, null), StringComparison.Ordinal);
        Assert.Contains("missing", OpennessRefusals.Message(OpennessRefusals.ProjectNotVisible, "bind", false, null), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => OpennessRefusals.Message("guessed-code", "attach", false, null));
    }

    [Fact]
    public void Native_text_is_bounded()
    {
        var message = OpennessRefusals.Message(OpennessRefusals.MissingProducts, "open", false, new string('x', 5000));
        Assert.EndsWith(new string('x', 600) + " ...", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Umac_validation_never_echoes_the_password()
    {
        EngineeringCredentialRules.ValidateUmacCredentials("", "", "");
        EngineeringCredentialRules.ValidateUmacCredentials("engineer", Secret, "Global");
        foreach (var refused in new Action[] {
            () => EngineeringCredentialRules.ValidateUmacCredentials("", Secret, ""),
            () => EngineeringCredentialRules.ValidateUmacCredentials("engineer", Secret, "Local"),
            () => EngineeringCredentialRules.ValidateUmacCredentials("", "", "Project") })
            Assert.DoesNotContain(Secret, Assert.ThrowsAny<ArgumentException>(refused).Message, StringComparison.Ordinal);
        foreach (string code in Codes.Select(c => (string)c[0]))
            Assert.DoesNotContain(Secret, OpennessRefusals.Message(code, "open", true, "TIA refused"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "Yes", "No", "Cancel" }, "Cancel")]
    [InlineData(new[] { "Ok", "Cancel" }, "Cancel")]
    [InlineData(new[] { "Abort", "Retry", "Ignore" }, "Abort")]
    [InlineData(new[] { "Yes", "No" }, "No")]
    [InlineData(new[] { "YesToAll", "NoToAll" }, "NoToAll")]
    [InlineData(new[] { "Ok" }, null)]
    [InlineData(new[] { "Yes", "YesToAll" }, null)]
    public void Confirmation_handler_refuses_by_default_and_never_accepts(string[] offered, string? expected)
    {
        Assert.Equal(expected, PortalDialogPolicy.RefusingChoice(offered));
        Assert.DoesNotContain(PortalDialogPolicy.RefusingChoice(offered), new[] { "Yes", "YesToAll", "Ok", "Retry", "Ignore" });
    }

    [Fact]
    public void Dialog_records_are_bounded_and_copied()
    {
        var records = new List<Dictionary<string, object?>>();
        for (int index = 0; index < PortalDialogPolicy.Capacity + 5; index++)
            PortalDialogPolicy.Record(records, new Dictionary<string, object?> { ["text"] = "dialog " + index });
        var snapshot = PortalDialogPolicy.Snapshot(records);
        Assert.Equal(PortalDialogPolicy.Capacity, snapshot.Length);
        Assert.Equal("dialog 5", snapshot[0]["text"]);
        snapshot[0]["text"] = "changed";
        Assert.Equal("dialog 5", records[0]["text"]);
    }
}
