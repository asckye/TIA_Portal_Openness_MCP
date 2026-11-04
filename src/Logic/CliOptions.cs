using System;
namespace TiaMcpServer
{
    public partial class CliOptions
    {
        public int? TiaMajorVersion { get; set; }
        public string? TiaPortalLocation { get; set; } // explicit install root, e.g. D:\app\TIA20\Portal V20
        public int? Logging { get; set; } // 1=stderr, 2=Debug, 3=EventLog
        // Tool roster: "lite" (default) or "full" (everything).
        // null = not given on the command line; TIA_MCP_PROFILE then decides.
        public string? Profile { get; set; }
        public bool EngineWorker { get; set; }
        public string? ToolCatalogOutput { get; set; }
        public bool RunFlowLightTest { get; set; }
        public bool FixCurrentFlowBinding { get; set; }
        public bool ProbeS71200Device { get; set; }
        public bool Add1511CToCurrentProject { get; set; }
        public bool ValidatePlcSclSyntax { get; set; }
        public bool RunMotorMinimalTest { get; set; }
        public bool ProbeKtp700Basic { get; set; }
        public bool ProbeKtp700BasicHmiImport { get; set; }
        public bool ProbeKtp700BasicHmiTags { get; set; }
        public bool ProbeKtp700BasicHmiConnection { get; set; }
        public bool ProbeKtp700BasicHmiSymbolicTags { get; set; }
        public bool ProbeKtp700BasicNetworking { get; set; }
        public bool ProbeCurrentKtp700HardwareHmiConnection { get; set; }
        public bool ValidateUnifiedHmiTemplates { get; set; }
        public bool ValidateUnifiedHmiTemplateBindings { get; set; }
        public bool ValidatePlcHmiSyncMinimal { get; set; }
        public bool ValidatePlcChineseCommentsMinimal { get; set; }
        public string? HmiTemplateDirectory { get; set; }
        public string? WorkspaceRoot { get; set; }

        public string RequireWorkspaceRoot()
        {
            if (!AbsolutePath(WorkspaceRoot) || !System.IO.Directory.Exists(WorkspaceRoot))
                throw new System.ArgumentException("INVALID_ARGUMENT: --workspace-root requires an existing absolute directory: " + WorkspaceRoot);
            return System.IO.Path.GetFullPath(WorkspaceRoot);
        }
        public static string RequireInput(string? value, string option)
        {
            if (!AbsolutePath(value) || !System.IO.Directory.Exists(value) && !System.IO.File.Exists(value))
                throw new System.ArgumentException("INVALID_ARGUMENT: " + option + " requires an explicit absolute path: " + value);
            return value;
        }
        private static bool AbsolutePath(string? value) => !string.IsNullOrWhiteSpace(value) && System.IO.Path.IsPathRooted(value)
            && (System.IO.Path.DirectorySeparatorChar != '\\' || System.IO.Path.GetPathRoot(value)!.Length >= 3);

        public void ValidatePrivateInputs()
        {
            if (AnalyzeReferenceAssets || AnalyzeGlobalLibraryPackage || AnalyzeHmiTemplateReference || AnalyzeHmiComponentCatalog
                || RunHmiActionScriptRecipeProbe || RunHmiTemplateLayoutProbe || RunClassicHmiMinimalPackageProbe || RunClassicHmiOfflineSuite
                || RunClassicHmiTemporaryImportPreflight || RunPlcSymbolManifestProbe || RunOfflineReleaseSuite || RebuildReleaseHandoff
                || RunHmiTemplatePlcSyncPrecheckSuite || AnalyzeHmiTemplatePlcMapping || GenerateHmiTemplateMappingSkeleton
                || GenerateGlobalLibraryProbeReport || GenerateHmiTemplateSyncPrecheck || GeneratePlcBuilderFixtureReadiness
                || RunPlcBuilderOfflineSuite || RunPlcTagTableBuilderProbe || RunPlcUdtBuilderProbe || RunStructuredTextBuilderProbe
                || RunPlcFcBlockComposerProbe || RunPlcGlobalDbBuilderProbe || RunFlgNetCallBuilderProbe
                || ValidateMappedHmiTemplateBindings || ProbeKtp700BasicHmiImport || RunMotorMinimalTest
                || GenerateMonitoringReadOnlyReport || ValidateUnifiedHmiActionSyntaxCheck || ValidateGlobalLibraryMasterCopyImport)
                RequireWorkspaceRoot();
            if (ValidateUnifiedHmiTemplates || ValidateUnifiedHmiTemplateBindings || ValidateMappedHmiTemplateBindings
                || AnalyzeHmiTemplateReference || AnalyzeHmiComponentCatalog || RunHmiActionScriptRecipeProbe || RunHmiTemplateLayoutProbe
                || RunHmiTemplatePlcSyncPrecheckSuite || AnalyzeHmiTemplatePlcMapping || GenerateHmiTemplateMappingSkeleton || GenerateHmiTemplateSyncPrecheck)
                RequireInput(HmiTemplateDirectory, "--hmi-template-directory");
            if (AnalyzeHmiComponentCatalog) RequireInput(GlobalLibraryProbeJsonPath, "--global-library-probe-json-path");
            if (AnalyzeReferenceAssets || AnalyzeHmiTemplateReference) RequireInput(ReferenceProjectPath, "--reference-project-path");
            if (AnalyzeReferenceAssets || AnalyzeHmiTemplateReference) RequireInput(ReferenceGlobalLibraryPath, "--reference-global-library-path");
            if (AnalyzeGlobalLibraryPackage || GenerateGlobalLibraryProbeReport || ValidateGlobalLibraryMasterCopyImport)
                RequireInput(string.IsNullOrWhiteSpace(GlobalLibraryPackagePath) ? ReferenceGlobalLibraryPath : GlobalLibraryPackagePath,
                        "--global-library-package-path");
        }
        public bool CreateHardwareHmiConnection { get; set; }
        public bool DeepHardwareHmiConnectionScan { get; set; }
        public bool ListPortalProcessProjects { get; set; }
        public bool RunCapabilitySelfTest { get; set; }
        public bool RunOnlineMonitoringSafetySelfTest { get; set; }
        public bool GenerateMonitoringReadOnlyReport { get; set; }
        public bool CapabilitySelfTestConnect { get; set; }
        public bool CapabilitySelfTestProjectTree { get; set; }
        public bool CapabilitySelfTestInspectProcesses { get; set; }
        public bool GenerateAcceptanceReport { get; set; }
        public string? AcceptanceReportDirectory { get; set; }
        public bool GenerateErrorReport { get; set; }
        public string? ErrorReportCode { get; set; }
        public string? ErrorReportSummary { get; set; }
        public string? ErrorReportDetail { get; set; }
        public string? ErrorReportActions { get; set; }
        public string? ErrorReportDirectory { get; set; }
        public string? MonitoringReportDirectory { get; set; }
        public string? PlcSoftwarePath { get; set; }
        public string? WatchTableRegex { get; set; }
        public string? PlcTagTableRegex { get; set; }
        public int? MaxPlcTagTablesToExport { get; set; }
        public string? PlcExportDirectory { get; set; }
        public bool AnalyzeReferenceAssets { get; set; }
        public bool AnalyzeGlobalLibraryPackage { get; set; }
        public bool AnalyzeHmiTemplateReference { get; set; }
        public bool AnalyzeHmiComponentCatalog { get; set; }
        public bool RunHmiActionScriptRecipeProbe { get; set; }
        public bool RunHmiActionScriptRecipeSafetySelfTest { get; set; }
        public bool ValidateUnifiedHmiActionSyntaxCheck { get; set; }
        public bool RunHmiTemplateLayoutProbe { get; set; }
        public bool RunClassicHmiMinimalPackageProbe { get; set; }
        public bool RunClassicHmiOfflineSuite { get; set; }
        public bool RunClassicHmiTemporaryImportPreflight { get; set; }
        public bool RunPlcSymbolManifestProbe { get; set; }
        public bool RunOfflineReleaseSuite { get; set; }
        public bool RebuildReleaseHandoff { get; set; }
        public bool RunHmiTemplatePlcSyncPrecheckSuite { get; set; }
        public bool AnalyzeHmiTemplatePlcMapping { get; set; }
        public bool GenerateHmiTemplateMappingSkeleton { get; set; }
        public bool GenerateGlobalLibraryProbeReport { get; set; }
        public bool ValidateGlobalLibraryMasterCopyImport { get; set; }
        public bool GenerateHmiTemplateSyncPrecheck { get; set; }
        public bool GeneratePlcBuilderFixtureReadiness { get; set; }
        public bool RunPlcBuilderOfflineSuite { get; set; }
        public bool RunPlcTagTableBuilderProbe { get; set; }
        public bool RunPlcUdtBuilderProbe { get; set; }
        public bool RunStructuredTextBuilderProbe { get; set; }
        public bool RunPlcFcBlockComposerProbe { get; set; }
        public bool RunPlcGlobalDbBuilderProbe { get; set; }
        public bool RunFlgNetCallBuilderProbe { get; set; }
        public bool ValidateMappedHmiTemplateBindings { get; set; }
        public bool MappedHmiTemplateOfflineOnly { get; set; }
        public string? ReferenceProjectPath { get; set; }
        public string? ReferenceGlobalLibraryPath { get; set; }
        public string? ReferenceReportDirectory { get; set; }
        public string? HmiTemplateReferenceReportDirectory { get; set; }
        public string? HmiComponentCatalogReportDirectory { get; set; }
        public string? HmiTemplateLayoutReportDirectory { get; set; }
        public string? ClassicHmiPackageReportDirectory { get; set; }
        public string? ClassicHmiOfflineSuiteReportDirectory { get; set; }
        public string? ClassicHmiTemporaryImportPreflightReportDirectory { get; set; }
        public string? PlcSymbolManifestReportDirectory { get; set; }
        public string? OfflineReleaseSuiteReportDirectory { get; set; }
        public string? OfflineReleaseSuiteJsonPath { get; set; }
        public string? HmiTemplatePlcSyncPrecheckReportDirectory { get; set; }
        public string? HmiTemplatePlcMappingReportDirectory { get; set; }
        public string? HmiTemplateMappingPath { get; set; }
        public string? GlobalLibraryProbeJsonPath { get; set; }
        public string? GlobalLibraryProbeReportDirectory { get; set; }
        public string? HmiTemplateSyncPrecheckReportDirectory { get; set; }
        public string? PlcBuilderFixtureReportDirectory { get; set; }
        public string? PlcBuilderProbeReportDirectory { get; set; }
        public string? PlcBuilderSuiteReportDirectory { get; set; }
        public string? GlobalLibraryPackagePath { get; set; }
        public string? GlobalLibraryReportDirectory { get; set; }
        public bool ProbeHardwareHmiConnectionOwnerCandidates { get; set; }
        public bool ProbeHardwareHmiConnectionWhitelistedServices { get; set; }
        public string? SearchGsdKeyword { get; set; }
        public string? SearchHardwareCatalogKeyword { get; set; }
        public string? ProjectDirectory { get; set; }
        public string? ProjectName { get; set; }
        public int? TiaStepTimeoutSeconds { get; set; }
        public bool PortalWithUserInterface { get; set; } // --with-ui: launch TIA with full GUI (slower) instead of headless

        // These commands print static information and must work without a supported install.
        public static bool IsInformationalCommand(string[] args)
        {
            if (args.Length == 0) return false;
            switch (args[0].ToLowerInvariant())
            {
                case "help": case "--help": case "-h": case "version": case "schema": return true;
                default: return false;
            }
        }
    }
}
