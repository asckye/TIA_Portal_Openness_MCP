using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using TiaMcp.PlcWorker;
using TiaMcp.WorkerChannel;
using TiaMcpServer.Siemens;

internal static class Program
{
    private static int Main(string[] args) => TiaMcp.Engine.Tests.Shared.FixtureCrashGuard.Run(() => Run(args));

    private static int Run(string[] args)
    {
        var options = WorkerLaunchOptions.Parse(args);
        if (options.TiaPortalLocation != null) Environment.SetEnvironmentVariable("TiaPortalLocation", options.TiaPortalLocation);
        AppDomain.CurrentDomain.UnhandledException += (_, error) => Console.Error.WriteLine(
            "PlcWorker background exception; terminating=" + error.IsTerminating + ": " + (error.ExceptionObject as Exception)?.Message);
        string exe = typeof(Program).Assembly.Location;
        string Hash(string path)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }
        var identity = new ChannelIdentity(options.ReleaseKey, Hash(exe),
            Hash(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + options.ReleaseKey + ".dll")),
            System.Diagnostics.Process.GetCurrentProcess().Id, options.Nonce);
        bool bound = false, projectClosed = false;
        bool failNextStatus = false;
        int attachedPid = 123;
        int writes = 0;
        long epoch = 0;
        string? faultMethod = null, faultTool = null, faultCorrelation = null, savedAs = null;
        string Spill(string raw, string? mode)
        {
            if (mode == "oversized-delayed") Thread.Sleep(1200);
            return WorkerReplySpill.Write(Environment.GetEnvironmentVariable("TIA_MCP_WORKER_SPILLS_DIRECTORY")!, raw);
        }
        // With TIA_MCP_FIXTURE_SLOW_GATE (a directory) the slow request marks its dispatch and waits for the test's release,
        // so a test can cancel while the request is certainly on the worker (P8-stab-1); otherwise it sleeps 150 ms as before.
        void Slow()
        {
            string? gate = Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE");
            if (string.IsNullOrEmpty(gate)) { Thread.Sleep(150); return; }
            File.WriteAllText(Path.Combine(gate, "dispatched"), "");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(Path.Combine(gate, "release")) && DateTime.UtcNow < deadline) Thread.Sleep(5);
        }
        new ChannelServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), identity, () => new ChannelBinding(epoch, bound), request => {
            if (request.Method == "adapter.FixtureWriteCount") return ChannelResponse.Success(writes.ToString());
            if (request.Method == "adapter.FixtureFailNextStatus")
            { failNextStatus = true; return ChannelResponse.Success("{}"); }
            if (request.Method == "adapter.HostObserve")
            {
                if (failNextStatus)
                {
                    failNextStatus = false;
                    return ChannelResponse.Error(new ChannelFailureException("Synthetic answered status error.", -32603, ChannelOutcome.ReadFailed));
                }
                return ChannelResponse.Success(JsonSerializer.Serialize(new { releaseKey = options.ReleaseKey,
                    diagnosticsDirectory = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY"),
                    readiness = new { ready = true }, binding = bound ? new {
                        projectPath = projectClosed ? null : savedAs ?? "C:/fixture.ap" + options.ReleaseKey } : null,
                    session = new { isConnected = bound } }));
            }
            if (request.Method == "adapter.project-lifecycle.EnsureOpennessUserGroup"
                && Environment.GetEnvironmentVariable("TIA_MCP_PLC_WORKER_GROUP_FIX") is { Length: > 0 } groupFix)
                return GroupFix(groupFix);
            // The PlcWorker's Save As replies: the cached binding of SessionCallbackPrimitives.Snapshot and the bool of
            // ArchiveSaveProjectCopy. A target path containing "malformed" gets an object instead of the bool.
            if (request.Method == "adapter.project-lifecycle.ReadProjectLifecycleBinding")
                return ChannelResponse.Success(JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["identity"] = new { projectPath = projectClosed ? null : savedAs ?? "C:\\fixture\\P.ap" + options.ReleaseKey,
                        projectName = projectClosed ? null : "P" }, ["fault"] = null,
                    ["instanceReserved"] = true, ["verifiedLive"] = false,
                    ["note"] = "Cached binding only. Project access verifies PID/start time/full path/name; no implicit rebind."
                }));
            // EngineeringTransaction's plain replies: the scope id, a member flag, and void for commit and dispose.
            if (request.Method == "adapter.engineering-services.BeginEngineeringTransaction") return ChannelResponse.Success("\"fixture-scope\"");
            if (request.Method == "adapter.engineering-services.ReadEngineeringTransaction") return ChannelResponse.Success("true");
            if (request.Method is "adapter.engineering-services.CommitEngineeringTransaction" or "adapter.engineering-services.DisposeEngineeringTransaction")
                return ChannelResponse.Success("null");
            if (FamilyReply(request.Method, request.ArgumentsJson) is { } familyReply) return ChannelResponse.Success(familyReply);
            if (request.Method == "adapter.FaultFixtureArm")
            {
                faultMethod = JsonNode.Parse(request.ArgumentsJson)!["method"]!.GetValue<string>();
                faultTool = request.ToolName;
                faultCorrelation = request.CorrelationId;
                if (string.IsNullOrWhiteSpace(faultMethod) || string.IsNullOrWhiteSpace(faultTool) || string.IsNullOrWhiteSpace(faultCorrelation))
                    throw new ArgumentException("Fault target requires a method, tool and correlation.");
                return ChannelResponse.Success("{\"armed\":true}");
            }
            // Readiness and prechecks may share the tool/correlation, but must not
            // consume the operation's fault. Other invocations of that tool are normal too.
            bool target = request.Method == faultMethod && request.ToolName == faultTool && request.CorrelationId == faultCorrelation;
            string? fixtureMode = WorkerFaultInjection.PlcMode(typeof(Program).Assembly);
            string? mode = target ? fixtureMode : null;
            if (mode == "hang" && request.Method == "adapter.SaveProject"
                && (bool?)JsonNode.Parse(request.ArgumentsJson)?["dryRun"] == true) mode = null;
            if (mode == "hang" && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE")))
            {
                string progressFault = Path.Combine(Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE")!, "progress-fault");
                if (File.Exists(progressFault))
                {
                    bool overLimit = File.ReadAllText(progressFault) == "over-limit";
                    int count = overLimit ? 1025 : 1;
                    for (int sequence = 1; sequence <= count; sequence++)
                        Console.Out.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "progress",
                            @params = new { requestId = request.Id, sequence = overLimit ? sequence : 2, percent = 50 } }));
                    Console.Out.Flush();
                }
                Slow();
                string faultAfterGate = Path.Combine(Environment.GetEnvironmentVariable("TIA_MCP_FIXTURE_SLOW_GATE")!, "after-gate-fault");
                if (File.Exists(faultAfterGate)) WorkerFaultInjection.BeforeDispatch(File.ReadAllText(faultAfterGate), request);
                mode = null;
            }
            var injected = mode == null ? null : WorkerFaultInjection.PlcBeforeDispatch(typeof(Program).Assembly, request, _ => { });
            if (injected != null) return injected;
            if (mode == "slow" && request.Method != "adapter.ReadState") Slow();
            if (request.Method == "adapter.project-lifecycle.ArchiveSaveProjectCopy")
            {
                string path = JsonNode.Parse(request.ArgumentsJson)!["path"]!.GetValue<string>();
                if (path.Contains("malformed")) return ChannelResponse.Success("{\"Saved\":true}");
                if (path.Contains("refused")) return ChannelResponse.Error(new ChannelFailureException(
                    "Save As needs a single-user project.", -32603, ChannelOutcome.RejectedBeforeNative,
                    "{\"exceptionType\":\"AdapterPreconditionException\",\"condition\":\"session\"}"));
                savedAs = path; projectClosed = false;
                bound = true; epoch++;
                return ChannelResponse.Success("true");
            }
            if (request.Method == "adapter.project-lifecycle.ExecuteMultiuserSession")
            {
                var args = JsonNode.Parse(request.ArgumentsJson)!["request"]!;
                bool committed = (string?)args["Action"] == "commit" && (bool?)args["DryRun"] == false;
                if (committed) { savedAs = null; projectClosed = true; epoch++; }
                return Step("{\"operationSuccess\":true}", committed);
            }
            if (request.Method == "adapter.project-lifecycle.ConnectIsolatedPortal")
            {
                if (bound) return ChannelResponse.Error(new ChannelFailureException(
                    "This session already owns a TIA connection.", -32603, ChannelOutcome.RejectedBeforeNative,
                    "{\"exceptionType\":\"AdapterPreconditionException\",\"condition\":\"session\"}"));
                bound = true; epoch++;
                return Step("{\"operationSuccess\":true}", false);
            }
            if (request.Method == "adapter.project-lifecycle.RetrieveProjectArchive")
            {
                var args = JsonNode.Parse(request.ArgumentsJson)!["request"]!;
                bool refused = args["ArchivePath"]!.GetValue<string>().Contains("refused");
                if (!refused && (bool?)args["DryRun"] == false) { bound = true; epoch++; }
                return Step(refused
                    ? "{\"operationSuccess\":false,\"status\":\"PreconditionFailed\",\"mayHaveChanged\":false}"
                    : "{\"operationSuccess\":true}", !refused && (bool?)args["DryRun"] == false);
            }
            if (request.Method == "adapter.FixtureDocumentImportUnknown")
            {
                var document = JsonSerializer.SerializeToNode(new TiaMcp.Adapters.PlcDocumentImportResult())!.AsObject();
                document["Status"] = "unknown"; document["Attempted"] = true; document["MayHaveChanged"] = true;
                document["RequiresSessionReset"] = true; document["Error"] = "Synthetic document import result unknown.";
                return ChannelResponse.Success(document.ToJsonString());
            }
            if (request.Method == "adapter.FixtureAnsweredUnknown")
            {
                writes++;
                return ChannelResponse.Error(new ChannelFailureException("Synthetic answered unknown.", -32603, ChannelOutcome.Unknown));
            }
            if (request.Method == "adapter.SaveProject")
            {
                bool preview = JsonNode.Parse(request.ArgumentsJson)?["dryRun"] is JsonValue dryRun
                    && dryRun.TryGetValue<bool>(out var requestedPreview) && requestedPreview;
                if (!preview) writes++;
                return ChannelResponse.Success(new JsonObject { ["Executed"] = !preview,
                    ["ProjectFile"] = "C:/fixture.ap" + options.ReleaseKey }.ToJsonString());
            }
            if (request.Method == "adapter.Attach")
            {
                attachedPid = (int)JsonNode.Parse(request.ArgumentsJson)!["processId"]!;
                bound = true; epoch++;
                return ChannelResponse.Success(new JsonObject { ["Stage"] = "attached", ["OwnsPortal"] = false,
                    ["AttemptedPids"] = new JsonArray(attachedPid), ["Strategy"] = "explicit", ["LaunchMode"] = "attach" }.ToJsonString());
            }
            if (request.Method == "adapter.Disconnect")
            {
                bool wasBound = bound;
                bound = false; epoch++;
                return ChannelResponse.Success(new JsonObject { ["Stage"] = "disconnected", ["SessionState"] = "disconnected",
                    ["Strategy"] = "non-owning-attachment-only", ["WorkerAcknowledged"] = true, ["Detached"] = wasBound,
                    ["ProcessId"] = wasBound ? attachedPid : (int?)null, ["SavedProject"] = false, ["ClosedProject"] = false,
                    ["LaunchMode"] = "never" }.ToJsonString());
            }
            string padding = mode is "oversized" or "oversized-delayed" ? new string('x', ChannelLimits.ResponseBytes) : "fixture";
            if (request.Method == "adapter.optional-products.ExecuteOptionalProduct")
            {
                string step = new JsonObject { ["Message"] = "fixture", ["RequiresSessionReset"] = false,
                    ["MayHaveChanged"] = false, ["BlockReadsAfterUncertain"] = true,
                    ["Meta"] = new JsonObject { ["tool"] = "ListTestSuiteCases", ["success"] = true,
                        ["fixture"] = padding, ["records"] = new JsonArray() } }.ToJsonString();
                return ChannelResponse.WithSpill(step, () => Spill(step, mode));
            }
            string raw = request.Method == "adapter.ReadBlocks" ? new JsonArray(new JsonObject { ["Name"] = padding }).ToJsonString()
                : request.Method == "adapter.plc-paging.ReadBlockPage" ? new JsonObject {
                    ["Items"] = new JsonArray(new JsonObject { ["Name"] = padding, ["Path"] = "fixture" }),
                    ["Offset"] = 0, ["Total"] = 1, ["NextCursor"] = null }.ToJsonString()
                : JsonSerializer.Serialize(new { ReleaseKey = options.ReleaseKey, IsAttached = bound, ProcessId = bound ? (int?)attachedPid : null,
                    // Fault scenarios have a synthetic project so normal ReadState
                    // prechecks can admit the operation that will receive the fault.
                    ProjectFile = projectClosed ? null : savedAs
                        ?? (bound || !string.IsNullOrEmpty(fixtureMode) ? "C:/fixture.ap" + options.ReleaseKey : null),
                    WithUi = options.WithUi,
                    TiaPortalLocation = Environment.GetEnvironmentVariable("TiaPortalLocation") });
            return ChannelResponse.WithSpill(raw, () => Spill(raw, mode));
        }).Run();
        return 0;
    }

    private static ChannelResponse Step(string meta, bool changed) => ChannelResponse.Success(new JsonObject {
        ["Message"] = "Fixture lifecycle result.", ["Meta"] = JsonNode.Parse(meta),
        ["RequiresSessionReset"] = false, ["MayHaveChanged"] = changed, ["BlockReadsAfterUncertain"] = true
    }.ToJsonString());

    // Replies whose shape the host checks member for member, as their worker producers write them: the PUT/GET dictionaries
    // (PutGetAccess.GetPutGetAccess, HardwareSecurityAdapter.SetPutGetAccess), PlcSeedHmiReply and the scaffold primitives of
    // WorkerProjectScaffold.Execute. A request that contains "malformed" gets the same reply with one member missing.
    private static string? FamilyReply(string method, string argumentsJson)
    {
        if (method is not ("adapter.project-security.ExecuteHardwareSecurity" or "adapter.plc-software.SeedReferenceHmi"
            or "adapter.project-lifecycle.ExecuteProjectScaffoldPrimitive")) return null;
        var request = (JsonNode.Parse(argumentsJson) as JsonObject)?["request"] as JsonObject;
        bool malformed = argumentsJson.Contains("malformed");
        var meta = new Dictionary<string, object?> { ["success"] = true };
        Dictionary<string, object?>? reply = null;
        string missing = "";
        switch (method)
        {
            case "adapter.project-security.ExecuteHardwareSecurity":
                string device = (string?)request?["DeviceName"] ?? "";
                if ((string?)request?["Operation"] == "GetPlcPutGetAccess")
                    reply = new Dictionary<string, object?> { ["found"] = true, ["device"] = device, ["deviceItem"] = "PLC_1",
                        ["attributeName"] = "PutGetCommunication", ["enabled"] = true, ["rawValue"] = "True" };
                else if ((string?)request?["Operation"] == "SetPlcPutGetAccess")
                {
                    bool enable = (bool?)request?["Enable"] == true;
                    reply = new Dictionary<string, object?> { ["ok"] = true, ["mayHaveChanged"] = true, ["writeOutcomeKnown"] = true, ["device"] = device,
                        ["deviceItem"] = "PLC_1", ["attributeName"] = "PutGetCommunication", ["before"] = (!enable).ToString(), ["after"] = enable.ToString(),
                        ["note"] = "Hardware-config change — run DownloadPlc (hardware) for it to take effect on the CPU." };
                }
                missing = reply != null && reply.ContainsKey("note") ? "note" : "rawValue";
                break;
            case "adapter.plc-software.SeedReferenceHmi":
                reply = new Dictionary<string, object?> { ["Message"] = null, ["Imported"] = new[] { "Screen_1" }, ["Failed"] = new object[0], ["Meta"] = meta
                };
                missing = "Meta";
                break;
            case "adapter.project-lifecycle.ExecuteProjectScaffoldPrimitive":
                var text = (request?["Text"] as JsonArray)?.Select(t => (string?)t ?? "").ToArray() ?? new string[0];
                reply = ((string?)request?["Kind"]) switch
                {
                    "addDevice" => new Dictionary<string, object?> { ["Ok"] = true, ["DeviceName"] = text[2], ["Family"] = text[3], ["MlfbUsed"] = text[0],
                        ["VersionUsed"] = text[1], ["Attempts"] = new[] { text[0] }, ["Error"] = null, ["Message"] = "Device '" + text[2]
                            + "' added (one exact create call)", ["Meta"] = meta },
                    "compile" => new Dictionary<string, object?> { ["State"] = "Success", ["ErrorCount"] = 0, ["WarningCount"] = 0, ["Errors"] = new string[0],
                        ["Warnings"] = new string[0], ["Info"] = new string[0], ["RawMessages"] = new string[0], ["Message"] = "Software '" + text[0]
                            + "' compile state=Success", ["Meta"] = meta },
                    "hmiInfo" => new Dictionary<string, object?> { ["Name"] = text[0], ["ProgramType"] = "Unified", ["Screens"] = new string[0],
                        ["Message"] = "HMI program info retrieved from '" + text[0] + "'", ["Meta"] = meta },
                    "hmiConnect" => new Dictionary<string, object?> {
                        ["Meta"] = meta, ["Message"] = null, ["ObjectKind"] = "HmiConnection", ["ObjectPath"] = text[1],
                        ["TypeName"] = null, ["Members"] = null },
                    _ => new Dictionary<string, object?> { ["Message"] = "fixture " + (string?)request?["Kind"], ["Meta"] = meta },
                };
                missing = "Meta";
                break;
        }
        if (reply == null) return null;
        if (malformed) reply.Remove(missing);
        return JsonSerializer.Serialize(reply);
    }

    // The PlcWorker's group repair (EnsureMembership, then RequireEffectiveMembership) on a fake Windows platform that
    // produces the named outcome, with the dispatcher's failure shape: success is true, any other outcome its message.
    // "malformed" answers with a string instead of the bool.
    private static ChannelResponse GroupFix(string outcome)
    {
        if (outcome == "malformed") return ChannelResponse.Success("\"true\"");
        try
        {
            var platform = new FixturePlatform((WindowsOpennessGroup.Outcome)Enum.Parse(typeof(WindowsOpennessGroup.Outcome), outcome));
            return ChannelResponse.Success(WindowsOpennessGroup.RequireEffectiveMembership(WindowsOpennessGroup.EnsureMembership(platform)) ? "true" : "false");
        }
        catch (InvalidOperationException error)
        {
            return ChannelResponse.Error(new ChannelFailureException(error.Message, -32603, ChannelOutcome.Unknown));
        }
    }

    private sealed class FixturePlatform : WindowsOpennessGroup.IPlatform
    {
        private readonly WindowsOpennessGroup.Outcome target;
        private bool added;
        internal FixturePlatform(WindowsOpennessGroup.Outcome target) { this.target = target; }
        public WindowsOpennessGroup.CurrentUser ReadCurrentUser() => new("S-1-5-21-1-2-3-1001", "FIXTURE\\user");
        public bool ReadTokenMembership() => target == WindowsOpennessGroup.Outcome.Ready;
        public bool? ReadDirectLocalMembership(string sid) => target switch
        {
            WindowsOpennessGroup.Outcome.MissingGroup => null,
            WindowsOpennessGroup.Outcome.PendingLogon => true,
            WindowsOpennessGroup.Outcome.AddedPendingLogon => added,
            _ => false,
        };
        public int AddCurrentUser(string accountName)
        {
            if (target == WindowsOpennessGroup.Outcome.ElevationDeclined) throw new System.ComponentModel.Win32Exception(1223);
            added = target == WindowsOpennessGroup.Outcome.AddedPendingLogon;
            return target == WindowsOpennessGroup.Outcome.AddFailed ? 2 : 0;
        }
    }
}
