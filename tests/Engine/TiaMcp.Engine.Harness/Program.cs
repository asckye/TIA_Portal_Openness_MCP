using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
    private static Assembly Server = null!;
    // Source captures run the host in-process. Keep the unattached adapter on
    // its own MTA thread without adopting a Portal or changing session lifecycle.
    private sealed class SourceHardwareBridge : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> queue = new System.Collections.Concurrent.BlockingCollection<Action>();
        private readonly Thread thread;
        private readonly FieldInfo bridge;
        private readonly object previous;
        private IDisposable adapter = null!;
        private object dispatcher = null!;
        private MethodInfo dispatch = null!;
        private Type request = null!;

        internal SourceHardwareBridge(Assembly server, string api)
        {
            bridge = FindServerType(server, "TiaMcpServer.ModelContextProtocol.HardwareAddressWorkerBridge").GetField("Call", All)!;
            previous = bridge.GetValue(null)!;
            var ready = new TaskCompletionSource<bool>();
            thread = new Thread(() => {
                try {
                    var type = FindServerType(server, "TiaMcp.Adapters.PlcFoundationEngine");
                    string release = server.GetReferencedAssemblies().First(a => a.Name!.StartsWith("Siemens.Engineering", StringComparison.Ordinal)).Version!.Major.ToString();
                    adapter = (IDisposable)Activator.CreateInstance(type, new object[] { release, api })!;
                    var dispatcherType = server.GetType("TiaMcp.PlcWorker.FoundationWorkerDispatcher", true)!;
                    dispatcher = Activator.CreateInstance(dispatcherType, All, null, new object[] { adapter, server, null! }, null)!;
                    dispatch = dispatcherType.GetMethod("Dispatch", All)!;
                    request = FindServerType(server, "TiaMcp.WorkerChannel.ChannelRequest");
                    ready.SetResult(true);
                    foreach (var action in queue.GetConsumingEnumerable()) action();
                }
                catch (Exception error) { ready.TrySetException(error); }
                finally { adapter?.Dispose(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            ready.Task.GetAwaiter().GetResult();
            Func<string, System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonNode?> call = (operation, input) => {
                var completion = new TaskCompletionSource<System.Text.Json.Nodes.JsonNode?>();
                queue.Add(() => {
                    try {
                        var message = Activator.CreateInstance(request, new object[] { 0L, "adapter." + operation, input.ToJsonString(), (Action<int, string?>)((_, __) => { }), null! })!;
                        var response = dispatch.Invoke(dispatcher, new[] { message })!;
                        var failure = response.GetType().GetProperty("Failure")!.GetValue(response);
                        if (failure != null) throw new InvalidOperationException((string)failure.GetType().GetProperty("Message")!.GetValue(failure)!);
                        completion.SetResult(System.Text.Json.Nodes.JsonNode.Parse((string)response.GetType().GetProperty("ResultJson")!.GetValue(response)!));
                    }
                    catch (Exception error) { completion.SetException(error); }
                });
                return completion.Task.GetAwaiter().GetResult();
            };
            bridge.SetValue(null, call);
        }

        public void Dispose()
        {
            bridge.SetValue(null, previous);
            queue.CompleteAdding();
            thread.Join();
            queue.Dispose();
        }
    }
    internal static Type FindServerType(Assembly server, string name)
        => FindReferencedType(server, name);
    internal static Type FindReferencedType(Assembly server, string name)
    {
        var direct = server.GetType(name, false);
        if (direct != null) return direct;

        foreach (var reference in server.GetReferencedAssemblies())
        {
            Assembly assembly;
            try { assembly = Assembly.Load(reference); }
            catch (Exception error) when (error is FileNotFoundException || error is FileLoadException
                || error is BadImageFormatException || error is TypeLoadException) { continue; }

            var type = assembly.GetType(name, false);
            if (type != null) return type;
        }

        throw new TypeLoadException("Could not find type '" + name + "' in the engine or any of its referenced assemblies.");
    }
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    internal static int ExpectedFullToolCount(int major)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VersionTools.json")
            ?? throw new InvalidOperationException("Generated release catalog is missing from the harness.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var catalog = Json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
        var releases = (Dictionary<string, object>)catalog["releases"];
        var release = (Dictionary<string, object>)releases[major.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        return Convert.ToInt32(release["toolCount"], System.Globalization.CultureInfo.InvariantCulture);
    }
    internal static int ExpectedEngineToolCount(int major) => ExpectedFullToolCount(major)
        - EngineSurface.PortedTools(Server, major.ToString(System.Globalization.CultureInfo.InvariantCulture)).Length - 2;
    private static int Passed;
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static bool HasChinese(string? value) => !String.IsNullOrEmpty(value)
        && value.Any(character => character >= '\u4e00' && character <= '\u9fff');
    private static Dictionary<string, object> Parse(string value) => Json.Deserialize<Dictionary<string, object>>(value);
    private static string Request(object id) => Json.Serialize(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = "GetSessionState" } });
    private static string Reply(object id, object value) => Json.Serialize(new { jsonrpc = "2.0", id, result = value });
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static Dictionary<string, object> GetToolUsage(string toolName = "", string query = "", string language = "",
        string exampleId = "", string exampleKind = "all")
    {
        var result = HostPortRunner.Call(Server, "GetToolUsage", new System.Text.Json.Nodes.JsonObject {
            ["toolName"] = toolName, ["query"] = query, ["language"] = language,
            ["exampleId"] = exampleId, ["exampleKind"] = exampleKind, ["limit"] = 80
        });
        return Json.Deserialize<Dictionary<string, object>>(result.ToJsonString())!;
    }
    private static Dictionary<string, object> AsObject(object value) => (Dictionary<string, object>)value;
    // JavaScriptSerializer returns object[] or ArrayList depending on the target type.
    private static object[] AsArray(object value) => value as object[] ?? ((System.Collections.IEnumerable)value).Cast<object>().ToArray();
    private static bool IsV4ForRelease(Dictionary<string, object> envelope, string release)
        => Convert.ToInt32(envelope["schemaVersion"]) == 4 && AsObject(envelope["meta"])["releaseKey"].ToString() == release
            && envelope.ContainsKey("data");
    private static async Task<T> Bounded<T>(Task<T> task, int ms = 5000)
    { if (await Task.WhenAny(task, Task.Delay(ms)) != task) throw new Exception("Test operation hung"); return await task; }
    private static async Task Fault(Task task, Type type)
    {
        try { await Bounded(AsResult(task)); }
        catch (Exception ex) { if (type.IsInstanceOfType(ex)) return; throw; }
        throw new Exception("Expected " + type.Name);
    }
    private static async Task<bool> AsResult(Task task) { await task; return true; }

    private static async Task Test(string label, Func<Task> test)
    { await test(); Passed++; Console.WriteLine("PASS " + label); }

    private static async Task<int> Main(string[] args)
    {
        // Mirror the engine's startup (Program.cs): .NET Framework writes child stdin with Console.InputEncoding,
        // which emits a UTF-8 BOM when the console code page is 65001 and the engine has not replaced it.
        try { Console.InputEncoding = new System.Text.UTF8Encoding(false); } catch (IOException) { }
        try {
            if(args.Length > 0 && args[0] == "stdin-hex-fixture") {
                using var input = Console.OpenStandardInput();
                using var bytes = new MemoryStream();
                input.CopyTo(bytes);
                Console.WriteLine(BitConverter.ToString(bytes.ToArray()));
                return 0;
            }
            string exe=Path.GetFullPath(args[0]); string dir=Path.GetDirectoryName(exe)!;
            bool hostBuildNoTia = args.Length > 1 && args[1] == "host-build-no-tia";
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                string name=new AssemblyName(e.Name).Name!;
                if(hostBuildNoTia && name.StartsWith("Siemens.Engineering",StringComparison.Ordinal)) return null;
                string dependency=Path.Combine(dir,name+".dll");
                return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
            };
            Server=Assembly.LoadFrom(exe);
            if (args.Length > 2 && Directory.Exists(args[2]))
                AppDomain.CurrentDomain.AssemblyResolve += (_, requested) => {
                    string dependency = Path.Combine(Path.GetFullPath(args[2]), new AssemblyName(requested.Name).Name + ".dll");
                    return File.Exists(dependency) ? Assembly.LoadFrom(dependency) : null;
                };
            EngineSurface.CheckHostRetirement(Server, Check);
            if (args.Length >= 2 && args[1] == "descriptor-catalog-only") {
                ToolDescriptorChecks.Run(Server, (ok, message) => { Check(ok, message); Passed++; });
                Console.WriteLine("COMPLETE: " + Passed + " descriptor catalog checks passed");
                return 0;
            }
            if (args.Length >= 2 && new[] { "host-build-no-tia", "packaged-start-no-tia", "http-start-no-tia" }.Contains(args[1]))
                return FoundationHostChecks.Run(Server, args[1] == "packaged-start-no-tia");
            if (args.Length >= 2 && new[] { "concurrency-only", "staging-sessions-only" }.Contains(args[1]))
                return FoundationHostChecks.Suites("engine-host");
            if(args.Length > 1 && args[1] == "test-download-route")
                return DeveloperChecks.DownloadRoute(Server, args.Length > 2 ? args[2] : "");
            if(args.Length > 1 && args[1] == "test-match-plc-name")
                return DeveloperChecks.MatchPlcName(Server);
            if(args.Length > 2 && args[1] == "test-migration-read-assembly")
                return DeveloperChecks.MigrationReadAssembly(Server, args[2]);
            if(args.Length > 2 && args[1] == "test-ecosystem-assembly")
                return DeveloperChecks.EcosystemAssembly(Server, args[2], args.Skip(3).ToArray());
            if(args.Length > 2 && args[1] == "generate-tools-list")
                return DeveloperChecks.GenerateToolsList(Server, args[2], args.Length > 3 ? args[3] : "", args.Length > 4 ? args[4] : "",
                    args.Length > 5 ? args[5] : "", args.Length > 6 ? args[6] : "");
            if(args.Length >= 5 && args[1] == "script-inputs-only") {
                // Validate campaign fixtures with the engine's actual closed schema
                // implementation. No tool is dispatched and no Portal is created.
                var schemaType = FindServerType(Server, "TiaMcp.Logic.V4.Inputs.InputSchema");
                var parse = FindServerType(Server, "TiaMcp.Logic.V4.V4Json").GetMethod("ParseInput", All)!;
                var schemas = ((object[])Json.DeserializeObject(File.ReadAllText(args[3])))
                    .Cast<Dictionary<string, object>>().ToDictionary(t => (string)t["name"], t =>
                        Activator.CreateInstance(schemaType, new[] { parse.Invoke(null, new object[] { Json.Serialize(t["inputSchema"]) }) })!);
                int rejected = 0, failed = 0;
                foreach(var item in ((object[])Json.DeserializeObject(File.ReadAllText(args[4]))).Cast<Dictionary<string, object>>()) {
                    var input = parse.Invoke(null, new object[] { Json.Serialize(item["args"]) });
                    var error = schemaType.GetMethod("Validate")!.Invoke(schemas[(string)item["tool"]], new[] { input, "arguments" });
                    bool negative = (bool)item["negativeInput"];
                    if((error != null) != negative) {
                        failed++;
                        Console.WriteLine("FAIL " + item["label"] + " " + item["tool"] + ": " + Json.Serialize(error));
                    } else { Passed++; if(negative) rejected++; }
                }
                Console.WriteLine("COMPLETE: " + Passed + " script input checks passed (" + rejected + " explicit rejections); " + failed + " failed; no dispatch");
                return 0;
            }
            if(args.Length >= 4 && args[1] == "device-candidate-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                DeviceCandidateChecks.Run(Server, args[3] == "safe-v4", args.Length > 4 ? args[4] : "",
                    (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " device candidate checks passed");
                return 0;
            }
            if(args.Length >= 4 && args[1] == "import-candidate-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                ImportCandidateChecks.Run(Server, args[3] == "safe-v4", args.Length > 4 ? args[4] : "",
                    (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " import candidate checks passed");
                return 0;
            }
            if(args.Length >= 4 && args[1] == "session-candidate-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                SessionCandidateChecks.Run(Server, args[3] == "safe-v4", args.Length > 4 ? args[4] : "",
                    (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " session candidate checks passed");
                return 0;
            }
            if(args.Length >= 4 && args[1] == "save-close-candidate-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                SaveCloseCandidateChecks.Run(Server, args[3] == "safe-v4", args.Length > 4 ? args[4] : "",
                    (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " save/close candidate checks passed");
                return 0;
            }
            if(args.Length >= 4 && args[1] == "compile-candidate-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                CompileCandidateChecks.Run(Server, args[3] == "safe-v4", args.Length > 4 ? args[4] : "",
                    (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " compile candidate checks passed");
                return 0;
            }
            if(args.Length >= 4 && args[1] == "fallback-candidate-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                FallbackCandidateChecks.Run(Server, args[3] == "safe-v4", args.Length > 4 ? args[4] : "",
                    (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " fallback candidate checks passed");
                return 0;
            }
            if(args.Length >= 4 && args[1] == "source-candidate-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                SourceCandidateChecks.Run(Server, args[3] == "safe-v4", args.Length > 4 ? args[4] : "",
                    (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " source candidate checks passed");
                return 0;
            }
            if(args.Length >= 4 && args[1] == "export-candidate-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                ExportCandidateChecks.Run(Server, args[3] == "safe-v4", args.Length > 4 ? args[4] : "",
                    (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " export candidate checks passed");
                return 0;
            }
            if(args.Length >= 3 && (args[1] == "response-golden-only" || args[1] == "response-golden-record")) {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                string golden = args.Length > 3 ? Path.GetFullPath(args[3]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Golden");
                ResponseGoldenTests.Run(Server, golden, args[1] == "response-golden-record",
                    (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " response golden checks passed");
                return 0;
            }
            if(args.Length > 1 && args[1] == "child-stdin-only") { await ChildStdinTests(); return 0; }
            if (args.Length > 1 && args[1] == "example-library-only") {
                void Counted(bool ok, string message) { Check(ok, message); Passed++; }
                string release = args.Length > 2 ? args[2] : FindServerType(Server, "TiaMcpServer.Siemens.EngineRouter")
                    .GetField("CompiledTiaMajorVersion", All)!.GetValue(null)!.ToString()!;
                var index = GetToolUsage();
                Counted(IsV4ForRelease(index, release), "GetToolUsage index has the selected release V4 envelope");
                var indexData = AsObject(index["data"]);
                Counted(Convert.ToInt32(indexData["toolCount"]) > 0 && AsArray(indexData["tools"]).Length > 0,
                    "GetToolUsage index returns registered tools");

                foreach (var languageCode in new[] { "scl", "scl-sd", "lad", "fbd", "mixed", "db", "udt", "s7res", "stl", "graph", "hmi-javascript", "hmi-vbscript", "csharp" })
                {
                    var library = GetToolUsage(language: languageCode);
                    var data = AsObject(library["data"]);
                    Counted(IsV4ForRelease(library, release)
                        && (AsArray(data["examples"]).Length > 0 || data.ContainsKey("sourceDocuments")),
                        "GetToolUsage loads " + languageCode + " examples for the selected engine");
                }

                var perTool = GetToolUsage(toolName: "CreateDevice"); // a registered tool whose example has arguments on both engines
                Counted(IsV4ForRelease(perTool, release), "GetToolUsage per-tool result has the selected release");
                var example = AsObject(AsObject(perTool["data"])["example"]);
                var arguments = AsObject(AsObject(AsObject(example["request"])["params"])["arguments"]);
                Counted(example["kind"].ToString() == "parameterized-call-example" && arguments.Count > 0,
                    "GetToolUsage returns typed arguments for a registered tool");

                var language = GetToolUsage(language: "scl", exampleKind: "language");
                Counted(IsV4ForRelease(language, release) && AsArray(AsObject(language["data"])["examples"])
                    .Select(AsObject).Any(row => Convert.ToBoolean(row["releaseMatches"]) && Convert.ToBoolean(row["profileMatches"])),
                    "GetToolUsage lists an SCL example matching the selected release");
                var source = GetToolUsage(exampleId: "scl-add", exampleKind: "language");
                var sourceRow = AsObject(AsArray(AsObject(source["data"])["examples"]).First());
                Counted(IsV4ForRelease(source, release) && Convert.ToBoolean(sourceRow["releaseMatches"])
                    && Convert.ToBoolean(sourceRow["profileMatches"]) && AsArray(sourceRow["files"]).Length > 0,
                    "GetToolUsage returns the complete release-matched language source");

                var sequenceId = "sequence/plc-scl-block";
                var sequence = GetToolUsage(exampleId: sequenceId, exampleKind: "sequence");
                var sequenceRow = AsObject(AsArray(AsObject(sequence["data"])["examples"]).First());
                Counted(IsV4ForRelease(sequence, release) && Convert.ToBoolean(sequenceRow["releaseMatches"])
                    && Convert.ToBoolean(sequenceRow["available"]) && AsArray(sequenceRow["steps"]).Length > 0,
                    "GetToolUsage returns a registered sequence for the selected engine release");

                var search = GetToolUsage(query: "openness-base");
                Counted(IsV4ForRelease(search, release) && AsArray(AsObject(search["data"])["matches"])
                    .Select(AsObject).Any(row => row["id"].ToString() == "guides/skills/openness-base/SKILL.md"),
                    "GetToolUsage official-document search is scoped to the selected release");
                Console.WriteLine("COMPLETE: " + Passed + " GetToolUsage index/example/language/sequence/reference checks; no native calls");
                return 0;
            }
            if (args.Length >= 3 && args[1] == "native-diagnostics-only") { NativeDiagnosticsJit(args[2]); return 0; }
            if (args.Length > 1 && args[1] == "lease-holder") return LeaseFixture(args);
            if (args.Length > 1 && args[1] == "process-leases-only") { await ProcessLeaseTests(); return 0; }
            if(args.Length > 1 && args[1] == "worker-supervisor-only") return FoundationHostChecks.Suites("worker-robustness");
            if(args.Length >= 3 && (args[1] == "software-lookup-only" || args[1] == "engineering-api-only" || args[1] == "hardware-contracts-only")) {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                if(args[1] == "hardware-contracts-only") {
                    HardwareContractsTests.Run(Server, (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                    Console.WriteLine("COMPLETE: " + Passed + " hardware contract checks passed");
                    return 0;
                }
                if(args[1] == "engineering-api-only") {
                    Action<bool,string> shapeCheck = (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); };
                    EngineSurfaceChecks.Run(Server, shapeCheck);
                    SecurityContractChecks.Run(Server, shapeCheck);
                    ProjectSecurityShapeChecks.Run(Server, shapeCheck);
                    SafetyShapeChecks.Run(Server, shapeCheck);
                    SecurityDeepShapeChecks.Run(Server, shapeCheck);
                    SafetyValidationShapeChecks.Run(Server, shapeCheck);
                    EngineeringApiShapeTests.Run(Server, shapeCheck);
                    BindingAndTagShapeChecks.Run(Server, shapeCheck);
                    DeviceTransferShapeChecks.Run(Server, shapeCheck);
                    PlcBlockServicesShapeChecks.Run(Server, shapeCheck);
                    HardwareServicesShapeChecks.Run(Server, shapeCheck);
                    UnifiedUiModelShapeChecks.Run(Server, shapeCheck);
                    MotionProDiagClassicHmiShapeChecks.Run(Server, shapeCheck);
                    RuntimeChannelsShapeChecks.Run(Server, shapeCheck);
                    SimulationDocumentationShapeChecks.Run(Server, shapeCheck);
                    UnifiedScreenItemShapeChecks.Run(Server, shapeCheck);
                    UnifiedExchangeShapeChecks.Run(Server, shapeCheck);
                    HardwareNetworkShapeChecks.Run(Server, shapeCheck);
                    LibraryDeepShapeChecks.Run(Server, shapeCheck);
                    SessionAndHardwareShapeChecks.Run(Server, shapeCheck);
                    SoftwareUnitDeepShapeChecks.Run(Server, shapeCheck);
                    PlcDomainShapeChecks.Run(Server, shapeCheck);
                    TechnologyMappingShapeChecks.Run(Server, shapeCheck);
                    ClassicHmiFoldersShapeChecks.Run(Server, shapeCheck);
                    SivarcShapeChecks.Run(Server, shapeCheck);
                    StartdriveShapeChecks.Run(Server, shapeCheck);
                    DccShapeChecks.Run(Server, shapeCheck);
                    TestSuiteShapeChecks.Run(Server, shapeCheck);
                    TeamcenterShapeChecks.Run(Server, shapeCheck);
                    CfcShapeChecks.Run(Server, shapeCheck);
                    EngineeringAuditRuntimeChecks.Run(Server, shapeCheck);
                    Console.WriteLine("COMPLETE: " + Passed + " engineering API checks passed");
                    return 0;
                }
                SoftwareLookupRuntimeTests.Run(Server, (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " software lookup checks passed");
                return 0;
            }
            if(args.Length >= 3 && args[1] == "behavior-capabilities-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                BehaviorCapabilityChecks.Run(Server, (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " behavior capability checks passed");
                return 0;
            }
            if(args.Length >= 4 && args[1] == "offline-contracts-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                TiaMcp.Engine.Tests.OfflineContractsTests.Run(Server, Path.GetFullPath(args[3]),
                    (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Check(Passed >= 64, "Missing engine retirement checks; offline assertions run in the net10 offline suites.");
                Console.WriteLine("COMPLETE: " + Passed + " offline contract checks passed");
                return 0;
            }
            if(args.Length >= 3 && args[1] == "usage-contracts-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                PilotToolChecks.UsageServesEveryMigratedExampleAgainstItsActualSchema(Server,
                    (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " V4 usage contract checks passed");
                return 0;
            }
            if(args.Length >= 3 && args[1] == "runtime-settings-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                RuntimeSettingsRuntimeTests.Run(Server, (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " runtime settings checks passed");
                return 0;
            }
            if(args.Length >= 3 && args[1] == "graphic-selection-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                GraphicSelectionRuntimeTests.Run(Server, (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " graphical selection checks passed");
                return 0;
            }
            if(args.Length >= 3 && args[1] == "global-script-only") {
                string api=Path.GetFullPath(args[2]);
                AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                    string dependency=Path.Combine(api,new AssemblyName(e.Name).Name+".dll");
                    return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                GlobalScriptBridgeTests.Run(Server, (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " global script bridge checks passed");
                return 0;
            }
            if(args.Skip(1).Contains("hmi-snapshot-only")) {
                HmiSnapshotRemotingTests.Run(Server, (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " HMI snapshot remoting checks passed");
                return 0;
            }
            if(args.Skip(1).Contains("native-export-only")) {
                NativeExportRemotingTests.Run(Server, (ok, message) => { Check(ok, message); Passed++; Console.WriteLine("PASS " + message); });
                Console.WriteLine("COMPLETE: " + Passed + " native export remoting checks passed");
                return 0;
            }
            if(args.Skip(1).Contains("hmi-only")) {
                var refs=Server.GetReferencedAssemblies().Where(x=>x.Name!.StartsWith("Siemens.Engineering")).ToArray();
                int major = int.Parse(args[2]);
                Check(refs.Length>0 && refs.All(x=>x.Version!.Major==major),"Wrong TIA reference version");
                Check(System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion==args[3],"Wrong fixed file version");
                TiaMcpServer.Siemens.HmiScreenTraversal.EngineType=Server.GetType("TiaMcpServer.Siemens.HmiScreenTraversal",true)!;
                int failures=0;
                TiaMcp.Engine.Tests.HmiScreenTraversalTests.Run((ok,name)=>{if(ok)Passed++;else{failures++;Console.Error.WriteLine("FAIL "+name);}});
                Check(failures==0 && Passed==18,"HMI traversal regression failed");
                Console.WriteLine("PASS actual V" + major + " EXE " + args[3] + ": 18 HMI traversal assertions, 0 failed");
                return 0;
            }
            await ChildStdinTests();
            return FoundationHostChecks.Run(Server);
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
