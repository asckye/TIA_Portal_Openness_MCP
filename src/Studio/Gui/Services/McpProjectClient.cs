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
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Inspection;
using TiaOpenness.Gui.Localization;

namespace TiaOpenness.Gui.Services;

public sealed class McpProjectClient : IProjectClient
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
    private void OnDetached(object? sender, EventArgs args) => Exited?.Invoke(this, args);
    public void Start(bool forceMock, string opennessVersion)
    {
        if (forceMock) throw new InvalidOperationException("Use an offline operator fixture for demonstrations.");
        if (!IsRunning) throw new InvalidOperationException("Select and attach an MCP session first.");
    }
    internal async Task<JsonObject> Read(string tool, Dictionary<string, object?>? arguments = null, CancellationToken token = default)
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
            throw new InvalidOperationException(code + ": " + EngineResultText.Error(code) + " " + (string?)envelope["error"]?["message"]);
        }
        return envelope["data"] as JsonObject ?? new();
    }
    private bool Supports(string tool, string parameter) => Session.ToolSchema(tool)?["properties"]?[parameter] != null;
    internal async Task<JsonObject> Write(string tool, Dictionary<string, object?> arguments, CancellationToken token = default, string? expectedProject = null)
    {
        string binding = expectedProject ?? (string?)Session.State?["binding"]?["projectFile"] ?? ExpectedProjectFile;
        if ((bool?)Session.State?["sessionLocked"] == true) throw new InvalidOperationException("SESSION_RESET_REQUIRED: " + EngineResultText.Error("SESSION_RESET_REQUIRED"));
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
    public async Task<SessionState> ConnectAsync(bool withUserInterface)
    {
        var data = await Read("ListPortalProcessProjects");
        var processes = (data["processes"] as JsonArray ?? data["items"] as JsonArray ?? new()).Select(Decode).OfType<JsonObject>().ToArray();
        var selected = processes.Where(p => ProcessId > 0 ? (int?)p["processId"] == ProcessId :
            !string.IsNullOrEmpty(ExpectedProjectFile) && string.Equals((string?)p["projectPath"], ExpectedProjectFile, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length != 1) throw new InvalidOperationException("Choose a TIA process ID and exact project file. Start TIA manually if none is listed.");
        int pid = (int)selected[0]["processId"]!;
        string file = (string?)selected[0]["projectPath"] ?? ExpectedProjectFile;
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
        foreach (Match match in Regex.Matches(tree, @"PlcSoftware: (.*?) \[softwarePath=(.*?)\]"))
            devices.Add(new() { Id = match.Groups[2].Value, Name = match.Groups[1].Value, DisplayName = match.Groups[1].Value, Category = "Plc" });
        return devices;
    }
    internal static JsonNode? Decode(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out string? json) && json.StartsWith("{", StringComparison.Ordinal)
        ? JsonNode.Parse(json) : node;
    internal static BlockInfo Block(JsonObject data, string path)
    {
        string name = (string?)data["name"] ?? "";
        var attributes = (data["attributes"] as JsonArray ?? new()).OfType<JsonObject>();
        string? Attribute(string field) => attributes.FirstOrDefault(a => (string?)a["name"] == field)?["value"]?.ToString();
        string kind = (string?)data["typeName"] ?? (string?)data["kind"] ?? "";
        return new() { Name = name, Path = path, FolderPath = path.Contains('/') ? path[..path.LastIndexOf('/')] : "",
            Kind = kind.Contains("OB", StringComparison.Ordinal) ? BlockKind.OB : kind.Contains("FB", StringComparison.Ordinal) ? BlockKind.FB
                : kind.Contains("FC", StringComparison.Ordinal) ? BlockKind.FC : kind.Contains("DB", StringComparison.Ordinal) ? BlockKind.DB : BlockKind.Unknown,
            Number = int.TryParse(Attribute("Number"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int number) ? number : null,
            IsConsistent = (bool?)data["isConsistent"] == true, IsKnowHowProtected = (bool?)data["isKnowHowProtected"] == true,
            ProgrammingLanguage = (string?)data["programmingLanguage"] ?? "", HeaderAuthor = Attribute("HeaderAuthor") ?? "" };
    }
    private static string Escape(string name) => Uri.EscapeDataString(name);
    public async Task<List<BlockInfo>> ListBlocksAsync(string deviceId)
    {
        var data = await Read("GetPlcBlockHierarchy", new() { ["softwarePath"] = deviceId });
        var result = new List<BlockInfo>();
        void Walk(JsonObject group, string path)
        {
            foreach (var block in (group["blocks"] as JsonArray ?? new()).OfType<JsonObject>())
                result.Add(Block(block, (string?)block["path"] ?? path + Escape((string?)block["name"] ?? "")));
            foreach (var child in (group["groups"] as JsonArray ?? new()).OfType<JsonObject>())
                Walk(child, path + Escape((string?)child["name"] ?? "") + "/");
        }
        Walk(data["root"] as JsonObject ?? data, "");
        return result;
    }
    public Task<ExportResult> ExportBlocksAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory, ExportFormat format)
        => ExportAsync(deviceId, blocks, outputDirectory, format, CancellationToken.None);
    internal async Task<ExportResult> ExportAsync(string deviceId, IEnumerable<string> blocks, string outputDirectory, ExportFormat format, CancellationToken token)
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
            var data = await Write("ExportPlcBlock", new() { ["softwarePath"] = deviceId, ["blockPath"] = path, ["exportPath"] = outputDirectory, ["preservePath"] = true }, token);
            string file = (string?)data["outputFile"] ?? (string?)data["filePath"]
                ?? (string?)data["items"]?[0]?["result"]?["data"]?["files"]?[0]?["path"] ?? "";
            result.Items.Add(new() { BlockPath = path, FilePath = file, Succeeded = true }); result.Succeeded++;
        }
        return result;
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
        var data = await Write("CompilePlcSoftware", new() { ["softwarePath"] = deviceId });
        var diagnostics = data["attempt"]?["diagnostics"];
        return new() { State = (string?)data["state"] ?? (string?)diagnostics?["state"] ?? "Unknown",
            ErrorCount = (int?)data["errorCount"] ?? (int?)diagnostics?["rootErrorCount"] ?? 0,
            WarningCount = (int?)data["warningCount"] ?? (int?)diagnostics?["rootWarningCount"] ?? 0 };
    }
    public async Task<InspectionReport> InspectAsync(string deviceId, string? blockNamePattern)
    {
        await Read("ListPlcBlocks", new() { ["softwarePath"] = deviceId });
        var blocks = await ListBlocksAsync(deviceId);
        var facts = new List<BlockInfo>();
        foreach (var block in blocks)
            facts.Add(Block(await Read("GetPlcBlockInfo", new() { ["softwarePath"] = deviceId, ["blockPath"] = block.Path }), block.Path));
        return InspectionEngine.Run(deviceId, facts, new InspectionOptions { BlockNamePattern = blockNamePattern }, referencedNames: null);
    }
    public void Dispose() { Session.Detached -= OnDetached; }
}
