using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.PlcWorker;

// The approved scaffold remains host-side. Each primitive reuses the normal
// owner-thread dispatcher, including its binding, lease and uncertain latch.
internal sealed class WorkerProjectScaffold
{
    private readonly string _release;
    private readonly Func<string, JsonObject, JsonNode?> _call;
    private readonly Func<string, JsonObject> _documentPreflight;

    internal WorkerProjectScaffold(string release, Func<string, JsonObject, JsonNode?> call,
        Func<string, JsonObject> documentPreflight)
    {
        _release = release;
        _call = call;
        _documentPreflight = documentPreflight;
    }

    internal static void Register(string release, Action<Func<ProjectScaffoldPrimitive, object>> bind,
        Func<string, JsonObject, JsonNode?> call,
        Func<string, JsonObject> documentPreflight)
        => bind(new WorkerProjectScaffold(release, call, documentPreflight).Execute);

    private JsonObject RuntimeState => Call("ReadState", new JsonObject());
    private JsonObject Call(string operation, JsonObject arguments)
        => _call(operation, arguments) as JsonObject
            ?? throw new InvalidOperationException("Missing scaffold worker reply: " + operation);

    private JsonObject Write(string operation, JsonObject arguments, string? expected = null)
    {
        arguments["dryRun"] = false;
        arguments["confirm"] = true;
        arguments["expectedProjectFile"] = expected ?? (string?)RuntimeState["ProjectFile"] ?? "";
        return Call(operation, arguments);
    }

    private static JsonObject Meta(bool success = true)
        => JsonSerializer.SerializeToNode(LegacyResponseMetadata.Basic(DateTime.Now, success))!.AsObject();
    private static JsonObject Message(string message, JsonObject? meta = null)
        => new JsonObject { ["Message"] = message, ["Meta"] = meta ?? Meta() };

    private static JsonObject RequireFamilyReply(JsonObject reply)
    {
        if (reply["Failure"] is JsonObject failure)
        {
            string code = (string?)failure["Code"] ?? "";
            string message = (string?)failure["Message"] ?? "Scaffold operation failed.";
            if (code == nameof(ArgumentException)) throw new ArgumentException(message);
            if (code == nameof(NotSupportedException)) throw new NotSupportedException(message);
            var error = new PlcSoftwareException(code, message,
                failure["Candidates"] == null ? null : JsonSerializer.Deserialize<string[]>(failure["Candidates"]!.ToJsonString()));
            if (failure["Evidence"] is JsonObject evidence) error.Data["nativeResultEvidence"] = evidence.DeepClone();
            throw error;
        }
        return reply;
    }

    private JsonObject Plc(PlcSoftwareRequest request)
        => RequireFamilyReply(Write(PortedFamilies.ForTool(request.Operation).OperationPrefix
            + ".ExecutePlcOrganisation", new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request) }));

    private JsonObject Documents(PlcB5Request request)
    {
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
        return RequireFamilyReply(request.DryRun
            ? Call("plc-documents.ExecutePlcB5", arguments) : Write("plc-documents.ExecutePlcB5", arguments));
    }

    private JsonObject Unified(UnifiedHmiRequest request)
    {
        if (!string.IsNullOrEmpty(request.DesignJson))
        {
            try { request.Inputs[request.DesignJson] = Input(JsonNode.Parse(request.DesignJson)); }
            catch (JsonException error) { request.Inputs[request.DesignJson] = new UnifiedHmiInput { Kind = "error", Text = error.Message }; }
        }
        var reply = RequireFamilyReply(Write("hmi-unified.ExecuteUnifiedHmi",
            new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request) }));
        return reply["Value"] as JsonObject ?? throw new InvalidOperationException("Missing Unified HMI value.");
    }

    private static UnifiedHmiInput Input(JsonNode? node)
    {
        var result = new UnifiedHmiInput();
        if (node is JsonObject fields)
        {
            result.Kind = "object";
            foreach (var pair in fields) result.Properties[pair.Key] = Input(pair.Value);
        }
        else if (node is JsonArray items)
        {
            result.Kind = "array";
            foreach (var item in items) result.Items.Add(Input(item));
        }
        else if (node != null)
        {
            result.Kind = node.GetValueKind() == JsonValueKind.String ? "string"
                : node.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? "boolean" : "number";
            result.Text = result.Kind == "string" ? node.GetValue<string>() : node.ToJsonString();
        }
        return result;
    }

    internal object Execute(ProjectScaffoldPrimitive request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        var text = request.Text;
        var expected = request.Kind switch {
            "connect" or "save" => 0, "compile" or "hmiInfo" => 1,
            "createProject" or "sclGenerate" or "hmiScreen" => 2,
            "sclImport" or "hmiConnect" or "hmiDesign" => 3,
            "addDevice" or "lad" => 4, "hmiTag" => 8,
            _ => throw new ArgumentException("Unknown scaffold primitive.")
        };
        if (text == null || text.Length != expected || text.Any(value => value == null)
            || request.Numbers == null || request.Numbers.Length != (request.Kind == "hmiScreen" ? 2 : 0)
            || request.Flags == null || request.Flags.Length != (request.Kind is "hmiDesign" or "hmiTag" ? 1 : 0))
            throw new ArgumentException("Invalid scaffold primitive arguments.");
        ProjectScaffoldCapabilities.Require(_release, request.Kind);
        switch (request.Kind)
        {
            case "connect": return Connect();
            case "createProject":
                // Never close an existing project, including a borrowed UI project.
                if (!string.IsNullOrEmpty((string?)RuntimeState["ProjectFile"]))
                    throw new AdapterPreconditionException("CreateProject refused: a project is already bound; explicitly save/close/disconnect first.", "session", false);
                if (_call("ListProjects", new JsonObject()) is not JsonArray projects)
                    throw new InvalidOperationException("Missing scaffold project inventory.");
                if (projects.Count != 0)
                    throw new AdapterPreconditionException("CreateProject refused: TIA Portal already has an open project/session; explicitly bind or disconnect first.", "session", false);
                var directory = Path.GetFullPath(text[0]);
                Directory.CreateDirectory(directory);
                string suffix = _release == "14sp1" ? "14" : _release == "15.1" ? "15_1" : _release;
                Write("CreateProject", new JsonObject { ["directoryPath"] = directory, ["projectName"] = text[1] },
                    Path.Combine(directory, text[1], text[1] + ".ap" + suffix));
                return Message($"Project '{text[1]}' created in '{text[0]}'");
            case "save":
                bool local = (bool?)RuntimeState["IsLocalSession"] == true;
                Write("SaveProject", new JsonObject());
                return Message(local ? "Local session saved" : "Local project saved");
            case "addDevice": return AddDevice(text);
            case "lad": return ImportDocuments(text);
            case "sclImport":
                Source("ImportPlcExternalSource", new JsonObject { ["softwarePath"] = text[0], ["groupPath"] = text[1], ["filePath"] = text[2] });
                return Message("PLC external source imported");
            case "sclGenerate":
                Source("GenerateBlocksFromExternalSource", new JsonObject { ["softwarePath"] = text[0], ["externalSourceName"] = text[1] });
                return Message($"Blocks generated from external source '{text[1]}'");
            case "compile": return Compile(text[0]);
            case "hmiInfo": return HmiInfo(text[0]);
            case "hmiConnect":
                var connection = Unified(new UnifiedHmiRequest { Operation = "EnsureUnifiedHmiConnection", HmiSoftwarePath = text[0], ConnectionName = text[1], PlcName = text[2], DryRun = false });
                connection["Meta"] = Meta();
                return connection;
            case "hmiScreen": return Unified(new UnifiedHmiRequest { Operation = "EnsureUnifiedHmiScreen", HmiSoftwarePath = text[0], ScreenName = text[1], Width = request.Numbers[0], Height = request.Numbers[1], DryRun = false });
            case "hmiDesign": return Unified(new UnifiedHmiRequest { Operation = "ApplyUnifiedHmiScreenDesign", HmiSoftwarePath = text[0], ScreenName = text[1], DesignJson = text[2], Strict = request.Flags[0], DryRun = false });
            case "hmiTag": return Unified(new UnifiedHmiRequest { Operation = "EnsureUnifiedHmiTag", HmiSoftwarePath = text[0], TagTableName = text[1], TagName = text[2], HmiDataType = text[3], PlcName = text[4], PlcTag = text[5], ConnectionName = text[6], Address = text[7], RequireVerifiedBinding = request.Flags[0], DryRun = false });
            default: throw new ArgumentException("Unknown scaffold primitive.");
        }
    }

    private void Source(string operation, JsonObject arguments)
    {
        arguments["dryRun"] = true;
        var plan = Call(operation, arguments);
        arguments["expectedPlanHash"] = plan["PlanHash"]!.DeepClone();
        var result = Write(operation, arguments);
        if ((bool?)result["Executed"] != true)
            throw new InvalidOperationException((string?)result["Error"] ?? "External source operation did not execute.");
    }

    private JsonObject Connect()
    {
        var processes = Call("ReadPortalProcessProjects", new JsonObject())["Processes"]!.AsArray();
        if (processes.Count > 1) throw new AdapterPreconditionException("Multiple TIA instances match. Use ListPortalProcessProjects then ConnectProject with PID, start time and full project path.", "session", false);
        bool started = processes.Count == 0;
        if (started) Call("project-lifecycle.ConnectIsolatedPortal", new JsonObject());
        else
        {
            var selected = processes[0]!;
            Call("Attach", new JsonObject { ["processId"] = selected["ProcessId"]!.DeepClone() });
            string? path = (string?)selected["ProjectPath"];
            if (!string.IsNullOrEmpty(path))
                Call("BindProject", new JsonObject { ["projectName"] = Path.GetFileNameWithoutExtension(path), ["expectedProjectFile"] = path });
        }
        int? pid = (int?)RuntimeState["ProcessId"];
        var meta = Meta(); meta["boundProcessId"] = pid; meta["startedNew"] = started;
        return Message(started ? "Started a new TIA Portal instance (PID " + pid + ") - no TIA Portal was running."
            : "Connected to TIA-Portal (attached to PID " + pid + ").", meta);
    }

    private JsonObject AddDevice(string[] text)
    {
        var probe = Write("hardware-devices.HardwareCreateDeviceFallback", new JsonObject {
            ["preferredMlfb"] = text[0], ["preferredVersion"] = text[1], ["deviceName"] = text[2], ["family"] = text[3] });
        bool ok = probe["Device"] != null;
        var meta = Meta(ok);
        if (ok) meta["name"] = probe["Device"]!["Name"]?.DeepClone();
        return new JsonObject { ["Ok"] = ok, ["DeviceName"] = text[2], ["Family"] = text[3],
            ["MlfbUsed"] = ok ? probe["MlfbUsed"]?.DeepClone() : null,
            ["VersionUsed"] = ok ? probe["VersionUsed"]?.DeepClone() : null,
            ["Attempts"] = probe["Attempts"]?.DeepClone(), ["Error"] = ok ? null : probe["Error"]?.DeepClone(),
            ["Message"] = ok ? $"Device '{text[2]}' added (one exact create call)" : $"Failed to add device '{text[2]}' exactly as requested", ["Meta"] = meta };
    }

    private JsonObject Compile(string path)
    {
        // This existing family action uses the shared structured diagnostic codec.
        var result = Plc(new PlcSoftwareRequest { Operation = "RepairAndReimportPlcBlock", Action = "compile", SoftwarePath = path, DryRun = false })["Compiler"]!.AsObject();
        return new JsonObject { ["State"] = result["State"]?.DeepClone(), ["ErrorCount"] = result["ErrorCount"]?.DeepClone(),
            ["WarningCount"] = result["WarningCount"]?.DeepClone(), ["Errors"] = result["Errors"]?.DeepClone(),
            ["Warnings"] = result["Warnings"]?.DeepClone(), ["Info"] = result["Info"]?.DeepClone(),
            ["RawMessages"] = result["RawMessages"]?.DeepClone(),
            ["Message"] = $"Software '{path}' compile state={result["State"]}; root counts and diagnostics scopes are in Meta.",
            ["Meta"] = result["Meta"]?.DeepClone() };
    }

    private JsonObject HmiInfo(string path)
    {
        var request = new HmiRequest { Operation = "GetHmiProgramInfo", SoftwarePath = path, DryRun = true };
        var reply = RequireFamilyReply(Call("hmi-generic.ExecuteHmi", new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = true }));
        var info = reply["Value"] as JsonObject ?? throw new InvalidOperationException($"HMI program not found at '{path}'");
        return new JsonObject { ["Name"] = info["Name"]?.DeepClone(), ["ProgramType"] = info["ProgramType"]?.DeepClone(),
            ["Screens"] = info["Screens"]?.DeepClone(), ["Message"] = $"HMI program info retrieved from '{path}'", ["Meta"] = Meta() };
    }

    private JsonObject ImportDocuments(string[] text)
    {
        string directory = text[2], name = text[3];
        if (name.Replace('\\', '/').Split('/').Length != 1 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("fileNameWithoutExtension must be one safe document basename.");
        var warnings = new JsonArray();
        try
        {
            var missing = S7ResScanner.GetMissingEnUsIds(directory, name);
            if (missing.Count > 0) warnings.Add(new JsonObject { ["name"] = name,
                ["missingEnUsIds"] = new JsonArray(missing.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) });
        }
        catch (Exception error) { warnings.Add(new JsonObject { ["name"] = name, ["precheckError"] = error.Message }); }
        var preflight = _documentPreflight(File.ReadAllText(Path.Combine(directory, name + ".s7dcl")));
        var reply = Documents(new PlcB5Request { Operation = "ImportPlcBlockDocuments", SoftwarePath = text[0], GroupPath = text[1],
            ImportPath = directory, FileNameWithoutExtension = name, ImportOption = "Override", DryRun = false });
        if ((bool?)reply["Value"] != true) throw new InvalidOperationException($"Failed importing '{name}' from '{directory}'");
        bool verified = false;
        string detail;
        try
        {
            var verify = new PlcB5Request { Operation = "ImportPlcBlockDocuments", Action = "verify", SoftwarePath = text[0], GroupPath = text[1],
                ImportedNames = JsonSerializer.Deserialize<string[]>(reply["ImportedNames"]!.ToJsonString())! };
            verified = (bool?)Documents(verify)["Value"] == true;
            detail = verified ? "Native imported names found in the exact target group; content completeness has not been verified."
                : "Exact target readback failed; imported content has not been verified.";
        }
        catch (Exception error) { detail = "readback skipped: " + error.Message; }
        var meta = Meta(); meta["verified"] = verified; meta["existsVerified"] = verified; meta["contentVerified"] = null;
        meta["verificationScope"] = "exact target group and native ImportedPlcBlocks names";
        meta["formatPreflight"] = preflight; meta["verifyDetail"] = detail; meta["warnings"] = warnings;
        return Message($"Imported '{name}' from '{directory}'" + (verified ? " (verified)" : ""), meta);
    }
}
