using System;
namespace TiaMcpServer
{
    public partial class CliOptions
    {

        public static CliOptions ParseArgs(string[] args)
        {
            var options = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--workspace-root":
                        if (options.WorkspaceRoot != null || ++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i]
                                .StartsWith("--", StringComparison.Ordinal))
                            throw new System.ArgumentException("INVALID_ARGUMENT: --workspace-root requires one absolute directory.");
                        options.WorkspaceRoot = args[i];
                        options.RequireWorkspaceRoot();
                        break;
                    case "--isolate-openness": case "--no-isolate-openness": case "--openness-worker-child":
                    case "--worker-parent-pid": case "--worker-parent-start": case "--worker-timeout-seconds":
                    case "--transport": case "--http-prefix": case "--http-api-key":
                        throw new System.ArgumentException("Engine MCP host modes have retired. Start TiaMcp.FoundationHost.exe --release-key <20|21>.");
                    case "-tia-major-version":
                    case "--tia-major-version":
                    case "--tia-version":
                        if (++i >= args.Length)
                            throw new System.ArgumentException("A canonical TIA version key is required after " + args[i - 1] + ".");
                        var selectedVersion = TiaMcp.Versioning.TiaVersionCatalog.RequireRunnable(args[i]);
                        if (selectedVersion.EngineOutputDirectory == null) throw new System
                            .ArgumentException("Use the matching PLC foundation host selected in the configurator for " + selectedVersion.DisplayName + ".");
                        if (options.TiaMajorVersion.HasValue && options.TiaMajorVersion.Value != selectedVersion.MajorVersion)
                            throw new System.ArgumentException("Conflicting TIA version options are not allowed.");
                        options.TiaMajorVersion = selectedVersion.MajorVersion;
                        break;

                    case "-tia-portal-location":
                    case "--tia-portal-location":
                        if (i + 1 < args.Length)
                        {
                            options.TiaPortalLocation = args[i + 1];
                            i++;
                        }
                        break;

                    case "-profile":
                    case "--profile":
                        if (i + 1 < args.Length)
                        {
                            options.Profile = args[i + 1];
                            i++;
                        }
                        break;

                    case "-logging":
                    case "--logging":
                        if (i + 1 < args.Length && int
                                .TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int l))
                        {
                            options.Logging = l;
                            i++;
                        }
                        break;

                    case "--run-flowlight-test":
                        options.RunFlowLightTest = true;
                        break;

                    case "--fix-current-flow-binding":
                        options.FixCurrentFlowBinding = true;
                        break;

                    case "--probe-s7-1200-device":
                        options.ProbeS71200Device = true;
                        break;

                    case "--add-1511c-current":
                        options.Add1511CToCurrentProject = true;
                        break;

                    case "--validate-plc-scl-syntax":
                        options.ValidatePlcSclSyntax = true;
                        break;

                    case "--run-motor-minimal-test":
                        options.RunMotorMinimalTest = true;
                        break;

                    case "--probe-ktp700-basic":
                        options.ProbeKtp700Basic = true;
                        break;

                    case "--probe-ktp700-basic-hmi-import":
                        options.ProbeKtp700BasicHmiImport = true;
                        break;

                    case "--probe-ktp700-basic-hmi-tags":
                        options.ProbeKtp700BasicHmiTags = true;
                        break;

                    case "--probe-ktp700-basic-hmi-connection":
                        options.ProbeKtp700BasicHmiConnection = true;
                        break;

                    case "--probe-ktp700-basic-hmi-symbolic-tags":
                        options.ProbeKtp700BasicHmiSymbolicTags = true;
                        break;

                    case "--probe-ktp700-basic-networking":
                        options.ProbeKtp700BasicNetworking = true;
                        break;

                    case "--probe-current-ktp700-hardware-hmi-connection":
                        options.ProbeCurrentKtp700HardwareHmiConnection = true;
                        break;

                    case "--validate-unified-hmi-templates":
                        options.ValidateUnifiedHmiTemplates = true;
                        break;

                    case "--validate-unified-hmi-template-bindings":
                        options.ValidateUnifiedHmiTemplateBindings = true;
                        break;

                    case "--validate-plc-hmi-sync-minimal":
                        options.ValidatePlcHmiSyncMinimal = true;
                        break;

                    case "--validate-plc-chinese-comments-minimal":
                        options.ValidatePlcChineseCommentsMinimal = true;
                        break;

                    case "--hmi-template-directory":
                        if (i + 1 < args.Length)
                        {
                            options.HmiTemplateDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--create-hardware-hmi-connection":
                        options.CreateHardwareHmiConnection = true;
                        break;

                    case "--deep-hardware-hmi-connection-scan":
                        options.DeepHardwareHmiConnectionScan = true;
                        break;

                    case "--list-portal-process-projects":
                        options.ListPortalProcessProjects = true;
                        break;

                    case "--run-capability-self-test":
                        options.RunCapabilitySelfTest = true;
                        break;

                    case "--run-online-monitoring-safety-self-test":
                        options.RunOnlineMonitoringSafetySelfTest = true;
                        break;

                    case "--generate-monitoring-readonly-report":
                        options.GenerateMonitoringReadOnlyReport = true;
                        break;

                    case "--self-test-connect":
                        options.CapabilitySelfTestConnect = true;
                        break;

                    case "--self-test-project-tree":
                        options.CapabilitySelfTestProjectTree = true;
                        break;

                    case "--self-test-inspect-processes":
                        options.CapabilitySelfTestInspectProcesses = true;
                        break;

                    case "--generate-acceptance-report":
                        options.GenerateAcceptanceReport = true;
                        break;

                    case "--acceptance-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.AcceptanceReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--generate-error-report":
                        options.GenerateErrorReport = true;
                        break;

                    case "--error-code":
                        if (i + 1 < args.Length)
                        {
                            options.ErrorReportCode = args[i + 1];
                            i++;
                        }
                        break;

                    case "--error-summary":
                        if (i + 1 < args.Length)
                        {
                            options.ErrorReportSummary = args[i + 1];
                            i++;
                        }
                        break;

                    case "--error-detail":
                        if (i + 1 < args.Length)
                        {
                            options.ErrorReportDetail = args[i + 1];
                            i++;
                        }
                        break;

                    case "--error-actions":
                        if (i + 1 < args.Length)
                        {
                            options.ErrorReportActions = args[i + 1];
                            i++;
                        }
                        break;

                    case "--error-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.ErrorReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--monitoring-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.MonitoringReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--plc-software-path":
                        if (i + 1 < args.Length)
                        {
                            options.PlcSoftwarePath = args[i + 1];
                            i++;
                        }
                        break;

                    case "--watch-table-regex":
                        if (i + 1 < args.Length)
                        {
                            options.WatchTableRegex = args[i + 1];
                            i++;
                        }
                        break;

                    case "--plc-tag-table-regex":
                        if (i + 1 < args.Length)
                        {
                            options.PlcTagTableRegex = args[i + 1];
                            i++;
                        }
                        break;

                    case "--max-plc-tag-tables-to-export":
                        if (i + 1 < args.Length && int
                                .TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                                    out int maxTagTables))
                        {
                            options.MaxPlcTagTablesToExport = maxTagTables;
                            i++;
                        }
                        break;

                    case "--plc-export-directory":
                        if (i + 1 < args.Length)
                        {
                            options.PlcExportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--analyze-reference-assets":
                        options.AnalyzeReferenceAssets = true;
                        break;

                    case "--analyze-global-library-package":
                        options.AnalyzeGlobalLibraryPackage = true;
                        break;

                    case "--analyze-hmi-template-reference":
                        options.AnalyzeHmiTemplateReference = true;
                        break;

                    case "--analyze-hmi-component-catalog":
                        options.AnalyzeHmiComponentCatalog = true;
                        break;

                    case "--run-hmi-action-script-recipe-probe":
                        options.RunHmiActionScriptRecipeProbe = true;
                        break;

                    case "--run-hmi-action-script-recipe-safety-self-test":
                        options.RunHmiActionScriptRecipeSafetySelfTest = true;
                        break;

                    case "--validate-unified-hmi-action-syntaxcheck":
                        options.ValidateUnifiedHmiActionSyntaxCheck = true;
                        break;

                    case "--run-hmi-template-layout-probe":
                        options.RunHmiTemplateLayoutProbe = true;
                        break;

                    case "--run-classic-hmi-minimal-package-probe":
                        options.RunClassicHmiMinimalPackageProbe = true;
                        break;

                    case "--run-classic-hmi-offline-suite":
                        options.RunClassicHmiOfflineSuite = true;
                        break;

                    case "--run-classic-hmi-temporary-import-preflight":
                        options.RunClassicHmiTemporaryImportPreflight = true;
                        break;

                    case "--run-plc-symbol-manifest-probe":
                        options.RunPlcSymbolManifestProbe = true;
                        break;

                    case "--run-offline-release-suite":
                        options.RunOfflineReleaseSuite = true;
                        break;

                    case "--rebuild-release-handoff":
                        options.RebuildReleaseHandoff = true;
                        break;

                    case "--run-hmi-template-plc-sync-precheck-suite":
                        options.RunHmiTemplatePlcSyncPrecheckSuite = true;
                        break;

                    case "--analyze-hmi-template-plc-mapping":
                        options.AnalyzeHmiTemplatePlcMapping = true;
                        break;

                    case "--generate-hmi-template-mapping-skeleton":
                        options.GenerateHmiTemplateMappingSkeleton = true;
                        break;

                    case "--generate-global-library-probe-report":
                        options.GenerateGlobalLibraryProbeReport = true;
                        break;

                    case "--validate-global-library-mastercopy-import":
                        options.ValidateGlobalLibraryMasterCopyImport = true;
                        break;

                    case "--generate-hmi-template-sync-precheck":
                        options.GenerateHmiTemplateSyncPrecheck = true;
                        break;

                    case "--generate-plc-builder-fixture-readiness":
                        options.GeneratePlcBuilderFixtureReadiness = true;
                        break;

                    case "--run-plc-builder-offline-suite":
                        options.RunPlcBuilderOfflineSuite = true;
                        break;

                    case "--run-plc-tag-table-builder-probe":
                        options.RunPlcTagTableBuilderProbe = true;
                        break;

                    case "--run-plc-udt-builder-probe":
                        options.RunPlcUdtBuilderProbe = true;
                        break;

                    case "--run-structured-text-builder-probe":
                        options.RunStructuredTextBuilderProbe = true;
                        break;

                    case "--run-plc-fc-block-composer-probe":
                        options.RunPlcFcBlockComposerProbe = true;
                        break;

                    case "--run-plc-global-db-builder-probe":
                        options.RunPlcGlobalDbBuilderProbe = true;
                        break;

                    case "--run-flgnet-call-builder-probe":
                        options.RunFlgNetCallBuilderProbe = true;
                        break;

                    case "--validate-mapped-hmi-template-bindings":
                        options.ValidateMappedHmiTemplateBindings = true;
                        break;

                    case "--mapped-hmi-template-offline-only":
                        options.MappedHmiTemplateOfflineOnly = true;
                        break;

                    case "--reference-project-path":
                        if (i + 1 < args.Length)
                        {
                            options.ReferenceProjectPath = args[i + 1];
                            i++;
                        }
                        break;

                    case "--reference-global-library-path":
                        if (i + 1 < args.Length)
                        {
                            options.ReferenceGlobalLibraryPath = args[i + 1];
                            i++;
                        }
                        break;

                    case "--reference-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.ReferenceReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--hmi-template-reference-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.HmiTemplateReferenceReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--hmi-component-catalog-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.HmiComponentCatalogReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--hmi-template-layout-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.HmiTemplateLayoutReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--classic-hmi-package-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.ClassicHmiPackageReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--classic-hmi-offline-suite-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.ClassicHmiOfflineSuiteReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--classic-hmi-temporary-import-preflight-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.ClassicHmiTemporaryImportPreflightReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--plc-symbol-manifest-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.PlcSymbolManifestReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--offline-release-suite-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.OfflineReleaseSuiteReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--offline-release-suite-json-path":
                        if (i + 1 < args.Length)
                        {
                            options.OfflineReleaseSuiteJsonPath = args[i + 1];
                            i++;
                        }
                        break;

                    case "--hmi-template-plc-sync-precheck-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.HmiTemplatePlcSyncPrecheckReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--hmi-template-plc-mapping-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.HmiTemplatePlcMappingReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--hmi-template-mapping-path":
                        if (i + 1 < args.Length)
                        {
                            options.HmiTemplateMappingPath = args[i + 1];
                            i++;
                        }
                        break;

                    case "--global-library-probe-json":
                        if (i + 1 < args.Length)
                        {
                            options.GlobalLibraryProbeJsonPath = args[i + 1];
                            i++;
                        }
                        break;

                    case "--global-library-probe-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.GlobalLibraryProbeReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--hmi-template-sync-precheck-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.HmiTemplateSyncPrecheckReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--plc-builder-fixture-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.PlcBuilderFixtureReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--plc-builder-probe-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.PlcBuilderProbeReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--plc-builder-suite-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.PlcBuilderSuiteReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--global-library-package-path":
                        if (i + 1 < args.Length)
                        {
                            options.GlobalLibraryPackagePath = args[i + 1];
                            i++;
                        }
                        break;

                    case "--global-library-report-directory":
                        if (i + 1 < args.Length)
                        {
                            options.GlobalLibraryReportDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--probe-hardware-hmi-connection-owner-candidates":
                        options.ProbeHardwareHmiConnectionOwnerCandidates = true;
                        break;

                    case "--probe-hardware-hmi-connection-whitelisted-services":
                        options.ProbeHardwareHmiConnectionWhitelistedServices = true;
                        break;

                    case "--search-gsd":
                        if (i + 1 < args.Length)
                        {
                            options.SearchGsdKeyword = args[i + 1];
                            i++;
                        }
                        break;

                    case "--search-hardware-catalog":
                        if (i + 1 < args.Length)
                        {
                            options.SearchHardwareCatalogKeyword = args[i + 1];
                            i++;
                        }
                        break;

                    case "--project-directory":
                        if (i + 1 < args.Length)
                        {
                            options.ProjectDirectory = args[i + 1];
                            i++;
                        }
                        break;

                    case "--project-name":
                        if (i + 1 < args.Length)
                        {
                            options.ProjectName = args[i + 1];
                            i++;
                        }
                        break;

                    case "--tia-step-timeout-seconds":
                        if (i + 1 < args.Length && int
                                .TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                                    out int stepTimeoutSeconds))
                        {
                            options.TiaStepTimeoutSeconds = stepTimeoutSeconds;
                            i++;
                        }
                        break;

                    case "-with-ui":
                    case "--with-ui":
                        options.PortalWithUserInterface = true;
                        break;

                    case "--engine-worker":
                        options.EngineWorker = true;
                        break;

                    case "--write-tool-catalog":
                        if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", System.StringComparison.Ordinal))
                            throw new System.ArgumentException("--write-tool-catalog requires a file path.");
                        options.ToolCatalogOutput = args[i];
                        break;

                }
            }
            if (options.EngineWorker && (options.ToolCatalogOutput != null))
                throw new System.ArgumentException("Engine worker mode cannot be combined with another worker mode or catalog export.");
            return options;
        }
    }
}
