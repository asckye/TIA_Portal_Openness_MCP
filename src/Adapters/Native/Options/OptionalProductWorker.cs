using System;
using Siemens.Engineering.HW.Utilities;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Options;

namespace TiaMcp.Adapters;

public sealed partial class PlcFoundationEngine
{
    internal T OptionalHardwareUtility<T>(string identifier) where T : HardwareUtility => HardwareRequireUtility<T>(identifier);

    public OptionalProductReply ExecuteOptionalProduct(OptionalProductRequest request, bool dryRun = true)
    {
        Check();
        if (request.DryRun != dryRun) throw new ArgumentException("Conflicting optional-product preview mode.");
        var unavailable = OptionalProductCapabilities.Unsupported(ReleaseKey, request.Operation, request.Category, request.Kind, request.Action, request.ExecutionMode, request.Password);
        if (unavailable != null) throw new NotSupportedException(unavailable);
        var session = new OptionalProductSession(this, request);
        try { return ExecuteOptionalProductCore(session, request); }
        catch (Exception error) when (OptionalProductFailures.IsAssemblyLoadFailure(error))
        {
            // Assembly availability belongs to the bound worker session, never the generated catalog.
            return session.RunHmiStepTool(request.Operation, OptionalProductSession.RefuseMissingAssembly);
        }
    }

    private static OptionalProductReply ExecuteOptionalProductCore(OptionalProductSession session, OptionalProductRequest request)
    {
        switch (request.Operation)
        {
#if CFC_CHARTS
            case "ExchangeCfcCharts": return new CfcAdapter(session).ExchangeCfcCharts(request.SoftwarePath, request.Action, request.FilePath, request.ModelVersion, request.Filter, request.Unattended, request.DeleteAtTarget, request.DryRun, request.ChartNamesJson, request.SkipChartPreflight);
            case "ManageCfcChartProtection": return new CfcAdapter(session).ManageCfcChartProtection(request.SoftwarePath, request.ChartName, request.Action, request.CurrentPassword, request.NewHashedPassword, request.DryRun, request.ModelVersion, request.SkipChartPreflight);
#endif
#if OPTION_TEST_SUITE
            case "ListTestSuiteCases": return new TestSuiteAdapter(session).ReadTestSuiteCases(request.Category, request.Name, request.Offset, request.Limit, request.Kind);
            case "ExchangeTestSuiteCase": return new TestSuiteAdapter(session).ExchangeTestSuiteCase(request.Category, request.Action, request.Name, request.FilePath, request.ImportOptions, request.LoadOptions, request.DryRun, request.Kind);
            case "RunTestSuiteCase": return new TestSuiteAdapter(session).RunTestSuiteCase(request.Category, request.Name, request.ConfirmExternalExecution, request.DryRun, request.NamesJson, request.RunAll, request.Kind);
            case "ManageTestSuiteCase": return new TestSuiteAdapter(session).ManageTestSuiteCase(request.Category, request.Name, request.Action, request.Kind, request.NewName, request.SoftwarePath, request.InstanceName, request.ExecutionMode, request.OpcUaServerAddress, request.ServerInterfaceType, request.InterfaceFolderPath, request.UpdateOptions, request.ScopeJson, request.TargetName, request.MasterCopyPath, request.LibraryName, request.DryRun);
#endif
            case "ManageSinumerikArchive": return new OptionalProductsAdapter(session).ManageSinumerikArchive(request.Action, request.FilePath, request.DevicePathJson, request.ItemPathJson, request.ModifiedDevicePathJson, request.ModifiedItemPathJson, request.Mode, request.Comment, request.Author, request.Password, request.DryRun);
            case "ImportSinumerikAlarmTexts": return new OptionalProductsAdapter(session).ImportSinumerikAlarmTexts(request.DevicePathJson, "[]", request.DryRun);
            case "ManageSinumerikSafetyMode": return new OptionalProductsAdapter(session).ManageSinumerikSafetyMode(request.DevicePathJson, request.Action, request.Mode, request.DryRun, request.ConfirmSafetyChange);
            case "InitializeSimotionScripting": return new OptionalProductsAdapter(session).InitializeSimotionScripting(request.DryRun);
            case "ExportScadaData": return new OptionalProductsAdapter(session).ExportScadaData(request.FilePath, request.SoftwarePath, request.DryRun);
            default: throw new ArgumentException("Unregistered optional-product operation: " + request.Operation);
        }
    }
}
