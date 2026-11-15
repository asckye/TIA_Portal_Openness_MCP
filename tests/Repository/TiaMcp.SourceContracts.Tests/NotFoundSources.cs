using TiaMcp.BuildCommon;
using Xunit;

namespace TiaMcp.SourceContracts.Tests;

public sealed class NotFoundSources
{
    [Theory]
    [InlineData("Plc/PlcExchangePolicy.cs", "AdapterPreconditionException.NotFound(\"Object not found")]
    [InlineData("Plc/PlcExchangePolicy.cs", "AdapterPreconditionException.NotFound(\"Tag table not found")]
    [InlineData("Plc/PlcFoundationPolicy.cs", "AdapterPreconditionException.NotFound(\"Object not found")]
    [InlineData("Plc/PlcReadPathPolicy.cs", "AdapterPreconditionException.NotFound(\"PLC software not found")]
    [InlineData("Plc/PlcExternalSourceWorkflow.cs", "AdapterPreconditionException.NotFound(\"External source not found")]
    [InlineData("Plc/PlcUnitScope.cs", "AdapterPreconditionException.NotFound(\"Exact software unit not found")]
    [InlineData("Plc/PlcSpecialExport.cs", "AdapterPreconditionException.NotFound(\"Exact canonical technology-object path not found")]
    [InlineData("Plc/PlcSpecialExport.cs", "AdapterPreconditionException.NotFound(\"Exact canonical watch-table path not found")]
    [InlineData("Hmi/UnifiedObjectServicesAdapter.cs", "PlcSoftwareException(\"NotFound\", \"Exact list not found.")]
    [InlineData("Hmi/UnifiedObjectServicesAdapter.cs", "PlcSoftwareException(\"NotFound\", \"Exact HMI connection not found.")]
    [InlineData("Hmi/UnifiedObjectServicesAdapter.cs", "PlcSoftwareException(\"NotFound\", \"Exact plant view not found.")]
    [InlineData("Hmi/UnifiedUiModelAdapter.cs", "PlcSoftwareException(\"NotFound\", \"Exact list not found: ")]
    [InlineData("Plc/PlcTechnology.cs", "PlcSoftwareException(\"NotFound\", \"Exact technology object not found.")]
    [InlineData("Plc/PlcTechnologyEvidence.cs", "PlcSoftwareException(\"NotFound\", \"Exact technology object not found.")]
    [InlineData("Libraries/LibraryService.cs", "PlcSoftwareException(\"NotFound\", \"Exact library type not found.")]
    [InlineData("Hardware/HardwareServices.cs", "HardwareAddressingException(\"NotFound\", \"Node not found on interface: ")]
    [InlineData("Hardware/HardwareAddressingSteps.cs", "?? throw Native.Hardware.ProjectDeviceWalk.MissingDevice(")]
    [InlineData("Hardware/ProjectDeviceWalk.cs", "internal static HardwareAddressingException MissingDevice(")]
    [InlineData("Hardware/ProjectDeviceWalk.cs", "var error = new HardwareAddressingException(\"NotFound\",")]
    [InlineData("Hardware/ProjectDeviceWalk.cs", "error.Data[\"candidates\"] = candidates;\n            return error;")]
    [InlineData("Hardware/HardwareAddressingSteps.cs", "HardwareAddressingException(\"NotFound\", \"Device item not found: ")]
    [InlineData("Hmi/UnifiedEngineeringAdapter.cs", "PlcSoftwareException(\"NotFound\", \"Exact object not found: ")]
    [InlineData("Hmi/HmiDynamic.cs", "PlcSoftwareException(\"NotFound\", \"Exact entry not found: ")]
    [InlineData("Engineering/EngineeringServicesWorker.cs", "PlcSoftwareException(\"NotFound\", \"Exact open global library not found.")]
    [InlineData("Hmi/UnifiedHmiAdapter.cs", "PlcSoftwareException(\"NotFound\", $\"Screen item '")]
    [InlineData("Hardware/HardwareLegacyWrites.cs", "meta[\"status\"] = \"NotFound\"; meta[\"parameter\"] = \"cpuPath\";")]
    [InlineData("Plc/PlcWatchTables.Online.cs", "[\"parameter\"] = \"watchTableName\"")]
    [InlineData("Plc/EmptyPlcGroupPolicy.cs", "\"groupPath\", isArgument: false")]
    [InlineData("Collaboration/TeamcenterAdapter.cs", "\"teamcenter-connection\", null")]
    [InlineData("Session/ProjectArchive.cs", "\"unsaved-changes\", null")]
    [InlineData("Libraries/LibraryService.Global.cs", "PlcSoftwareException(\"AlreadyExists\", \"Library with this name already open.")]
    public void Vm_sample_sites_use_typed_refusals(string path, string expected)
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        Assert.Contains(expected, File.ReadAllText(Path.Combine(root, "src/Adapters/Native", path)), StringComparison.Ordinal);
    }

    [Fact]
    public void Simulation_runtime_missing_instance_is_not_found()
    {
        string root = Repository.FindRoot(AppContext.BaseDirectory);
        string source = File.ReadAllText(Path.Combine(root, "src/OnlineChannels/Simulation/PlcSimAdvancedAdapter.cs"));
        Assert.Contains("cause.GetType().Name == \"SimulationRuntimeException\"", source, StringComparison.Ordinal);
        Assert.Contains("cause.Message.Contains(\"DoesNotExist\", StringComparison.Ordinal)", source, StringComparison.Ordinal);
        Assert.Contains("? \"NOT_FOUND\" : \"ReadOrWriteFailed\"", source, StringComparison.Ordinal);
    }
}
