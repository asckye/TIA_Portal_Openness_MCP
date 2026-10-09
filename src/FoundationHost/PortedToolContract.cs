#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
#endif
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
#if TIA_FOUNDATION_TEST_HOST
using enginehost::TiaMcpServer.ModelContextProtocol;
#else
using TiaMcpServer.ModelContextProtocol;
#endif

namespace TiaMcp.FoundationHost;

// Declarations, availability and the existing Foundation session lane are shared
// by every port. Only the typed service factory is specific to a family.
internal static class PortedToolContract
{
    internal static McpServerTool Wrap(FoundationTool tool) => McpServer.WrapTools(new[] { tool.ValidatedTool() }).Single();

    // The primary module operation comes first; previews and fallback reads are
    // also part of the wiring. Every invocation reads managed session state.
    internal static string[] WiredOperations(string tool) => tool switch
    {
        "GetDeviceAddressing" => new[] { "hardware-addressing.ReadHardwareAddressing", "ReadState" },
        "GetDeviceIpAddress" => new[] { "hardware-addressing.ReadHardwareIpAddress", "ReadState" },
        "GetDeviceItemIoAddresses" => new[] { "hardware-addressing.ReadHardwareIoAddresses", "hardware-addressing.DescribeHardwareIoChildren", "ReadState" },
        "SetDeviceAddress" => new[] { "hardware-addressing.UpdateHardwareAddress", "ReadState" },
        "SetDeviceItemIoAddress" => new[] { "hardware-addressing.SetHardwareIoAddress", "hardware-addressing.ReadHardwareIoAddresses", "ReadState" },
        _ => throw new NotSupportedException("Unregistered ported tool: " + tool)
    };

    internal static IEnumerable<McpServerTool> Create(IFoundationWorker worker, string release)
    {
        foreach (var method in PortedToolDeclarations.Methods(release))
        {
            string name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
            if (PortedFamilies.Available(release, name)) yield return new FoundationTool(worker, release, method, name);
        }
    }
}

internal sealed partial class FoundationTool
{
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
            descriptor.Parameters.Select(p => new Argument(p.Name, p.FriendlyType, p.Required)).ToArray(), "HardwareAddressing");
    }

    internal McpServerTool ValidatedTool() => new InfrastructureInputTool(this, portedDescriptor!);

    private async ValueTask<CallToolResult> InvokePortedAsync(RequestContext<CallToolRequestParams> request, CancellationToken token)
    {
        var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        using var release = HardwareContract.UseRelease(portedRelease!);
        var family = PortedFamilies.ForTool(tool.Name);
        if (!family.Available(portedRelease!) || arguments.TryGetValue("action", out var action)
            && !family.SupportsAction(portedRelease!, action.GetString()!))
            return HardwareContract.Run(tool.Name, () => throw new NotSupportedException("Tool is unavailable in this release."), false, true);
        // ReadState is managed lifecycle data, never an Openness lookup. Keep it in
        // the same lane as the eventual request so approval cannot race a rebind.
        using var lane = await AcquireLane(token);
        ActivateLane(lane);
        var state = await worker.Call("ReadState", new JsonObject(), token);
        var service = new HardwareAddressingService((operation, values) => worker.Call(operation, values, token).GetAwaiter().GetResult(),
            () => !string.IsNullOrEmpty((string?)state?["ProjectFile"]), () => (string?)state?["ProjectFile"] ?? "");
        var target = new AddressesTools(service);
        var values = portedMethod!.GetParameters().Select(p => arguments.TryGetValue(p.Name!, out var value)
            ? JsonSerializer.Deserialize(value.GetRawText(), p.ParameterType, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false }) : p.DefaultValue).ToArray();
        try { return (CallToolResult)portedMethod.Invoke(target, values)!; }
        catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
    }
}
