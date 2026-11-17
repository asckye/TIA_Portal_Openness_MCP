using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class UnifiedRuntimeSettingsFieldsTests
{
    [Fact]
    public void Root_names_and_call_limit_remain_unchanged()
    {
        Assert.Equal(new[] { "StartScreen", "ScreenResolution", "CentralInputHint", "CentralPanning", "CentralZooming",
            "BitSelection", "BitSelectionStrategyForResourceLists", "BitSelectionStrategyForTagDynamization",
            "EnableLanguageCompatibleFontFamilies", "AutoLogOffURL", "GMPEnabled", "GeneralESIGCommentsStrategy" },
            UnifiedRuntimeSettingsFields.Root);
        Assert.Equal(12, UnifiedRuntimeSettingsFields.MaxFieldsPerCall);
    }

    public static IEnumerable<object[]> NestedFields()
    {
        var groups = new (string Root, string Leaves, int Since)[] {
            ("OpcUaServerRuntimeSettings", "ActAsOPCServer AllowAlarmOperations EnableAlarmConditions " +
                "EnableGuestAuthentication EnableLocalDiscoveryServer EnableUsernameAndPasswordAuthentication " +
                "OpcUaServerSecurity128RsaModeSigned OpcUaServerSecurity128RsaModeSignedAndEncrypted " +
                "OpcUaServerSecurity256ModeSigned OpcUaServerSecurity256ModeSignedAndEncrypted " +
                "OpcUaServerSecurity256ShaModeSigned OpcUaServerSecurity256ShaModeSignedAndEncrypted " +
                "OpcUaServerSecurityAes128Sha256RsaOaepSigned OpcUaServerSecurityAes128Sha256RsaOaepSignedAndEncrypted " +
                "OpcUaServerSecurityAes256Sha256RsaPssSigned OpcUaServerSecurityAes256Sha256RsaPssSignedAndEncrypted " +
                "OpcUaServerSecurityNone EndpointUrlPortNumber MaxMonitoredItemPerSubscriptionCount " +
                "MaxSessionCount MaxSessionTimeout MinPublishingInterval", 19),
            ("MaxLoginRuntimeSettings", "EnableLockAfterNumberOfAttempts MaxLoginErrors", 19),
            ("ProcessDiagnosticsRuntimeSettings", "EnableProcessDiagnostics CriteriaAnalysisAbsoluteAddress " +
                "CriteriaAnalysisComment CriteriaAnalysisSymbol CriteriaAnalysisValue CriteriaAnalysisAll CriteriaAnalysisExtendText", 19),
            ("HmiReportingSettings", "IsReportingEnabled ReportingDatabaseStorage ReportingMainStorage " +
                "ReportingDatabaseStoragePath ReportingMainStoragePath", 19),
            ("RuntimeResourceSettings", "EnableHighResolutionGraphicsOptimization", 19),
            ("TelemetryRuntimeSettings", "TelemetryActive TelemetryStorageFolder TelemetryStorageMedia", 20),
            ("HmiUpssRuntimeSettings", "GlobalScopePersistencyAuthorization PersistencyStrategy", 20),
            ("HmiUnifiedTagSettings", "IgnoreInitialQCNotifications IgnoreTimestampNotifications", 20),
            ("HmiUnifiedTagSettings", "TagOptimizationActive", 21),
            ("HmiExclusiveOperationSettings", "ExclusiveOperationControlTag ExclusiveOperationStatusTag", 21)
        };
        foreach (var group in groups)
        foreach (string leaf in group.Leaves.Split(' '))
        foreach (int release in new[] { 19, 20, 21 })
            yield return new object[] { release.ToString(System.Globalization.CultureInfo.InvariantCulture),
                group.Root + "." + leaf, release >= group.Since };
    }

    [Theory, MemberData(nameof(NestedFields))]
    public void Every_nested_leaf_has_exact_release_availability(string release, string field, bool available)
    {
        Assert.Contains(field, UnifiedRuntimeSettingsFields.Nested);
        Assert.Equal(available ? null : "HmiRuntimeSetting." + field + " is not exposed by the V" + release + " PublicAPI.",
            UnifiedRuntimeSettingsFields.Unsupported(release, field));
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Root_release_availability_is_preserved(string release)
    {
        var missing = release == "19" ? new[] { "CentralInputHint", "CentralPanning", "CentralZooming",
            "EnableLanguageCompatibleFontFamilies", "GeneralESIGCommentsStrategy" }
            : release == "20" ? new[] { "CentralInputHint", "CentralPanning", "CentralZooming" } : Array.Empty<string>();
        foreach (string field in UnifiedRuntimeSettingsFields.Root)
            Assert.Equal(missing.Contains(field) ? "HmiRuntimeSetting." + field + " is not exposed by the V" + release + " PublicAPI." : null,
                UnifiedRuntimeSettingsFields.Unsupported(release, field));
    }

    [Theory]
    [InlineData("OpcUaServerRuntimeSettings.Parent")]
    [InlineData("OpcUaServerRuntimeSettings.MaxSessionCount.Value")]
    [InlineData("OpcUaServerRuntimeSettings")]
    [InlineData("LanguageAndFonts.Font")]
    [InlineData("MaxLoginRuntimeSettings.maxLoginErrors")]
    public void Fields_outside_the_exact_table_are_refused(string field)
    {
        Assert.Equal("Unknown runtime setting name.", UnifiedRuntimeSettingsFields.Invalid(new[] { field }));
        foreach (string release in new[] { "19", "20", "21" })
            Assert.Null(UnifiedRuntimeSettingsFields.Unsupported(release, field));
    }

    [Fact]
    public void Table_has_no_extra_or_duplicate_nested_fields()
    {
        var expected = NestedFields().Select(row => (string)row[1]).Distinct(StringComparer.Ordinal).Order().ToArray();
        Assert.Equal(expected, UnifiedRuntimeSettingsFields.Nested.Order().ToArray());
    }
}
