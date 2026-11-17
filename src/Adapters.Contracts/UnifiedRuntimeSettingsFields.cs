using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Contracts;

public static class UnifiedRuntimeSettingsFields
{
    public const int MaxFieldsPerCall = 12;

    public static IReadOnlyList<string> Root { get; } = Array.AsReadOnly(new[] {
        "StartScreen", "ScreenResolution", "CentralInputHint", "CentralPanning", "CentralZooming",
        "BitSelection", "BitSelectionStrategyForResourceLists", "BitSelectionStrategyForTagDynamization",
        "EnableLanguageCompatibleFontFamilies", "AutoLogOffURL", "GMPEnabled", "GeneralESIGCommentsStrategy"
    });

    public static IReadOnlyList<string> Nested { get; } = Array.AsReadOnly(new[] {
        "OpcUaServerRuntimeSettings.ActAsOPCServer",
        "OpcUaServerRuntimeSettings.AllowAlarmOperations",
        "OpcUaServerRuntimeSettings.EnableAlarmConditions",
        "OpcUaServerRuntimeSettings.EnableGuestAuthentication",
        "OpcUaServerRuntimeSettings.EnableLocalDiscoveryServer",
        "OpcUaServerRuntimeSettings.EnableUsernameAndPasswordAuthentication",
        "OpcUaServerRuntimeSettings.OpcUaServerSecurity128RsaModeSigned",
        "OpcUaServerRuntimeSettings.OpcUaServerSecurity128RsaModeSignedAndEncrypted",
        "OpcUaServerRuntimeSettings.OpcUaServerSecurity256ModeSigned",
        "OpcUaServerRuntimeSettings.OpcUaServerSecurity256ModeSignedAndEncrypted",
        "OpcUaServerRuntimeSettings.OpcUaServerSecurity256ShaModeSigned",
        "OpcUaServerRuntimeSettings.OpcUaServerSecurity256ShaModeSignedAndEncrypted",
        "OpcUaServerRuntimeSettings.OpcUaServerSecurityAes128Sha256RsaOaepSigned",
        "OpcUaServerRuntimeSettings.OpcUaServerSecurityAes128Sha256RsaOaepSignedAndEncrypted",
        "OpcUaServerRuntimeSettings.OpcUaServerSecurityAes256Sha256RsaPssSigned",
        "OpcUaServerRuntimeSettings.OpcUaServerSecurityAes256Sha256RsaPssSignedAndEncrypted",
        "OpcUaServerRuntimeSettings.OpcUaServerSecurityNone",
        "OpcUaServerRuntimeSettings.EndpointUrlPortNumber",
        "OpcUaServerRuntimeSettings.MaxMonitoredItemPerSubscriptionCount",
        "OpcUaServerRuntimeSettings.MaxSessionCount",
        "OpcUaServerRuntimeSettings.MaxSessionTimeout",
        "OpcUaServerRuntimeSettings.MinPublishingInterval",
        "MaxLoginRuntimeSettings.EnableLockAfterNumberOfAttempts",
        "MaxLoginRuntimeSettings.MaxLoginErrors",
        "ProcessDiagnosticsRuntimeSettings.EnableProcessDiagnostics",
        "ProcessDiagnosticsRuntimeSettings.CriteriaAnalysisAbsoluteAddress",
        "ProcessDiagnosticsRuntimeSettings.CriteriaAnalysisComment",
        "ProcessDiagnosticsRuntimeSettings.CriteriaAnalysisSymbol",
        "ProcessDiagnosticsRuntimeSettings.CriteriaAnalysisValue",
        "ProcessDiagnosticsRuntimeSettings.CriteriaAnalysisAll",
        "ProcessDiagnosticsRuntimeSettings.CriteriaAnalysisExtendText",
        "HmiReportingSettings.IsReportingEnabled",
        "HmiReportingSettings.ReportingDatabaseStorage",
        "HmiReportingSettings.ReportingMainStorage",
        "HmiReportingSettings.ReportingDatabaseStoragePath",
        "HmiReportingSettings.ReportingMainStoragePath",
        "RuntimeResourceSettings.EnableHighResolutionGraphicsOptimization",
        "TelemetryRuntimeSettings.TelemetryActive",
        "TelemetryRuntimeSettings.TelemetryStorageFolder",
        "TelemetryRuntimeSettings.TelemetryStorageMedia",
        "HmiUpssRuntimeSettings.GlobalScopePersistencyAuthorization",
        "HmiUpssRuntimeSettings.PersistencyStrategy",
        "HmiUnifiedTagSettings.IgnoreInitialQCNotifications",
        "HmiUnifiedTagSettings.IgnoreTimestampNotifications",
        "HmiUnifiedTagSettings.TagOptimizationActive",
        "HmiExclusiveOperationSettings.ExclusiveOperationControlTag",
        "HmiExclusiveOperationSettings.ExclusiveOperationStatusTag"
    });

    public static string? Unsupported(string release, string field)
    {
        bool available = Root.Contains(field, StringComparer.Ordinal) || Nested.Contains(field, StringComparer.Ordinal);
        if (release == "19" || release == "20")
            available &= field != "CentralInputHint" && field != "CentralPanning" && field != "CentralZooming"
                && field != "HmiUnifiedTagSettings.TagOptimizationActive"
                && !field.StartsWith("HmiExclusiveOperationSettings.", StringComparison.Ordinal);
        if (release == "19")
            available &= field != "EnableLanguageCompatibleFontFamilies" && field != "GeneralESIGCommentsStrategy"
                && !field.StartsWith("TelemetryRuntimeSettings.", StringComparison.Ordinal)
                && !field.StartsWith("HmiUpssRuntimeSettings.", StringComparison.Ordinal)
                && !field.StartsWith("HmiUnifiedTagSettings.", StringComparison.Ordinal);
        return available && (release == "19" || release == "20" || release == "21") ? null
            : "HmiRuntimeSetting." + field + " is not exposed by the V" + release + " PublicAPI.";
    }
}
