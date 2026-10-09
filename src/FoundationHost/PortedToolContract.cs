#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
#endif
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
#if TIA_FOUNDATION_TEST_HOST
using enginehost::TiaMcpServer.ModelContextProtocol;
using enginehost::TiaMcpServer.Siemens.Services;
#else
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
#endif

namespace TiaMcp.FoundationHost;

// Declarations, availability and the existing Foundation session lane are shared
// by every port. Only the typed service factory is specific to a family.
internal static class PortedToolContract
{
    // V20/V21 B1 declarations belong to the full EngineHost catalog. The
    // bounded plc-foundation profile includes F18/F19/F20/F21 hardware ports.
    internal static IEnumerable<PortedFamilies.Family> Families(string release) =>
        PortedFamilies.All.Where(family => (family.Name == "F18" || family.Name == "F19" || family.Name == "F20" || family.Name == "F21") && family.Available(release));
    internal static McpServerTool Wrap(FoundationTool tool) => tool.CapabilityAdmission(
        McpServer.WrapTools(new[] { tool.ValidatedTool() }).Single());

    // The primary module operation comes first; previews and fallback reads are
    // also part of the wiring. Pure builders do not read session state.
    internal static string[] WiredOperations(string tool) => tool switch
    {
        "GetDeviceAddressing" => new[] { "hardware-addressing.ReadHardwareAddressing", "ReadState" },
        "GetDeviceIpAddress" => new[] { "hardware-addressing.ReadHardwareIpAddress", "ReadState" },
        "GetDeviceItemIoAddresses" => new[] { "hardware-addressing.ReadHardwareIoAddresses", "hardware-addressing.DescribeHardwareIoChildren", "ReadState" },
        "SetDeviceAddress" => new[] { "hardware-addressing.UpdateHardwareAddress", "ReadState" },
        "SetDeviceItemIoAddress" => new[] { "hardware-addressing.SetHardwareIoAddress", "hardware-addressing.ReadHardwareIoAddresses", "ReadState" },
        "ExportDeviceAml" => new[] { "hardware-aml.ExportHardwareAml", "ReadState" },
        "ImportDeviceAml" => new[] { "hardware-aml.ImportHardwareAml", "ReadState" },
        "BuildDeviceAmlDocument" => Array.Empty<string>(),
        "CreateDevice" => new[] { "hardware-devices.HardwareCreateDevice", "ReadState" },
        "CreateGsdDevice" => new[] { "hardware-devices.HardwareCreateGsdDevice", "ReadState" },
        "CreateHardwareCatalogDevice" => new[] { "hardware-devices.HardwareCreateCatalogDevice", "ReadState" },
        "ListDevices" => new[] { "hardware-devices.HardwareDescribeDevices", "hardware-devices.HardwareReadPlcNames", "ReadState" },
        "GetDeviceInfo" => new[] { "hardware-devices.HardwareDescribeDeviceAt", "ReadState" },
        "GetDeviceItemInfo" => new[] { "hardware-devices.HardwareDescribeItemAt", "ReadState" },
        "GetDeviceItemTree" => new[] { "hardware-devices.HardwareReadItemTree", "ReadState" },
        "SearchInstalledGsdDevices" => new[] { "hardware-devices.HardwareLegacySearchInstalledGsdDevices", "ReadState" },
        "GetDeviceAttributes" => new[] { "hardware-devices.HardwareLegacyDumpDeviceAttributes", "ReadState" },
        "SetDeviceItemAttribute" => new[] { "hardware-devices.HardwareLegacySetDeviceItemAttribute", "ReadState" },
        "SetPlcCpuSettings" => new[] { "hardware-devices.HardwareLegacySetCpuCommonSettings", "ReadState" },
        "GetDevicePlugLocations" => new[] { "hardware-devices.HardwareReadPlugLocations", "ReadState" },
        "PlugDeviceItem" => new[] { "hardware-devices.HardwarePlugModule", "ReadState" },
        "ManageHardwareObject" => new[] { "hardware-devices.HardwareManageHardwareObject", "ReadState" },
        "ManageDeviceUserGroup" => new[] { "hardware-devices.HardwareManageDeviceUserGroup", "ReadState" },
        "GetHardwareFeatures" => new[] { "hardware-devices.HardwareReadHardwareFeatures", "ReadState" },
        "ManageDeviceServiceObjects" => new[] { "hardware-devices.HardwareManageDeviceServiceObjects", "ReadState" },
        "ManageHardwareUtilities" => new[] { "hardware-devices.HardwareManageHardwareUtilities", "ReadState" },
        "ExchangeSystemDiagnosticsSettings" => new[] { "hardware-devices.HardwareExchangeSystemDiagnosticsSettings", "ReadState" },
        "ListIoSystems" => new[] { "hardware-network.HardwareReadIoSystems", "ReadState" },
        "ManageIoSystem" => new[] { "hardware-network.HardwareManageIoSystem", "ReadState" },
        "ListNetworkDomains" => new[] { "hardware-network.HardwareReadNetworkDomains", "ReadState" },
        "ManageNetworkDomain" => new[] { "hardware-network.HardwareManageNetworkDomain", "ReadState" },
        "ListTransferAreas" => new[] { "hardware-network.HardwareReadTransferAreas", "ReadState" },
        "ManageTransferArea" => new[] { "hardware-network.HardwareManageTransferArea", "ReadState" },
        "ListDeviceItemChannels" => new[] { "hardware-network.HardwareReadDeviceItemChannels", "ReadState" },
        "SetDeviceItemChannel" => new[] { "hardware-network.HardwareUpdateDeviceItemChannel", "ReadState" },
        "ManagePortInterconnection" => new[] { "hardware-network.HardwareManagePortInterconnection", "ReadState" },
        "GetDeviceItemNetworkInfo" => new[] { "hardware-network.HardwareGetDeviceItemNetworkInfo", "ReadState" },
        "ConnectDeviceNodesToProfinetSubnet" => new[] { "hardware-network.HardwareProbeConnectDeviceNodesToSubnet", "ReadState" },
        "PlanHardwareNetworkConfiguration" => Array.Empty<string>(),
        "EnsureSubnet" => new[] { "hardware-network.HardwareEnsureSubnet", "ReadState" },
        "AttachDeviceNodeToSubnet" => new[] { "hardware-network.HardwareAttachDeviceNodeToSubnet", "ReadState" },
        "ProbeHardwareHmiConnectionOwnerCandidates" => new[] { "hardware-network.ReadHardwareOwnerCandidates", "ReadState" },
        "ProbeHardwareHmiConnectionWhitelistedServices" => new[] { "hardware-network.ReadHardwareWhitelistedServices", "ReadState" },
        "GetProjectTopology" => new[] { "hardware-network.HardwareGetProjectTopology", "ReadState" },
        "ListCommunicationConnections" => new[] { "hardware-network.HardwareReadCommunicationConnections", "ReadState" },
        "ManageCommunicationConnection" => new[] { "hardware-network.HardwareManageCommunicationConnection", "ReadState" },
        _ when PortedFamilies.Contains(tool) && PortedFamilies.ForTool(tool).Name is "F01" or "F02" or "F03" => Array.Empty<string>(),
        _ => throw new NotSupportedException("Unregistered ported tool: " + tool)
    };

    internal static IEnumerable<McpServerTool> Create(IFoundationWorker worker, string release)
    {
        foreach (var method in PortedToolDeclarations.Methods(release))
        {
            string name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
            if (Families(release).Any(family => family.Tools.Contains(name, StringComparer.Ordinal))) yield return new FoundationTool(worker, release, method, name);
        }
    }
}

internal sealed partial class FoundationTool
{
    private static readonly ConditionalWeakTable<IFoundationWorker, DeviceCreationSession> DeviceCandidates = new();
    private readonly MethodInfo? portedMethod;
    private readonly string? portedRelease;
    private readonly ToolDescriptor? portedDescriptor;
    internal bool Ported => portedMethod != null;

    internal FoundationTool(IFoundationWorker worker, string release, MethodInfo method, string name)
    {
        this.worker = worker; portedMethod = method; portedRelease = release;
        var declaration = DeclaredToolMetadata.Create(name, method, _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
        portedDescriptor = descriptor; tool = declaration.ProtocolTool;
        definition = new Definition(name, PortedFamilies.ForTool(name).OperationPrefix, descriptor.RawDescription,
            descriptor.Parameters.Select(p => new Argument(p.Name, p.FriendlyType, p.Required)).ToArray(), PortedFamilies.ForTool(name).Name);
    }

    internal McpServerTool ValidatedTool() => new InfrastructureInputTool(this, portedDescriptor!);
    internal McpServerTool CapabilityAdmission(McpServerTool inner) => new CapabilityTool(this, inner);

    private sealed class CapabilityTool(FoundationTool owner, McpServerTool inner) : McpServerTool
    {
        public override Tool ProtocolTool => inner.ProtocolTool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
            // Invalid input still goes through the existing wrapper so its
            // behaviour disclosure and raw rejection payload remain unchanged.
            var input = JsonSerializer.SerializeToElement(arguments);
            if (McpServer.ToolInvoker.ValidateArguments(owner.portedDescriptor!, input, ProtocolTool.InputSchema) == null
                && owner.RefusePortedCapability(arguments) is { } refusal)
            {
                McpServer.RecordAdmissionProjection(request, refusal);
                return new ValueTask<CallToolResult>(refusal);
            }
            return inner.InvokeAsync(request, cancellationToken);
        }
    }

    internal CallToolResult? RefusePortedCapability(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        using var release = HardwareContract.UseRelease(portedRelease!);
        var family = PortedFamilies.ForTool(tool.Name);
        if (!PortedFamilies.Available(portedRelease!, tool.Name) || arguments.TryGetValue("action", out var action)
            && !family.SupportsAction(portedRelease!, action.GetString()!))
            return HardwareContract.Run(tool.Name, () => throw new NotSupportedException("Tool is unavailable in this release."), false, true);
        string actionName = arguments.TryGetValue("action", out var requestedAction) ? requestedAction.GetString() ?? "" : "";
        string scope = arguments.TryGetValue("family", out var requestedFamily) ? requestedFamily.GetString() ?? "" : arguments.TryGetValue("kind", out var requestedKind) ? requestedKind.GetString() ?? "" : "";
        if (HardwareCapabilities.Unsupported(portedRelease!, tool.Name, actionName, scope,
            arguments.TryGetValue("positionNumber", out var position) ? position.GetInt32() : -1,
            arguments.TryGetValue("extendedPositionNumber", out var extendedPosition) ? extendedPosition.GetInt32() : -1,
            arguments.TryGetValue("password", out var password) && !string.IsNullOrEmpty(password.GetString())) != null)
            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        return null;
    }

    private async ValueTask<CallToolResult> InvokePortedAsync(RequestContext<CallToolRequestParams> request, CancellationToken token)
    {
        var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        using var release = HardwareContract.UseRelease(portedRelease!);
        if (RefusePortedCapability(arguments) is { } refusal) return refusal;
        // ReadState is managed lifecycle data, never an Openness lookup. Keep it in
        // the same lane as the eventual request so approval cannot race a rebind.
        using var lane = await AcquireLane(token);
        ActivateLane(lane);
        var state = (tool.Name == "BuildDeviceAmlDocument" || tool.Name == "PlanHardwareNetworkConfiguration") ? null : await worker.Call("ReadState", new JsonObject(), token);
        var service = new HardwareAddressingService((operation, values) => worker.Call(operation, values, token).GetAwaiter().GetResult(),
            () => !string.IsNullOrEmpty((string?)state?["ProjectFile"]), () => (string?)state?["ProjectFile"] ?? "");
        Func<string, JsonObject, JsonNode?> call = (operation, input) => worker.Call(operation, input, token).GetAwaiter().GetResult();
        Func<bool> hasProject = () => !string.IsNullOrEmpty((string?)state?["ProjectFile"]);
        Func<string> identity = () => (string?)state?["ProjectFile"] ?? "";
        object target = portedMethod!.DeclaringType!.Name switch
        {
            "HardwareDevicesTools" => new HardwareDevicesTools(new HardwareDevicesService(call, hasProject, identity,
                DeviceCandidates.GetValue(worker, _ => new DeviceCreationSession()))),
            "ModulesTools" => new ModulesTools(new HardwareModulesService(call, hasProject, identity)),
            "HardwareManagementTools" => new HardwareManagementTools(new HardwareManagementService(call, hasProject, identity)),
            "HardwareNetworkTools" => new HardwareNetworkTools(new HardwareNetworkPortService(call, hasProject, identity)),
            "HardwareServicesPortTools" => new HardwareServicesPortTools(new HardwareServicesPortService(call, hasProject, identity)),
            "HardwareAmlTools" => new HardwareAmlTools(new HardwareAmlService(call, hasProject, identity)),
            _ => new AddressesTools(service)
        };
        var values = portedMethod!.GetParameters().Select(p => arguments.TryGetValue(p.Name!, out var value)
            ? JsonSerializer.Deserialize(value.GetRawText(), p.ParameterType, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false }) : p.DefaultValue).ToArray();
        try { return (CallToolResult)portedMethod.Invoke(target, values)!; }
        catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
    }
}
