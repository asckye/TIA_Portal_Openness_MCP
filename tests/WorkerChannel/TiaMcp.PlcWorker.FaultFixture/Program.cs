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
    private static int Main(string[] args)
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
        bool lost = false, bound = false;
        long epoch = 0;
        string? faultMethod = null, faultTool = null, faultCorrelation = null, savedAs = null;
        string Spill(string raw, string? mode)
        {
            if (mode == "oversized-delayed") Thread.Sleep(1200);
            return WorkerReplySpill.Write(Environment.GetEnvironmentVariable("TIA_MCP_WORKER_SPILLS_DIRECTORY")!, raw);
        }
        new ChannelServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), identity, () => new ChannelBinding(epoch, bound), request => {
            if (request.Method == "adapter.HostObserve")
                return ChannelResponse.Success(JsonSerializer.Serialize(new { releaseKey = options.ReleaseKey,
                    diagnosticsDirectory = Environment.GetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY"),
                    readiness = new { ready = true }, binding = bound ? new { projectPath = "C:/fixture.ap" + options.ReleaseKey } : null,
                    session = new { isConnected = bound } }));
            if (lost) return ChannelResponse.Error(new ChannelFailureException("Explicit restart required.", -32603, ChannelOutcome.RejectedBeforeNative));
            if (request.Method == "adapter.project-lifecycle.EnsureOpennessUserGroup"
                && Environment.GetEnvironmentVariable("TIA_MCP_PLC_WORKER_GROUP_FIX") is { Length: > 0 } groupFix)
                return GroupFix(groupFix);
            // The PlcWorker's Save As replies: the cached binding of SessionCallbackPrimitives.Snapshot and the bool of
            // ArchiveSaveProjectCopy. A target path containing "malformed" gets an object instead of the bool.
            if (request.Method == "adapter.project-lifecycle.ReadProjectLifecycleBinding")
                return ChannelResponse.Success(JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["identity"] = new { projectPath = savedAs ?? "C:\\fixture\\P.ap" + options.ReleaseKey, projectName = "P" }, ["fault"] = null,
                    ["instanceReserved"] = true, ["verifiedLive"] = false,
                    ["note"] = "Cached binding only. Project access verifies PID/start time/full path/name; no implicit rebind."
                }));
            if (request.Method == "adapter.project-lifecycle.ArchiveSaveProjectCopy")
            {
                string path = JsonNode.Parse(request.ArgumentsJson)!["path"]!.GetValue<string>();
                if (path.Contains("malformed")) return ChannelResponse.Success("{\"Saved\":true}");
                savedAs = path;
                return ChannelResponse.Success("true");
            }
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
            var injected = mode == null ? null : WorkerFaultInjection.PlcBeforeDispatch(typeof(Program).Assembly, request, _ => lost = true);
            if (injected != null) return injected;
            if (request.Method == "adapter.Attach")
            {
                bound = true; epoch++;
                return ChannelResponse.Success("{\"Stage\":\"attached\",\"OwnsPortal\":false,\"AttemptedPids\":[123],\"Strategy\":\"explicit\",\"LaunchMode\":\"attach\"}");
            }
            if (request.Method == "adapter.Disconnect")
            {
                bound = false; epoch++;
                return ChannelResponse.Success("{\"Stage\":\"disconnected\",\"SessionState\":\"terminal\",\"Strategy\":\"non-owning-attachment-only\",\"WorkerAcknowledged\":true,\"Detached\":true,\"SavedProject\":false,\"ClosedProject\":false,\"LaunchMode\":\"never\"}");
            }
            if (mode == "slow" && request.Method != "adapter.ReadState") Thread.Sleep(150);
            string padding = mode is "oversized" or "oversized-delayed" ? new string('x', ChannelLimits.ResponseBytes) : "fixture";
            if (request.Method == "adapter.optional-products.ExecuteOptionalProduct")
            {
                string step = new JsonObject { ["Message"] = "fixture", ["RequiresSessionReset"] = false,
                    ["Meta"] = new JsonObject { ["tool"] = "ListTestSuiteCases", ["success"] = true,
                        ["fixture"] = padding, ["records"] = new JsonArray() } }.ToJsonString();
                return ChannelResponse.WithSpill(step, () => Spill(step, mode));
            }
            string raw = request.Method == "adapter.ReadBlocks" ? new JsonArray(new JsonObject { ["Name"] = padding }).ToJsonString()
                : JsonSerializer.Serialize(new { ReleaseKey = options.ReleaseKey, IsAttached = bound, ProcessId = bound ? (int?)123 : null,
                    // Fault scenarios have a synthetic project so normal ReadState
                    // prechecks can admit the operation that will receive the fault.
                    ProjectFile = bound || !string.IsNullOrEmpty(fixtureMode) ? "C:/fixture.ap" + options.ReleaseKey : null, WithUi = options.WithUi,
                    TiaPortalLocation = Environment.GetEnvironmentVariable("TiaPortalLocation") });
            return ChannelResponse.WithSpill(raw, () => Spill(raw, mode));
        }).Run();
        return 0;
    }

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
                reply = new Dictionary<string, object?> { ["Message"] = null, ["Imported"] = new[] { "Screen_1" }, ["Failed"] = new object[0], ["Meta"] = meta };
                missing = "Meta";
                break;
            case "adapter.project-lifecycle.ExecuteProjectScaffoldPrimitive":
                var text = (request?["Text"] as JsonArray)?.Select(t => (string?)t ?? "").ToArray() ?? new string[0];
                reply = ((string?)request?["Kind"]) switch
                {
                    "addDevice" => new Dictionary<string, object?> { ["Ok"] = true, ["DeviceName"] = text[2], ["Family"] = text[3], ["MlfbUsed"] = text[0],
                        ["VersionUsed"] = text[1], ["Attempts"] = new[] { text[0] }, ["Error"] = null, ["Message"] = "Device '" + text[2] + "' added (one exact create call)", ["Meta"] = meta },
                    "compile" => new Dictionary<string, object?> { ["State"] = "Success", ["ErrorCount"] = 0, ["WarningCount"] = 0, ["Errors"] = new string[0],
                        ["Warnings"] = new string[0], ["Info"] = new string[0], ["RawMessages"] = new string[0], ["Message"] = "Software '" + text[0] + "' compile state=Success", ["Meta"] = meta },
                    "hmiInfo" => new Dictionary<string, object?> { ["Name"] = text[0], ["ProgramType"] = "Unified", ["Screens"] = new string[0],
                        ["Message"] = "HMI program info retrieved from '" + text[0] + "'", ["Meta"] = meta },
                    "hmiConnect" => new Dictionary<string, object?> { ["Meta"] = meta, ["Message"] = null, ["ObjectKind"] = "HmiConnection", ["ObjectPath"] = text[1],
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
