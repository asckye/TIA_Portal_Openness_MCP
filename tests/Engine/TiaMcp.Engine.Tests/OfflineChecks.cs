using System.Collections.Generic;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Xunit.Sdk;

namespace TiaMcp.Engine.Tests
{
    [XunitTestCaseDiscoverer("TiaMcp.Engine.Tests.Shared.CheckTheoryDiscoverer", "TiaMcp.Engine.Tests")]
    public sealed class CheckTheoryAttribute : TheoryAttribute { }

    public sealed class OfflineChecks
    {
        [CheckTheory]
        [MemberData(nameof(Rows), DisableDiscoveryEnumeration = true)]
        public void Check(CheckRow row) => row.Verify();

        public static IEnumerable<object[]> Rows()
        {
            ToolBridgeFixture.Configure();
            // Preserve the console runner's suite order, including suites that mutate shared state.
            foreach (var row in CheckSuite.Run(nameof(TiaVersionCatalogTests), TiaVersionCatalogTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run("ScaffoldOperationsTests", TiaMcpServer.ModelContextProtocol.McpServer.CheckScaffoldOperations)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(OfficialWorkflowTests), OfficialWorkflowTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(PlcTypeGroupCreationTests), PlcTypeGroupCreationTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(EngineeringOperationsTests), EngineeringOperationsTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(EngineeringObjectAddressTests), EngineeringObjectAddressTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(ExtendedEngineeringTests), ExtendedEngineeringTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(MigrationReadTests), MigrationReadTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(EngineeringDefectTests), EngineeringDefectTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(HmiInspectionTests), HmiInspectionTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(HmiSnapshotSafetyTests), HmiSnapshotSafetyTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(UnifiedGlobalScriptEditTests), UnifiedGlobalScriptEditTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(GraphicSelectionTests), GraphicSelectionTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(RuntimeSettingsTests), RuntimeSettingsTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(SoftwareContainerLookupTests), SoftwareContainerLookupTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(UnifiedMultilingualTextTests), UnifiedMultilingualTextTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(HmiScreenTraversalTests), HmiScreenTraversalTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(UnifiedUiModelTests), UnifiedUiModelTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(MotionProDiagClassicHmiTests), MotionProDiagClassicHmiTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(UnifiedScreenItemTests), UnifiedScreenItemTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(UnifiedExchangeTests), UnifiedExchangeTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(LibraryWriteSafetyTests), LibraryWriteSafetyTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(SessionAndHardwareRulesTests), SessionAndHardwareRulesTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(BindingAndTagDeletionTests), BindingAndTagDeletionTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(PreflightAndUpdateTests), PreflightAndUpdateTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(CallDisciplineTests), CallDisciplineTests.Run)) yield return row;
            foreach (var row in CheckSuite.Run(nameof(ToolExampleLibraryTests), ToolExampleLibraryTests.Run)) yield return row;
        }
    }
}
