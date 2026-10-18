using TiaMcpServer;
// Response boundary stand-ins. The catalog and bridge use the real MCP SDK attributes.
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaMcpServer.ModelContextProtocol;
using TiaMcp.Logic.V4;
using Xunit;

namespace TiaMcpServer.Siemens
{
    // Registration-only session boundary; no Siemens SDK types enter the offline suite.
    internal partial interface IEngineeringSession { }
    internal interface IHmiToolSession { }
    // Kept only for the linked EngineRegistration and maintenance root-lookup checks.
    // No Portal implementation files are compiled into this suite.
    internal sealed partial class Portal : IEngineeringSession, IHmiToolSession
    {
        internal (bool IsConnected, string? Project)? GetState()
            => throw new InvalidOperationException("The layout fixture must not read a session.");
    }
}

namespace TiaMcpServer.ModelContextProtocol
{

    public class ResponseMessage { public string? Message { get; set; } public JsonObject? Meta { get; set; } }
    public class ResponseStringList : ResponseMessage { public IEnumerable<string>? Items { get; set; } }
    // Explicit fixture catalogs load these probes; assembly discovery loads the real optional-product tools.
    public static class DcbVersionProbe
    {
        [McpServerTool(Name = "ManageDcbLibraries")]
        public static CallToolResult ProbeVersionedDcb(string action = "read", string[] devicePath = null!,
            string[] itemPath = null!, ushort driveObjectNumber = 0, int driveObjectIndex = -1,
            string filePath = "", bool dryRun = true)
        { ToolBridgeProbes.VersionProbeCalls++; return ToolBridgeProbes.ProbeResult(true); }
        [global::ModelContextProtocol.Server.McpServerTool(Name = "ListSafetyActivationTests")]
        public static CallToolResult ProbeVersionedSafety()
        { ToolBridgeProbes.VersionProbeCalls++; return ToolBridgeProbes.ProbeResult(true); }
    }
    [McpServerToolType]
    public static class ToolBridgeProbes
    {
        internal static int VersionProbeCalls;
        // Application-boundary fixtures, not fabricated Siemens SDK classes.
        [global::ModelContextProtocol.Server.McpServerTool]
        public static CallToolResult ProbeResult(bool success) => success
            ? McpServer.V4Result("ProbeResult", new JsonObject { ["summary"] = "probe" })
            : McpServer.V4Reject("ProbeResult", new Error("Fixture rejected the request.", new PreconditionFailedDetails("fixture", null)));
        [global::ModelContextProtocol.Server.McpServerTool]
        public static ResponseMessage ProbeThrow() => throw new InvalidOperationException("probe exception");
        [McpServerTool(Name = "ProbeMcpThrow"), ToolClassification("L2", "Diagnostics", "READ", batchRead: true), Description("[L2][Diagnostics][READ] Offline throwing read fixture.")]
        public static CallToolResult ProbeMcpThrow() => throw new global::ModelContextProtocol.McpException("SECRET-probe-argument", global::ModelContextProtocol.McpErrorCode.InternalError);
        [McpServerTool(Name = "ProbeWriteThrow"), ToolClassification("L2", "Diagnostics", "WRITE", batchWrite: true), Description("[L2][Diagnostics][WRITE] Offline throwing write fixture.")]
        public static CallToolResult ProbeWriteThrow() => throw new InvalidOperationException("SECRET-probe-argument");
        [McpServerTool, ToolClassification("L0", "Portal", "SESSION")]
        public static CallToolResult ProbeSessionThrow() => throw new global::ModelContextProtocol.McpException("SECRET-probe-argument", global::ModelContextProtocol.McpErrorCode.InternalError);
        [McpServerTool, ToolClassification("L2", "Hardware", "EXECUTE")]
        public static CallToolResult ProbeExecuteThrow() => throw new InvalidOperationException("SECRET-probe-argument");
        [McpServerTool, ToolClassification("L2", "Exports", "FILE")]
        public static CallToolResult ProbeFileThrow() => throw new InvalidOperationException("SECRET-probe-argument");
        [McpServerTool, ToolClassification("L1", "PLC-Online", "ONLINE")]
        public static CallToolResult ProbeOnlineThrow() => throw new InvalidOperationException("SECRET-probe-argument");
        public sealed class Cycle { public Cycle Self => this; }
        [global::ModelContextProtocol.Server.McpServerTool]
        public static Cycle ProbeCycle() => new Cycle();
    }
}

namespace TiaMcp.Engine.Tests
{
    internal static class ToolBridgeFixture
    {
        internal static readonly ToolCatalog Catalog = new ToolCatalog(new[] { typeof(McpServer), typeof(ToolBridgeProbes), typeof(DcbVersionProbe), typeof(InstanceProbeTools) });

        internal static void Configure(bool lite = false)
        {
            McpServer.ConfigureToolBridge(Catalog, () => lite, new HashSet<string>(StringComparer.Ordinal) { "ProbeResult" });
            EngineServices.SetServiceProvider(new ServiceCollection()
                .AddSingleton(new ProbeDependency("root"))





                .AddSingleton<TiaMcpServer.Siemens.Services.DeviceUserPortService>()
                .AddSingleton<TiaMcpServer.Siemens.Services.HardwareSecurityPortService>()
                .AddEngine(includeSession: false, Catalog).BuildServiceProvider());
        }
    }

    public sealed class ProbeDependency
    {
        public string Value { get; }
        public ProbeDependency(string value) { Value = value; }
    }

    [McpServerToolType]
    public sealed class InstanceProbeTools
    {
        private readonly ProbeDependency dependency;
        internal int Calls;
        public InstanceProbeTools(ProbeDependency dependency) { this.dependency = dependency; }

        [McpServerTool(Name = "InstanceProbe"), Description("[L0][Meta][READ] Read the injected probe dependency.")]
        public CallToolResult Read(string suffix = "default")
        {
            Calls++;
            return McpServer.V4Result("InstanceProbe", new JsonObject { ["summary"] = dependency.Value + ":" + suffix });
        }
    }

    public class ToolCatalogServerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }

}
