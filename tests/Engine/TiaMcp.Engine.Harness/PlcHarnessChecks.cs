using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiaMcp.Adapters.Contracts;

// The entry selects only kept worker/adapter code. The Python driver owns the
// FoundationHost and forwards calls through its shared SDK-ready server helper.
internal static class PlcHarnessChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static bool Active;
    internal static string Release = "";
    internal static string Diagnostics = "";
    internal static Assembly Adapter = null!;
    // The kept net48 Logic build (plc-logic/, see the project file); the engine entry never loads it.
    internal static Assembly Logic = null!;
    internal static string LogicDirectory => Path.Combine(AppContext.BaseDirectory, "plc-logic");
    internal static JsonObject Metadata = null!;
    private static object codecs = null!;

    internal static JsonObject HostCall(string command, JsonObject arguments)
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("TIA_MCP_PLC_HARNESS_RPC")
            ?? throw new InvalidOperationException("PLC harness requires its owned host bridge."));
        if (endpoint.Host != "127.0.0.1") throw new InvalidOperationException("Harness bridge must use IPv4 loopback.");
        using var client = new WebClient { Encoding = new UTF8Encoding(false) };
        client.Headers[HttpRequestHeader.ContentType] = "application/json";
        var result = JsonNode.Parse(client.UploadString(endpoint, new JsonObject {
            ["command"] = command, ["arguments"] = arguments.DeepClone()
        }.ToJsonString()))!.AsObject();
        if (result["exception"] is JsonNode error)
            throw new TargetInvocationException(new ArgumentException((string)error!));
        return result;
    }

    internal static bool Registered(string name, bool preview = false, bool token = false)
    {
        var tool = Metadata[name];
        if ((bool?)tool?["registered"] != true) return false;
        var parameters = tool!["parameters"]!.AsArray();
        return (!preview || parameters.Any(p => (string?)p!["name"] == "dryRun" && (bool?)p["default"] == true))
            && (!token || parameters.Any(p => (string?)p!["name"] == "previewToken" && (string?)p["default"] == ""));
    }

    internal static Type Helper(string name) => Adapter.GetType("TiaMcp.Adapters.Native.Hmi." + name, true)!;

    internal static IDisposable Configure(Assembly worker, string api)
    {
        var type = Adapter.GetType("TiaMcp.Adapters.PlcFoundationEngine", true)!;
        codecs = Activator.CreateInstance(type, new object[] { Release, api })!;
        // The engine delegates the PlcWorker's Program.Run sets before dispatching.
        type.GetProperty("OrganisationEvidenceJson", All)!.SetValue(codecs,
            new Func<Dictionary<string, object?>, string>(evidence => JsonSerializer.Serialize(evidence)));
        type.GetProperty("UnifiedPropertyAdmission", All)!.SetValue(codecs, Delegate.CreateDelegate(
            type.GetProperty("UnifiedPropertyAdmission", All)!.PropertyType,
            worker.GetType("TiaMcp.Shared.UnifiedHmiPropertyAdmission", true)!.GetMethod("Validate", All)!));
        worker.GetType("TiaMcpServer.Siemens.Services.UnifiedWorkerCodecs", true)!.GetMethod("Configure", All)!.Invoke(null, new[] { codecs });
        Helper("HmiValues").GetField("Renderer", All)!.SetValue(null,
            new Func<Dictionary<string, object?>, string>(value => JsonSerializer.Serialize(value)));
        Helper("UnifiedCodecs").GetField("Current", All)!.SetValue(null, codecs);
        Helper("UnifiedRequestValues").GetField("Major", All)!.SetValue(null, int.Parse(Release));
        Helper("UnifiedRequestValues").GetField("Decoder", All)!.SetValue(null,
            new Func<string, UnifiedValue>(value => JsonSerializer.Deserialize<UnifiedValue>(HostCall("Decode", new JsonObject { ["value"] = value })["value"]!.ToJsonString())!));
        Helper("HmiExactAccess").GetField("ContentToken", All)!.SetValue(null,
            Delegate.CreateDelegate(typeof(Func<string, Dictionary<string, object?>, string>), codecs, type.GetMethod("UnifiedHmiToken", All)!));
        return (IDisposable)codecs;
    }
}

internal static partial class Program
{
    // The harness application base has no System.Text.Json; the resolver must be registered before the JIT
    // compiles a method that uses JSON types, so the checks run in a separate method.
    private static Task<int> RunPlcHarness(string[] args)
    {
        if (args.Length != 7) throw new ArgumentException("Expected --plc worker mode public-api metadata.json diagnostics release.");
        string worker = Path.GetFullPath(args[1]), api = Path.GetFullPath(args[3]);
        string logic = Path.Combine(AppContext.BaseDirectory, "plc-logic");
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) => {
            string name = new AssemblyName(request.Name).Name + ".dll";
            foreach (string directory in new[] { Path.GetDirectoryName(worker)!, api, AppContext.BaseDirectory, logic }) {
                string dependency = Path.Combine(directory, name);
                if (File.Exists(dependency)) return Assembly.LoadFrom(dependency);
            }
            return null;
        };
        return RunPlcHarnessChecks(args, worker, api);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<int> RunPlcHarnessChecks(string[] args, string worker, string api)
    {
        PlcHarnessChecks.Active = true;
        PlcHarnessChecks.Release = args[6];
        PlcHarnessChecks.Diagnostics = Path.GetFullPath(args[5]);
        PlcHarnessChecks.Metadata = JsonNode.Parse(File.ReadAllText(args[4]))!.AsObject();
        Server = Assembly.LoadFrom(worker);
        PlcHarnessChecks.Logic = Assembly.LoadFrom(Path.Combine(PlcHarnessChecks.LogicDirectory, "TiaMcp.Logic.dll"));
        string key = Server.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TiaReleaseKey").Value!;
        Check(key == PlcHarnessChecks.Release, "PLC fixture release differs from the selected host");
        PlcHarnessChecks.Adapter = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(worker)!, "TiaMcp.Adapter." + key + ".dll"));
        void Count(bool ok, string message) { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); }
        string mode = args[2];
        if (mode == "process-leases-only") { await ProcessLeaseTests(); return 0; }
        if (mode == "test-download-route") return DeveloperChecks.DownloadRoute(Server, api);
        if (mode == "test-match-plc-name") return DeveloperChecks.MatchPlcName(Server);
        if (mode == "test-migration-read-assembly") return DeveloperChecks.MigrationReadAssembly(Server, api);
        if (mode == "test-ecosystem-assembly") return DeveloperChecks.EcosystemAssembly(Server, api, Array.Empty<string>());
        if (mode == "software-lookup-only") {
            PlcSoftwareLookupChecks.Run(PlcHarnessChecks.Adapter, Count);
            Console.WriteLine("COMPLETE: " + Passed + " software lookup checks passed"); return 0;
        }
        if (mode == "native-diagnostics-only") {
            PlcDiagnosticsChecks.Run(PlcHarnessChecks.Adapter,
                Environment.GetEnvironmentVariable("TIA_MCP_PLC_HARNESS_INVENTORY") ?? throw new ArgumentException("Adapter inventory is required"), Count);
            return 0;
        }
        using var configured = PlcHarnessChecks.Configure(Server, api);
        switch (mode)
        {
            case "hmi-snapshot-only":
                HmiSnapshotRemotingTests.Run(PlcHarnessChecks.Helper("HmiSnapshot").GetMethod("Capture", All)!, Count, UnifiedNativeFixture.Json);
                Console.WriteLine("COMPLETE: " + Passed + " HMI snapshot remoting checks passed"); break;
            case "native-export-only":
                NativeExportRemotingTests.Run(Server, Count);
                Console.WriteLine("COMPLETE: " + Passed + " native export remoting checks passed"); break;
            case "global-script-only":
                GlobalScriptBridgeTests.Run(PlcHarnessChecks.Adapter, PlcHarnessChecks.Adapter, PlcHarnessChecks.Helper("UnifiedScriptAccess"),
                    () => PlcHarnessChecks.Registered("SetUnifiedGlobalScript", preview: true, token: true), Count);
                Console.WriteLine("COMPLETE: " + Passed + " global script bridge checks passed"); break;
            case "graphic-selection-only":
                GraphicSelectionRuntimeTests.Run(PlcHarnessChecks.Helper("UnifiedGraphicSelection"), (left, right) =>
                    PlcHarnessChecks.HostCall("Compare", new JsonObject { ["left"] = left, ["right"] = right })["value"]!,
                    () => PlcHarnessChecks.Registered("GetUnifiedGraphicSelection") && PlcHarnessChecks.Registered("CompareUnifiedGraphicSelections"), Count);
                Console.WriteLine("COMPLETE: " + Passed + " graphical selection checks passed"); break;
            case "runtime-settings-only":
                RuntimeSettingsRuntimeTests.Run(PlcHarnessChecks.Adapter, PlcHarnessChecks.Helper("UnifiedRuntimeSettingsAccess"),
                    () => PlcHarnessChecks.Registered("GetUnifiedRuntimeSettings") && PlcHarnessChecks.Registered("SetUnifiedRuntimeSettings", preview: true), Count);
                Console.WriteLine("COMPLETE: " + Passed + " runtime settings checks passed"); break;
            case "hmi-only":
                var references = PlcHarnessChecks.Adapter.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)).ToArray();
                Count(references.Length > 0 && references.All(a => a.Version!.Major.ToString() == key), "Adapter uses only the selected Siemens major");
                // The engine half checks its fixed file version against Version.props; the worker and its adapter carry
                // the same release file version as the FoundationHost that started this run.
                string release = (string)System.Xml.Linq.XDocument.Load(Path.Combine(HostPortRunner.RepositoryRoot(), "Version.props"))
                    .Root!.Element("PropertyGroup")!.Element("TiaMcpRelease")! + ".0";
                string hostPath = Environment.GetEnvironmentVariable("TIA_MCP_PLC_HARNESS_HOST") ?? throw new ArgumentException("The FoundationHost path is required");
                string hostDll = Path.ChangeExtension(hostPath, ".dll");
                string? hostVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(File.Exists(hostDll) ? hostDll : hostPath).FileVersion;
                Count(hostVersion == release, "FoundationHost has the release file version " + release);
                Count(System.Diagnostics.FileVersionInfo.GetVersionInfo(Server.Location).FileVersion == hostVersion, "Worker has the host's release file version");
                Count(System.Diagnostics.FileVersionInfo.GetVersionInfo(PlcHarnessChecks.Adapter.Location).FileVersion == hostVersion, "Adapter has the host's release file version");
                int identity = Passed;
                TiaMcpServer.Siemens.HmiScreenTraversal.EngineType = PlcHarnessChecks.Helper("HmiScreenTraversal");
                TiaMcp.Engine.Tests.HmiScreenTraversalTests.Run(Count);
                Check(identity == 4 && Passed == identity + 18, "Incomplete adapter HMI traversal");
                Console.WriteLine("PASS adapter V" + key + " " + release + ": " + Passed + " HMI traversal assertions, 0 failed"); break;
            default: throw new ArgumentException("No native PLC harness mode: " + mode);
        }
        return 0;
    }
}
