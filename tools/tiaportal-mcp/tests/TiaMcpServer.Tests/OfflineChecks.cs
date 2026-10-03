using System.Collections.Generic;
using TiaMcpServer.Tests.Shared;
using Xunit;
using Xunit.Sdk;

namespace TiaMcpServer.Tests
{
    [XunitTestCaseDiscoverer("TiaMcpServer.Tests.Shared.CheckTheoryDiscoverer", "TiaMcpServer.Tests")]
    public sealed class CheckTheoryAttribute : TheoryAttribute { }

    public sealed class OfflineChecks
    {
        [CheckTheory]
        [MemberData(nameof(Rows), DisableDiscoveryEnumeration = true)]
        public void Check(CheckRow row) => row.Verify();

        public static IEnumerable<object[]> Rows()
        {
            // Preserve the console runner's suite order, including suites that mutate shared state.
            foreach (var row in CheckSuite.Run(nameof(PlcFoundationPolicyTests), PlcFoundationPolicyTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(TiaVersionCatalogTests), TiaVersionCatalogTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(ImportOrderTests), ImportOrderTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(EcosystemTests), EcosystemTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(McpLocalProcessTests), McpLocalProcessTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run("ScaffoldOperationsTests", TiaMcpServer.ModelContextProtocol.McpServer.CheckScaffoldOperations)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(OfficialWorkflowTests), OfficialWorkflowTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(PlcTypeGroupCreationTests), PlcTypeGroupCreationTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(EngineeringOperationsTests), EngineeringOperationsTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(EngineeringObjectAddressTests), EngineeringObjectAddressTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(NativeFileOutputTests), NativeFileOutputTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(ExtendedEngineeringTests), ExtendedEngineeringTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(MigrationReadTests), MigrationReadTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(EngineeringDefectTests), EngineeringDefectTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(HmiInspectionTests), HmiInspectionTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(HmiSnapshotSafetyTests), HmiSnapshotSafetyTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(UnifiedGlobalScriptEditTests), UnifiedGlobalScriptEditTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(GraphicSelectionTests), GraphicSelectionTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(RuntimeSettingsTests), RuntimeSettingsTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(SoftwareContainerLookupTests), SoftwareContainerLookupTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(PlcListingReadTests), PlcListingReadTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(UnifiedMultilingualTextTests), UnifiedMultilingualTextTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(HmiTemplateLayoutExecutionCheckTests), HmiTemplateLayoutExecutionCheckTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(S7ResScannerTests), S7ResScannerTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(ExportsAndArgDiagnosticsTests), ExportsAndArgDiagnosticsTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(UnifiedScriptSyntaxCheckTests), UnifiedScriptSyntaxCheckTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(HmiScreenTraversalTests), HmiScreenTraversalTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(DownloadPromptPolicyTests), DownloadPromptPolicyTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(PlcBlockServicesTests), PlcBlockServicesTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(HardwareServicesTests), HardwareServicesTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(ProjectSecurityTests), ProjectSecurityTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(UnifiedUiModelTests), UnifiedUiModelTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(MotionProDiagClassicHmiTests), MotionProDiagClassicHmiTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(OfflineAnalysisTests), OfflineAnalysisTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(PlcEditingTests), PlcEditingTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(V21EcosystemTests), V21EcosystemTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(RuntimeChannelsTests), RuntimeChannelsTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(PlcDocumentationTests), PlcDocumentationTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(PlcSimAdvancedTests), PlcSimAdvancedTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(HardwareAmlTests), HardwareAmlTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(LadTextRendererTests), LadTextRendererTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(SymbolQuotingReadbackTests), SymbolQuotingReadbackTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(SafetyLogicTests), SafetyLogicTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(UnifiedScreenItemTests), UnifiedScreenItemTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(UnifiedExchangeTests), UnifiedExchangeTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(HardwareNetworkTests), HardwareNetworkTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(LibraryDeepTests), LibraryDeepTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(LibraryWriteSafetyTests), LibraryWriteSafetyTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(SecurityDeepTests), SecurityDeepTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(BaseLeftoversTests), BaseLeftoversTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(SoftwareUnitDeepTests), SoftwareUnitDeepTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(Step7LeftoversTests), Step7LeftoversTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(TechnologyMappingTests), TechnologyMappingTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(ClassicHmiFoldersTests), ClassicHmiFoldersTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(BindingAndTagDeletionTests), BindingAndTagDeletionTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(SivarcTests), SivarcTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(StartdriveTests), StartdriveTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(DccTests), DccTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(SafetyValidationTests), SafetyValidationTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(TestSuiteTests), TestSuiteTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(TeamcenterTests), TeamcenterTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(CfcTests), CfcTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(PlcTagEditingTests), PlcTagEditingTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(PreflightAndUpdateTests), PreflightAndUpdateTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(CrossReferenceGuardTests), CrossReferenceGuardTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(EngineeringAuditTests), EngineeringAuditTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(CallDisciplineTests), CallDisciplineTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(ToolExampleLibraryTests), ToolExampleLibraryTests.Run)) yield return row;
        }
    }
}
