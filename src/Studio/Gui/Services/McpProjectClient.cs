using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Models.Errors;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Inspection;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Services;

public sealed partial class McpProjectClient : IProjectClient
{
    public sealed record Receipt(string Tool, string Phase, string Json)
    { public string Label => Tool + " · " + Phase; }
    public ObservableCollection<Receipt> Receipts { get; } = [];
    public IMcpSessionClient Session { get; }
    public int ProcessId { get; set; }
    public string ExpectedProjectFile { get; set; } = "";
    public string LastEnvelope { get; private set; } = "";
    public event EventHandler? ResultChanged;
    public bool IsRunning => Session.SessionKey != null;
    public bool IsMock => false;
    public event EventHandler<BridgeLogEventArgs>? Log;
    public event EventHandler<ProgressEventArgs>? Progress { add { } remove { } }
    public event EventHandler? Exited;
    public McpProjectClient(IMcpSessionClient session) { Session = session; session.Detached += OnDetached; }
    internal bool LostTransport { get; private set; }
    private void OnDetached(object? sender, EventArgs args)
    {
        LostTransport = args is SessionTransportLostEventArgs;
        Exited?.Invoke(this, args);
    }
    public void Start(bool forceMock, string opennessVersion)
    {
        if (forceMock) throw new InvalidOperationException(Loc.Current["Session.StartDemo"]);
        if (!IsRunning) throw new InvalidOperationException("Select and attach an MCP session first.");
    }
    internal async Task<JsonObject> Read(string tool, Dictionary<string, object?>? arguments = null, CancellationToken token = default)
        => (await ReadEnvelopeAsync(tool, arguments, token))["data"] as JsonObject ?? new();

    private async Task<JsonObject> ReadEnvelopeAsync(string tool, Dictionary<string, object?>? arguments = null, CancellationToken token = default)
    {
        var envelope = await Session.CallAsync(tool, arguments ?? new(), token);
        LastEnvelope = envelope.ToJsonString(new() { WriteIndented = true });
        string phase = arguments?.TryGetValue("mode", out var mode) == true ? mode?.ToString() ?? "result"
            : arguments?.TryGetValue("dryRun", out var dryRun) == true ? dryRun is true ? "preview" : "apply" : "result";
        Receipts.Add(new(tool, phase, LastEnvelope));
        while (Receipts.Count > 100) Receipts.RemoveAt(0);
        ResultChanged?.Invoke(this, EventArgs.Empty);
        Log?.Invoke(this, new() { Line = tool + ": " + envelope.ToJsonString() });
        if ((bool?)envelope["ok"] != true)
        {
            string code = (string?)envelope["error"]?["code"] ?? "INTERNAL_ERROR";
            if (tool == "CompilePlcSoftware" && phase == "apply" && code == "COMPILE_ERRORS"
                && (string?)envelope["meta"]?["execution"] == "completed")
                throw new CompilerErrors(envelope);
            var failure = new InvalidOperationException(code + ": " + EngineResultText.Error(code) + " " + (string?)envelope["error"]?["message"]);
            failure.Data["McpErrorCode"] = code;
            throw failure;
        }
        return envelope;
    }
    private bool Supports(string tool, string parameter) => Session.ToolSchema(tool)?["properties"]?[parameter] != null;
    internal async Task<JsonObject> Write(string tool, Dictionary<string, object?> arguments, CancellationToken token = default, string? expectedProject = null)
    {
        string binding = expectedProject ?? (string?)Session.State?["binding"]?["projectFile"] ?? ExpectedProjectFile;
        bool candidate = Supports(tool, "mode");
        if (Supports(tool, "expectedProjectFile")) arguments["expectedProjectFile"] = binding;
        if (Supports(tool, "confirm")) arguments["confirm"] = false;
        arguments[candidate ? "mode" : "dryRun"] = candidate ? "preview" : true;
        var preview = await Read(tool, arguments, token);
        if (Supports(tool, "expectedPlanHash"))
            arguments["expectedPlanHash"] = (string?)preview["plan"]?["hash"] ?? (string?)preview["planHash"]
                ?? throw new InvalidDataException("The preview did not return the required plan hash.");
        if (Supports(tool, "expectedInventoryHash") && preview["inventoryHash"] is JsonValue inventory)
            arguments["expectedInventoryHash"] = inventory.GetValue<string>();
        arguments[candidate ? "mode" : "dryRun"] = candidate ? "apply" : false;
        if (Supports(tool, "confirm")) arguments["confirm"] = true;
        // Approval is performed by the host's ordinary FoundationV4Tool; the panel cannot grant it.
        return await Read(tool, arguments, token);
    }
    public async Task<IReadOnlyList<ProjectProcessObservation>> ListProcessesAsync()
    {
        var data = await Read("ListPortalProcessProjects");
        var records = ProcessRecords(data);
        var rows = await ProcessRowsAsync(data, records);
        return rows.Select((row, i) => new ProjectProcessObservation(row, ObserveLease(TiaOpenness.Shared.DataLocations.Current.LeasesDirectory,
            row.ProcessId, (string?)records[i]["osStartTimeUtc"]))).ToArray();
    }

    internal static ProjectLeaseState ObserveLease(string directory, int pid, string? startTime)
    {
        if (pid <= 0 || !DateTimeOffset.TryParseExact(startTime, "O", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var start)) return ProjectLeaseState.Unknown;
        // FoundationProcessLease owns PID/start-time files with FileShare.None. Discovery only reads an existing file.
        string name = FormattableString.Invariant($"{pid}-{start.UtcTicks}.lease");
        try
        {
            using var reader = File.Open(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ProjectLeaseState.Available;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (error is FileNotFoundException or DirectoryNotFoundException) return ProjectLeaseState.Available;
            if (error is IOException && (error.HResult & 0xffff) == 32) return ProjectLeaseState.Held;
            System.Diagnostics.Trace.TraceInformation("Cannot observe the lease for PID {0}: {1}", pid, error.Message);
            return ProjectLeaseState.Unknown;
        }
    }

    private static JsonObject[] ProcessRecords(JsonObject data) =>
        (data["processes"] as JsonArray ?? data["items"] as JsonArray ?? new()).Select(Decode).OfType<JsonObject>().ToArray();

    private async Task<PortalProcessInfo[]> ProcessRowsAsync(JsonObject data, JsonObject[] processes)
    {
        if (processes.Length == 0) return [];
        var info = await Read("GetPortalInfo", new() { ["includeProcesses"] = true, ["includeSessions"] = false, ["includeProducts"] = false });
        var modes = (info["evidence"]?["processes"] as JsonArray ?? new()).Select(Decode).OfType<JsonObject>()
            .ToDictionary(p => (int)p["id"]!, p => (string?)p["mode"]);
        return processes.Select(p => new PortalProcessInfo
        {
            ProcessId = (int)p["processId"]!, ProjectPath = (string?)p["projectPath"],
            Release = (string?)data["releaseKey"] ?? (string?)Session.State?["releaseKey"] ?? "",
            UiMode = modes.TryGetValue((int)p["processId"]!, out var mode) ? mode : null,
        }).ToArray();
    }

    public async Task<SessionState> ConnectAsync(bool withUserInterface, int? processId = null)
    {
        var data = await Read("ListPortalProcessProjects");
        var processes = ProcessRecords(data);
        int requested = processId ?? ProcessId;
        var selected = processes.Where(p => requested != 0 ? (int?)p["processId"] == requested :
            !string.IsNullOrEmpty(ExpectedProjectFile) && string.Equals((string?)p["projectPath"], ExpectedProjectFile, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (requested == 0 && selected.Length != 1 && processes.Length > 1)
        {
            throw new PortalProcessSelectionRequiredException(await ProcessRowsAsync(data, processes));
        }
        if (selected.Length != 1) throw new InvalidOperationException("Choose a TIA process ID and exact project file. Start TIA manually if none is listed.");
        int pid = (int)selected[0]["processId"]!;
        string file = (string?)selected[0]["projectPath"] ?? "";
        ProcessId = pid;
        if (Supports("ConnectPortal", "mode"))
            await Write("ConnectPortal", CandidateProcess(selected[0]), expectedProject: "");
        else await Read("ConnectPortal", new() { ["processId"] = pid });
        ProjectInfo? project = null;
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (Supports("AttachOpenProject", "mode"))
            {
                var arguments = CandidateProcess(selected[0]); arguments["projectPath"] = file; arguments["reuseOpen"] = true;
                await Write("AttachOpenProject", arguments, expectedProject: file);
            }
            else await Read("AttachOpenProject", new() { ["projectName"] = Path.GetFileNameWithoutExtension(file), ["expectedProjectFile"] = file });
            ExpectedProjectFile = file; project = new() { Path = file, Name = Path.GetFileNameWithoutExtension(file) };
        }
        return new() { Connected = true, OpennessVersion = (string?)Session.State?["releaseKey"] ?? "", OpenProject = project! };
    }
    private static Dictionary<string, object?> CandidateProcess(JsonObject process)
        => new() { ["processId"] = (int)process["processId"]!, ["processStartUtc"] = (string?)process["osStartTimeUtc"]
            ?? throw new InvalidOperationException("The selected process has no observed OS start time. Refresh its metadata before connecting.") };
    public async Task<SessionState> DisconnectAsync()
    {
        if (Supports("DisconnectPortal", "mode")) await Write("DisconnectPortal", new());
        else await Read("DisconnectPortal");
        return new() { Connected = false };
    }
    public async Task<ProjectInfo> OpenProjectAsync(string path)
    {
        if (Supports("OpenProject", "projectPath"))
        {
            var processes = await Read("ListPortalProcessProjects");
            var process = (processes["processes"] as JsonArray ?? new()).Select(Decode).OfType<JsonObject>().Single(p => (int?)p["processId"] == ProcessId);
            var arguments = CandidateProcess(process); arguments["projectPath"] = path;
            await Write("OpenProject", arguments, expectedProject: path);
        }
        else await Write("OpenProject", new() { ["path"] = path });
        ExpectedProjectFile = path; return new() { Path = path, Name = Path.GetFileNameWithoutExtension(path) };
    }
    public async Task SaveProjectAsync() => await Write("SaveProject", new());
    public async Task<List<DeviceInfo>> ListDevicesAsync()
    {
        var data = await Read("GetProjectTree");
        string tree = (string?)data["value"] ?? (string?)data["projectTree"] ?? "";
        var devices = new List<DeviceInfo>();
        foreach (Match match in Regex.Matches(tree, @"(PlcSoftware|HmiTarget|HmiSoftware): (.*?) \[softwarePath=(.*?)\]"))
            devices.Add(new() { Id = match.Groups[3].Value, Name = match.Groups[2].Value, DisplayName = match.Groups[2].Value,
                TypeName = match.Groups[1].Value switch { "HmiSoftware" => "WinCC Unified", "HmiTarget" => "WinCC Classic", _ => "" },
                Category = match.Groups[1].Value == "PlcSoftware" ? "Plc" : "Hmi" });
        return devices;
    }
    public async Task<IReadOnlyList<string>?> ListScreensAsync(string hmi)
    {
        if (!Supports("ListHmiScreens", "softwarePath")) return null;
        var data = await Read("ListHmiScreens", new() { ["softwarePath"] = hmi });
        return (data["items"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToArray();
    }

    public async Task<(int? Tags, int? Alarms)> HmiCountsAsync(DeviceInfo hmi)
    {
        int? tags = null, alarms = null;
        if (Supports("ListHmiTags", "softwarePath"))
        {
            var inventory = await Read("ListHmiTags", new() { ["softwarePath"] = hmi.Id });
            tags = (inventory["items"] as JsonArray)?.Count;
        }
        if (hmi.TypeName == "WinCC Unified" && Supports("GetUnifiedAlarmCommon", "category"))
        {
            var inventory = await Read("GetUnifiedAlarmCommon", new() { ["softwarePath"] = hmi.Id, ["category"] = "discreteAlarms", ["limit"] = 1 });
            alarms = (int?)inventory["evidence"]?["expectedCount"];
        }
        return (tags, alarms);
    }

    public async Task<IReadOnlyList<SoftwareUnitInfo>> ListUnitsAsync(string plc)
    {
        if (!Supports("ListPlcSoftwareUnits", "includeContents")
            || !TiaMcp.Adapters.Contracts.OpennessApi.PlcUnits.Includes((string?)Session.State?["releaseKey"] ?? "")) return [];
        var units = new List<SoftwareUnitInfo>();
        int offset = 0;
        while (true)
        {
            JsonObject data;
            try { data = await Read("ListPlcSoftwareUnits", new() { ["softwarePath"] = plc, ["offset"] = offset, ["includeContents"] = true }); }
            catch (InvalidOperationException error) when (offset == 0 && Equals(error.Data["McpErrorCode"], "UNSUPPORTED_CAPABILITY"))
            {
                // The release advertises units even when this PLC has no PlcUnitProvider. The host receipt retains the capability result.
                return [];
            }
            var page = data["evidence"] ?? data;
            foreach (var row in (page["records"] as JsonArray ?? []).Select(Decode).OfType<JsonObject>())
            {
                var blocks = (row["blockGroup"]?["blocks"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToArray();
                units.Add(new(row["name"]!.GetValue<string>(), (string?)row["namespacePreset"] ?? "", blocks));
            }
            if (page["nextOffset"]?.GetValue<int>() is not int next) return units;
            if (next <= offset) throw new InvalidOperationException("The software-unit inventory returned a repeated page.");
            offset = next;
        }
    }

    public async Task<IReadOnlyList<BlockMember>> ReadInterfaceAsync(string plc, string block)
    {
        if (!Supports("RenderPlcBlockDocument", "blockPath") || !TiaMcp.Adapters.Contracts.OpennessApi.PlcDocuments.Includes(
            (string?)Session.State?["releaseKey"] ?? "")) return [];
        var page = await Read("RenderPlcBlockDocument", new() { ["softwarePath"] = plc, ["blockPath"] = block });
        string markdown = (string?)page["evidence"]?["markdown"] ?? "";
        var members = new List<BlockMember>();
        bool table = false;
        foreach (string line in markdown.Split('\n'))
        {
            if (!table) { table = line.TrimEnd() == "| Section | Name | Datatype |"; continue; }
            if (line.StartsWith("|---", StringComparison.Ordinal)) continue;
            var match = Regex.Match(line, @"^\| ([^|]*) \| \x60(.*)\x60 \| (.*) \|\r?$");
            if (!match.Success) break;
            members.Add(new(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value.Replace(@"\|", "|"), ""));
        }
        return members;
    }

    internal static JsonNode? Decode(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out string? json) && json
        .StartsWith("{", StringComparison.Ordinal)
        ? JsonNode.Parse(json) : node;
    internal static BlockInfo Block(JsonObject data, string path)
    {
        string name = (string?)data["name"] ?? "";
        var attributes = (data["attributes"] as JsonArray ?? new()).OfType<JsonObject>();
        string? Attribute(string field) => attributes.FirstOrDefault(a => (string?)a["name"] == field)?["value"]?.ToString();
        string kind = (string?)data["typeName"] ?? (string?)data["kind"] ?? "";
        return new() { Name = name, Path = path, FolderPath = path.Contains('/') ? path[..path.LastIndexOf('/')] : "",
            Kind = kind.Contains("OB", StringComparison.Ordinal) ? BlockKind.OB : kind.Contains("FB", StringComparison.Ordinal) ? BlockKind.FB
                : kind.Contains("FC", StringComparison.Ordinal) ? BlockKind.FC : kind.Contains("DB", StringComparison.Ordinal) ? BlockKind.DB : BlockKind
                    .Unknown,
            Number = int.TryParse(data["number"]?.ToString() ?? Attribute("Number"), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int number) ? number : null,
            IsConsistent = (bool?)data["isConsistent"] == true, IsKnowHowProtected = (bool?)data["isKnowHowProtected"] == true,
            ProgrammingLanguage = (string?)data["programmingLanguage"] ?? "", HeaderAuthor = Attribute("HeaderAuthor") ?? "" };
    }
    private static string Escape(string name) => Uri.EscapeDataString(name);
    public async Task<string> DescribeBlockLogicAsync(string softwarePath, string blockPath)
    {
        var data = await Read("DescribePlcBlockLogic", new() { ["softwarePath"] = softwarePath, ["blockPath"] = blockPath });
        if ((string?)data["evidence"]?["xmlContent"] == "interface-only")
            throw new InvalidOperationException((string?)data["summary"] ?? Loc.Current["Project.NoSource"]);
        return (string?)data["readable"] ?? "";
    }

    public async Task<List<TagInfo>> ListPlcTagsAsync(string plc, string table, bool includeUnits)
    {
        bool supportsUnits = Supports("ListPlcTags", "includeUnits");
        // Older releases require a table for each declaration read. Their table inventory returns root names.
        var tables = new List<string> { table };
        if (table.Length == 0 && !(supportsUnits && includeUnits))
        {
            var inventory = await Read("ListPlcTagTables", new() { ["softwarePath"] = plc });
            tables = inventory["items"]!.AsArray()
                .Select(item => TiaMcp.Adapters.Contracts.ObjectPathEncoding.EncodeSegment(item!.GetValue<string>())).ToList();
        }
        var tags = new List<TagInfo>();
        foreach (string path in tables)
        {
            var args = new Dictionary<string, object?> { ["plc"] = plc, ["table"] = path };
            if (supportsUnits) args["includeUnits"] = includeUnits;
            var data = await Read("ListPlcTags", args);
            foreach (var item in data["items"]!.AsArray())
            {
                string identity = item!["path"]!.GetValue<string>();
                tags.Add(new() { Name = item["name"]!.GetValue<string>(), DataType = item["dataType"]!.GetValue<string>(),
                    LogicalAddress = item["value"]!.GetValue<string>(), TableName = identity[..identity.LastIndexOf('/')],
                    Comment = (string?)item["comment"] ?? "" });
            }
        }
        return tags;
    }

    public async Task<List<BlockInfo>> ListBlocksAsync(string deviceId)
    {
        var result = new List<BlockInfo>();
        string? cursor = null;
        do
        {
            var arguments = new Dictionary<string, object?> { ["softwarePath"] = deviceId, ["limit"] = 100 };
            if (cursor != null) arguments["cursor"] = cursor;
            var envelope = await ReadEnvelopeAsync("ListPlcBlocks", arguments);
            foreach (var block in (envelope["data"]?["items"] as JsonArray ?? new()).OfType<JsonObject>())
                result.Add(Block(block, (string?)block["path"] ?? Escape((string?)block["name"] ?? "")));
            cursor = (string?)envelope["meta"]?["paging"]?["nextCursor"];
        } while (!string.IsNullOrEmpty(cursor));
        return result;
    }
    public Task<ExportResult> ExportBlocksAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory, ExportFormat format)
        => ExportAsync(deviceId, blocks, outputDirectory, format, CancellationToken.None);
    internal async Task<ExportResult> ExportAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory, ExportFormat format,
            CancellationToken token, bool stripVolatile = false)
    {
        Directory.CreateDirectory(outputDirectory);
        if (format == ExportFormat.Source)
        {
            var sourcePaths = blocks.ToArray();
            if (sourcePaths.Length == 0) sourcePaths = (await ListBlocksAsync(deviceId)).Select(b => b.Path).ToArray();
            string filePath = Path.Combine(outputDirectory, "blocks.scl");
            var arguments = new Dictionary<string, object?> { ["softwarePath"] = deviceId, ["blockPaths"] = sourcePaths, ["filePath"] = filePath };
            await Write("GeneratePlcSourceFromBlocks", arguments, token);
            return new() { OutputDirectory = outputDirectory, Requested = sourcePaths.Length, Succeeded = sourcePaths.Length,
                Items = sourcePaths.Select(path => new ExportedItem { BlockPath = path, FilePath = filePath, Succeeded = true }).ToList() };
        }
        var paths = blocks.ToArray();
        if (paths.Length == 0) paths = (await ListBlocksAsync(deviceId)).Select(b => b.Path).ToArray();
        var result = new ExportResult { OutputDirectory = outputDirectory, Requested = paths.Length };
        foreach (string path in paths)
        {
            string file = await ExportBlockXmlAsync(deviceId, path, outputDirectory, null, stripVolatile, token);
            result.Items.Add(new() { BlockPath = path, FilePath = file, Succeeded = true }); result.Succeeded++;
        }
        return result;
    }
    internal bool SupportsVolatileFilter => Supports("ExportPlcBlock", "stripVolatile");
    internal bool SupportsUnitExport => Supports("ExportPlcBlock", "unitName");
    internal async Task<string> ExportBlockXmlAsync(string plc, string path, string directory, string? unit, bool stripVolatile,
        CancellationToken token = default)
    {
        var arguments = new Dictionary<string, object?> { ["softwarePath"] = plc, ["blockPath"] = path, ["exportPath"] = directory, ["preservePath"] = true };
        if (unit != null)
        {
            if (!SupportsUnitExport) throw new InvalidOperationException(Loc.Current["Project.MissingExport"]);
            arguments["unitName"] = unit;
        }
        if (stripVolatile && SupportsVolatileFilter) arguments["stripVolatile"] = true;
        var data = await Write("ExportPlcBlock", arguments, token);
        return (string?)data["outputFile"] ?? (string?)data["filePath"] ?? (string?)data["items"]?[0]?["result"]?["data"]?["files"]?[0]?["path"] ?? "";
    }

    internal bool SupportsVerifiedImport => Supports("ImportPlcBlockVerified", "previewToken");
    internal async Task<bool> ImportVerifiedAsync(string plc, string block, string file, string directory, Func<string, bool> review)
    {
        var arguments = new Dictionary<string, object?> { ["softwarePath"] = plc, ["blockPath"] = block, ["importPath"] = file,
            ["evidenceDirectory"] = directory, ["dryRun"] = true };
        var preview = await Read("ImportPlcBlockVerified", arguments);
        var evidence = preview["evidence"];
        string? token = (string?)evidence?["token"];
        string? backup = (string?)evidence?["backup"]?["path"];
        string? planned = (string?)evidence?["plannedPath"];
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(backup) || string.IsNullOrEmpty(planned))
            throw new InvalidDataException(Loc.Current["Project.ImportEvidenceMissing"]);
        if (!review(string.Join("\n", plc + " / " + block, file, backup, planned))) return false;
        arguments["dryRun"] = false;
        arguments["previewToken"] = token;
        var applied = await Read("ImportPlcBlockVerified", arguments);
        if ((bool?)applied["evidence"]?["verificationSuccess"] != true)
            throw new InvalidDataException(Loc.Current["Project.ReadbackFailed"]);
        return true;
    }

    public async Task<ExportResult> ImportBlocksAsync(string deviceId, IEnumerable<string> files, bool overwrite)
    {
        var result = new ExportResult();
        foreach (string file in files)
        {
            result.Requested++;
            await Write("ImportPlcBlock", new() { ["softwarePath"] = deviceId, ["groupPath"] = "", ["importPath"] = file, ["overwrite"] = overwrite });
            result.Succeeded++; result.Items.Add(new() { FilePath = file, Succeeded = true });
        }
        return result;
    }
    public async Task<CompileResult> CompileAsync(string deviceId)
    {
        JsonObject data;
        JsonNode? details = null;
        try { data = await Write("CompilePlcSoftware", new() { ["softwarePath"] = deviceId }); }
        catch (CompilerErrors error)
        {
            data = error.Envelope["data"] as JsonObject ?? new();
            details = error.Envelope["error"]?["details"];
        }
        var evidence = data["evidence"];
        var diagnostics = data["attempt"]?["diagnostics"];
        var result = new CompileResult
        {
            State = details != null ? "Error" : (string?)evidence?["effectiveState"] ?? (string?)data["state"] ?? (string?)diagnostics?["state"] ?? "Unknown",
            ErrorCount = (int?)details?["errorCount"] ?? (int?)data["errorCount"]
                ?? (int?)evidence?["rootCounts"]?["errors"] ?? (int?)diagnostics?["rootErrorCount"] ?? 0,
            WarningCount = (int?)details?["warningCount"] ?? (int?)data["warningCount"]
                ?? (int?)evidence?["rootCounts"]?["warnings"] ?? (int?)diagnostics?["rootWarningCount"] ?? 0,
        };
        var messages = data["diagnosticMessages"] ?? evidence?["nodes"] ?? data["nodes"] ?? diagnostics?["messages"] ?? data["messages"];
        if (messages is JsonArray array) ParseCompilerMessages(array, result.Messages, "");
        if (details?["errors"] is JsonArray failures && !result.Messages.Any(m => m.Severity == CompileSeverity.Error))
            foreach (var item in failures.OfType<JsonObject>())
                result.Messages.Add(new() { Severity = CompileSeverity.Error, Target = (string?)item["path"] ?? "",
                    Description = (string?)item["description"] ?? (string?)item["message"] ?? "" });
        return result;
    }

    private sealed class CompilerErrors(JsonObject envelope) : Exception((string?)envelope["error"]?["message"])
    {
        internal JsonObject Envelope { get; } = envelope;
    }

    private static void ParseCompilerMessages(JsonArray nodes, List<CompileMessage> messages, string parent)
    {
        var descendants = nodes.OfType<JsonObject>().Select(node => (string?)node["parentPath"]).ToHashSet(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            string? state, description, path, code = null;
            if (node is JsonObject fields)
            {
                state = (fields["State"] ?? fields["state"])?.ToString();
                description = (fields["Description"] ?? fields["description"])?.ToString();
                code = fields["ErrorCode"]?.ToString();
                path = (fields["Path"] ?? fields["path"] ?? fields["ObjectPath"])?.ToString();
                if (string.IsNullOrEmpty(path))
                    path = paths.GetValueOrDefault((string?)fields["parentPath"] ?? "", parent);
                if (fields["treePath"] is JsonValue address)
                {
                    // The native collector emits parents first and locates children by collection path.
                    string collection = address + "/messages";
                    paths[collection] = path;
                    if (descendants.Contains(collection)) continue;
                }
                if ((fields["Messages"] ?? fields["messages"]) is JsonArray { Count: > 0 } children)
                {
                    ParseCompilerMessages(children, messages, path);
                    continue;
                }
            }
            else
            {
                // Native descriptions may contain semicolons. Split only at a named diagnostic field.
                string line = node?.ToString() ?? "";
                string Field(string key) => Regex.Match(line,
                    @"(?:^|; )" + key + @"=(.*?)(?=; (?:State|Description|Path|DateTime|ErrorCount|WarningCount)=|$)", RegexOptions.Singleline).Groups[1].Value;
                state = Field("State");
                description = line.StartsWith("State=", StringComparison.Ordinal) ? Field("Description") : line;
                path = Field("Path");
            }
            if (string.IsNullOrWhiteSpace(description)) continue;
            messages.Add(new()
            {
                Target = string.IsNullOrEmpty(path) ? parent : path, Description = description, ErrorCode = code ?? "",
                Severity = Enum.TryParse<CompileSeverity>(state, true, out var severity) ? severity : CompileSeverity.Information,
            });
        }
    }
    public async Task<InspectionReport> InspectAsync(string deviceId, string? blockNamePattern)
    {
        var blocks = await ListBlocksAsync(deviceId);
        var facts = new List<BlockInfo>();
        foreach (var block in blocks)
            facts.Add(Block(await Read("GetPlcBlockInfo", new() { ["softwarePath"] = deviceId, ["blockPath"] = block.Path }), block.Path));
        return InspectionEngine.Run(deviceId, facts, new InspectionOptions { BlockNamePattern = blockNamePattern }, referencedNames: null);
    }
    public void Dispose() { Session.Detached -= OnDetached; }
}
