using System.Reflection;
using ModelContextProtocol.Server;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Xunit.Sdk;

[XunitTestCaseDiscoverer("TiaMcp.Engine.Tests.Shared.CheckTheoryDiscoverer", "TiaMcp.FoundationHost.Tests")]
public sealed class CheckTheoryAttribute : TheoryAttribute { }

public sealed class FoundationChecks
{
    [CheckTheory]
    [MemberData(nameof(Rows), DisableDiscoveryEnumeration = true)]
    public void Check(CheckRow row) => row.Verify();

    public static IEnumerable<object[]> Rows()
    {
        // Preserve the original console runner's suite and dispatch order.
        var server=DispatchProxy.Create<IMcpServer,ServerProxy>();
        foreach (var row in CheckSuite.Run(nameof(DisconnectTests), check => DisconnectTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(PassiveHostDiagnosticsTests), check => PassiveHostDiagnosticsTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(ToolUsageTests), check => ToolUsageTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(DocumentImportTests), check => DocumentImportTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(BatchDocumentImportTests), check => BatchDocumentImportTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(WorkerSessionOutcomeTests), check => WorkerSessionOutcomeTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(DocumentExportTests), check => DocumentExportTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(BatchDocumentExportTests), check => BatchDocumentExportTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(ExternalSourcePlanTests), check => ExternalSourcePlanTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(ExternalSourceDeleteTests), check => ExternalSourceDeleteTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(ExternalSourceWorkflowTests), check => ExternalSourceWorkflowTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(AdapterSourceClosureTests), check => AdapterSourceClosureTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(HardwareCatalogDispatchTests), check => HardwareCatalogDispatchTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(DeviceAddDispatchTests), check => DeviceAddDispatchTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(RuntimeQueryTests), check => RuntimeQueryTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(OfflineSymbolManifestTests), check => OfflineSymbolManifestTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(BatchExportDispatchTests), check => BatchExportDispatchTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(SpecialExportDispatchTests), check => SpecialExportDispatchTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(BatchImportDispatchTests), check => BatchImportDispatchTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(OfflineCompositionTests), check => OfflineCompositionTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(OfflineBlockCompositionTests), check => OfflineBlockCompositionTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(OfflineLadderTests), check => OfflineLadderTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(FoundationToolDispatchTests), check => FoundationToolDispatchTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(WorkerClientTests), check => WorkerClientTests.Run(check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(V17ContractTests), check => V17ContractTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(DeclarationReadTests), check => DeclarationReadTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(SoftwareReadDispatchTests), check => SoftwareReadDispatchTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(SupplementaryReadTests), check => SupplementaryReadTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(OfflineXmlBuilderTests), check => OfflineXmlBuilderTests.Run(server,check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(PathAndIdentityTests), check => PathAndIdentityTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(ExchangeContractTests), check => ExchangeContractTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(ExportPublicationTests), (check,skip) => ExportPublicationTests.Run(check,message => skip(message,message)))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(BatchImportTests), (check,skip) => BatchImportTests.Run(check,message => skip(message,message)))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(SpecialExportTests), (check,skip) => SpecialExportTests.Run(check,message => skip(message,message)))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(BatchExportTests), (check,skip) => BatchExportTests.Run(check,message => skip(message,message)))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(AdditionalMutationResultTests), check => AdditionalMutationResultTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(OfflineCoverageBoundaryTests), check => OfflineCoverageBoundaryTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(CompileContractTests), check => CompileContractTests.Run(check))) yield return row;
        foreach (var row in CheckSuite.Run(nameof(LifecycleContractTests), check => LifecycleContractTests.Run(check).GetAwaiter().GetResult())) yield return row;
        foreach (var row in CheckSuite.Run(nameof(BindingSnapshotTests), BindingSnapshotTests.Run)) yield return row;
        foreach (var row in CheckSuite.Run(nameof(ExternalSourcePlanPolicyTests), ExternalSourcePlanPolicyTests.Run)) yield return row;
        foreach (var row in CheckSuite.Run(nameof(ExternalSourceDeletePolicyTests), ExternalSourceDeletePolicyTests.Run)) yield return row;
    }
}
