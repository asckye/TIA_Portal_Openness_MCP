using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Contracts
{
    // Reviewed release availability, shared by the host, worker and roster generators.
    // Availability never implies native acceptance; ported families remain current / NOT RUN.
    public static class PortedFamilies
    {
        public sealed class Family
        {
            public string Name { get; }
            public string OperationPrefix { get; }
            public string[] Releases { get; }
            public string[] Tools { get; }
            public IReadOnlyDictionary<string, string[]> Actions { get; }
            public Family(string name, string operationPrefix, string[] releases, string[] tools,
                IReadOnlyDictionary<string, string[]>? actions = null)
            { Name = name; OperationPrefix = operationPrefix; Releases = releases; Tools = tools;
                Actions = actions ?? new Dictionary<string, string[]>(StringComparer.Ordinal); }
            public bool Available(string release) => Releases.Contains(release, StringComparer.Ordinal);
            public bool SupportsAction(string release, string action) => Available(release)
                && (Actions.Count == 0 || Actions.TryGetValue(action, out var releases) && releases.Contains(release, StringComparer.Ordinal));
        }

        public static readonly IReadOnlyList<Family> All = new[] {
            new Family("F01", "host-meta",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "ApplyToolBatch", "BuildReleaseDiagnosticReport", "BuildReleaseHandoffArtifacts", "BuildReleaseManifest", "BuildReleaseRunbook", "CallTool", "CheckProductUpdate", "ClearExportHandles", "DeleteExportHandle", "FindTools", "GenerateAcceptanceReport", "GenerateErrorReport", "GetEnvironmentDiagnostics", "GetExportContent", "GetNativeInvocationLog", "GetOpennessCompatibility", "GetOpennessGuidance", "GetOpennessWorkerStatus", "GetV21EcosystemCatalog", "ListExportHandles", "ListToolCategories", "PreviewToolBatch", "PreviewToolCall", "RestartOpennessWorker", "RunOfflineReleaseValidationSuite", "RunOnlineMonitoringSafetySelfTest", "RunReadOnlyToolBatch", "SaveExportContent" }),
            new Family("F02", "plc-offline",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "AnalyzePlcReferences", "AnalyzePlcSclSource", "AuditEngineeringExports", "BuildPlcAliasAlarmLad", "ComparePlcBlockDocuments", "DecodePlcSimaticMl", "ExtractPlcBlockMetrics", "GeneratePlcDocumentation", "InspectSimaticSdCompatibility", "InstantiatePlcTemplates", "PatchPlcBlockDocument", "RenderPlcBlockDocument", "RenderPlcVisualDiff", "ScanPlcSourceAnnotations", "ValidatePlcDocumentSchemas", "WritePlcSclSourceFile" }),
            new Family("F03", "hmi-offline",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "AnalyzeGlobalLibraryPackage", "AnalyzeHmiTemplateReference", "AnalyzeUnifiedHmiTemplateLayout", "BuildClassicHmiMinimalPackage", "BuildClassicHmiScreen", "BuildClassicHmiTagTable", "BuildUnifiedHmiButtonActionScript", "BuildUnifiedHmiLayoutDesign", "BuildUnifiedHmiTemplateApplyDesign", "BuildUnifiedHmiTemplateApplyDesignManifest", "BuildUnifiedHmiThemeDesign", "ManageUnifiedCwcPackage", "PlanGlobalLibraryTemplateReuse", "RunClassicHmiOfflineValidationSuite", "RunClassicHmiTemporaryImportPreflight", "RunHmiActionScriptRecipeSafetySelfTest", "RunHmiTemplatePlcSyncPrecheckSuite", "ValidateClassicHmiMinimalPackageFiles", "ValidateClassicHmiMinimalPackagePlcSync", "WriteClassicHmiMinimalPackageFiles" }),
            new Family("F08", "plc-organisation",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "CreatePlcBlockGroup", "CreatePlcTypeGroup", "DeleteEmptyPlcBlockGroup", "DeletePlcBlock", "DeletePlcTagTable", "DeletePlcType", "ManagePlcBlockProtection", "ManagePlcUserGroup", "MovePlcBlockToGroup", "ListPlcSystemGroups" }),
            new Family("F09", "plc-software",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "CreatePlcInstanceDb", "ManagePlcTagDefinition", "SetPlcProgram", "GetPlcTagTableConstants", "GetPlcBlockEditCapabilities", "ImportPlcBlockVerified", "BuildAndImportPlcArtifact", "DescribePlcBlockLogic", "RepairAndReimportPlcBlock", "ImportPlcTagTablesFromDirectory", "SeedProjectFromReference" }),
            new Family("F11", "plc-sources",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "GeneratePlcSourceFromBlocks", "ManagePlcExternalSources", "GetPlcCrossReferences" }),
            new Family("F17", "plc-compile",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "CompileDevice", "CompileHmiDiagnostics" }),
            new Family("F18", "hardware-devices",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "CreateDevice", "CreateGsdDevice", "CreateHardwareCatalogDevice", "ListDevices", "GetDeviceInfo", "GetDeviceItemInfo", "GetDeviceItemTree", "SearchInstalledGsdDevices", "GetDeviceAttributes", "SetDeviceItemAttribute", "SetPlcCpuSettings", "GetDevicePlugLocations", "PlugDeviceItem", "ManageHardwareObject", "ManageDeviceUserGroup", "GetHardwareFeatures", "ManageDeviceServiceObjects", "ManageHardwareUtilities", "ExchangeSystemDiagnosticsSettings" }),
            new Family("F20", "hardware-network",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "ListIoSystems", "ManageIoSystem", "ListNetworkDomains", "ManageNetworkDomain", "ListTransferAreas", "ManageTransferArea", "ListDeviceItemChannels", "SetDeviceItemChannel", "ManagePortInterconnection", "GetDeviceItemNetworkInfo", "ConnectDeviceNodesToProfinetSubnet", "PlanHardwareNetworkConfiguration", "EnsureSubnet", "AttachDeviceNodeToSubnet", "ProbeHardwareHmiConnectionOwnerCandidates", "ProbeHardwareHmiConnectionWhitelistedServices", "GetProjectTopology", "ListCommunicationConnections", "ManageCommunicationConnection" }),
            new Family("F21", "hardware-aml",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "BuildDeviceAmlDocument", "ExportDeviceAml", "ImportDeviceAml" }),
            new Family("F19", "hardware-addressing",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "GetDeviceAddressing", "GetDeviceIpAddress", "GetDeviceItemIoAddresses", "SetDeviceAddress", "SetDeviceItemIoAddress" })
        };

        public static readonly IReadOnlyDictionary<string, string[]> ToolReleases = new Dictionary<string, string[]>(StringComparer.Ordinal) {
            // Format evidence is deliberately separate from managed-code availability.
            ["BuildPlcAliasAlarmLad"] = new[] { "20", "21" },
            ["DecodePlcSimaticMl"] = new[] { "20", "21" },
            ["ValidatePlcDocumentSchemas"] = new[] { "20", "21" },
            ["InspectSimaticSdCompatibility"] = new[] { "20", "21" },
            ["BuildClassicHmiMinimalPackage"] = new[] { "20", "21" },
            ["BuildClassicHmiScreen"] = new[] { "20", "21" },
            ["BuildClassicHmiTagTable"] = new[] { "20", "21" },
            ["RunClassicHmiOfflineValidationSuite"] = new[] { "20", "21" },
            ["RunClassicHmiTemporaryImportPreflight"] = new[] { "20", "21" },
            ["RunHmiTemplatePlcSyncPrecheckSuite"] = new[] { "20", "21" },
            ["ValidateClassicHmiMinimalPackageFiles"] = new[] { "20", "21" },
            ["ValidateClassicHmiMinimalPackagePlcSync"] = new[] { "20", "21" },
            ["WriteClassicHmiMinimalPackageFiles"] = new[] { "20", "21" },
            ["AnalyzeUnifiedHmiTemplateLayout"] = new[] { "19", "20", "21" },
            ["BuildUnifiedHmiButtonActionScript"] = new[] { "19", "20", "21" },
            ["BuildUnifiedHmiLayoutDesign"] = new[] { "19", "20", "21" },
            ["BuildUnifiedHmiTemplateApplyDesign"] = new[] { "19", "20", "21" },
            ["BuildUnifiedHmiTemplateApplyDesignManifest"] = new[] { "19", "20", "21" },
            ["BuildUnifiedHmiThemeDesign"] = new[] { "19", "20", "21" },
            ["ManageUnifiedCwcPackage"] = new[] { "19", "20", "21" },
            ["RunHmiActionScriptRecipeSafetySelfTest"] = new[] { "19", "20", "21" },
            ["ListCommunicationConnections"] = new[] { "21" },
            ["ManageCommunicationConnection"] = new[] { "21" }
        };

        public static Family ForTool(string tool) => All.Single(f => f.Tools.Contains(tool, StringComparer.Ordinal));
        public static bool Contains(string tool) => All.Any(f => f.Tools.Contains(tool, StringComparer.Ordinal));
        public static bool Available(string release, string tool) => ForTool(tool).Available(release)
            && (!ToolReleases.TryGetValue(tool, out var releases) || releases.Contains(release, StringComparer.Ordinal));
    }
}
