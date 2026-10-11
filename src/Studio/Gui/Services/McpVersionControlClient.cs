using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Inspection;

namespace TiaOpenness.Gui.Services;

/// <summary>VCI uses the attached MCP tools; older releases retain only the fenced read bridge.</summary>
internal sealed class McpVersionControlClient(McpProjectClient project, IVersionControlClient? readBridge = null) : IVersionControlClient
{
    private readonly IVersionControlClient bridge = readBridge ?? new ReadOnlyVciBridge(project.Session);
    private string Release => (string?)project.Session.State?["releaseKey"] ?? "";
    private bool Engine => Release is "20" or "21";
    public bool CanRead => project.Session.SessionKey != null && Release is "16" or "17" or "18" or "19" or "20" or "21"
        && (int?)project.Session.State?["binding"]?["processId"] > 0 && (bool?)project.Session.State?["sessionLocked"] != true;
    public bool CanWrite => CanRead && Engine;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() } };
    private static T Model<T>(JsonObject data) where T : new() => data.Deserialize<T>(Json) ?? new();
    private static JsonObject Evidence(JsonObject data) => data["evidence"] as JsonObject
        ?? data["attempt"]?["nativeResult"] as JsonObject ?? data;
    private async Task<JsonObject> Read(string tool, Dictionary<string, object?> arguments)
    {
        if (!CanRead) throw new InvalidOperationException("The attached session has no readable VCI binding.");
        if (project.Session.ToolSchema(tool)?["properties"]?["mode"] != null)
            return await project.Write(tool, await CandidateArguments(tool, arguments));
        return await project.Read(tool, arguments);
    }
    private Dictionary<string, object?> Target(string tool, string? name)
    {
        var arguments = new Dictionary<string, object?>();
        var properties = project.Session.ToolSchema(tool)?["properties"];
        if (properties?["route"] != null) arguments["route"] = name ?? "";
        else if (properties?["workspaceName"] != null) arguments["workspaceName"] = name ?? "";
        return arguments;
    }
    private async Task<JsonObject> Write(string tool, Dictionary<string, object?> arguments, bool dryRun)
    {
        if (!CanWrite) throw new InvalidOperationException("VCI bridge reads only; writes require the MCP VCI tools.");
        if (!dryRun) return await Apply(tool, arguments);
        arguments = await CandidateArguments(tool, arguments);
        bool candidate = project.Session.ToolSchema(tool)?["properties"]?["mode"] != null;
        arguments[candidate ? "mode" : "dryRun"] = candidate ? "preview" : true;
        if (project.Session.ToolSchema(tool)?["properties"]?["expectedProjectFile"] != null)
            arguments["expectedProjectFile"] = (string?)project.Session.State?["binding"]?["projectFile"] ?? "";
        return await project.Read(tool, arguments);
    }
    private async Task<Dictionary<string, object?>> CandidateArguments(string tool, Dictionary<string, object?> arguments)
    {
        if (project.Session.ToolSchema(tool)?["properties"]?["route"] == null) return arguments;
        string name = arguments.TryGetValue("route", out var selected) ? selected?.ToString() ?? "" : "";
        var discovery = new Dictionary<string, object?>(arguments) { ["mode"] = "preview", ["route"] = "",
            ["expectedProjectFile"] = (string?)project.Session.State?["binding"]?["projectFile"] ?? "" };
        var data = await project.Read(tool, discovery);
        string prefix = tool is "CreateVersionControlWorkspace" or "ListVersionControlWorkspaces" ? "vci/project/"
            : "vci/workspace/" + Uri.EscapeDataString(name) + "/";
        var routes = (data["observedState"]?["routes"] as JsonArray ?? new()).OfType<JsonObject>()
            .Select(r => (string?)r["id"]).Where(r => r != null && r.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (routes.Length != 1) throw new InvalidOperationException("Select one exact VCI workspace route from the refreshed MCP inventory.");
        arguments["route"] = routes[0];
        return arguments;
    }
    private async Task<JsonObject> Apply(string tool, Dictionary<string, object?> arguments)
    {
        var properties = project.Session.ToolSchema(tool)?["properties"];
        if (properties?["mode"] != null || properties?["dryRun"] != null)
            return await project.Write(tool, await CandidateArguments(tool, arguments));
        string binding = project.Session.SessionKey + ":" + project.Session.State?["binding"];
        var preview = await project.Read("PreviewToolCall", new() { ["name"] = tool, ["arguments"] = new Dictionary<string, object?>(arguments) });
        if ((bool?)preview["ok"] == false)
            throw new InvalidOperationException("The VCI call did not pass the MCP argument preview.");
        if (binding != project.Session.SessionKey + ":" + project.Session.State?["binding"])
            throw new InvalidOperationException("The shared project changed after preview.");
        return await project.Read(tool, arguments);
    }
    public async Task<bool> VcSupportedAsync() => CanRead && (Engine
        ? project.Session.ToolSchema("ListVersionControlWorkspaces") != null : await bridge.VcSupportedAsync());
    public async Task<List<WorkspaceInfo>> VcListWorkspacesAsync()
    {
        if (!Engine) return await bridge.VcListWorkspacesAsync();
        var data = Evidence(await Read("ListVersionControlWorkspaces", new()));
        return (data["workspaces"] as JsonArray ?? data["items"] as JsonArray ?? new())
            .Select(McpProjectClient.Decode).OfType<JsonObject>().Select(Model<WorkspaceInfo>).ToList();
    }
    public async Task<WorkspaceInfo> VcCreateWorkspaceAsync(string name, string folderPath)
    {
        await Write("CreateVersionControlWorkspace", new() { ["workspaceName"] = name, ["folderPath"] = folderPath }, false);
        return new() { Name = name, RootPath = folderPath };
    }
    public async Task<MappingResult> VcMapProjectAsync(string? name, string? deviceId, bool dryRun)
    {
        const string tool = "ConnectProjectToWorkspace";
        var arguments = Target(tool, name); arguments["deviceFilter"] = deviceId ?? "";
        var data = Evidence(await Write(tool, arguments, dryRun));
        var summary = (JsonObject)data.DeepClone(); summary.Remove("items");
        var result = Model<MappingResult>(summary); result.DryRun = dryRun;
        result.WorkspaceName ??= name ?? "";
        result.Items = (data["items"] as JsonArray ?? new()).OfType<JsonObject>().Select(row => new MappingItem
        {
            Target = (string?)row["target"] ?? (string?)row["object"] ?? "",
            Outcome = (string?)row["outcome"] ?? (string?)row["state"] ?? (string?)row["execution"] ?? "",
            FileFormat = (string?)row["fileFormat"] ?? (string?)row["format"] ?? "",
            Directory = (string?)row["directory"] ?? "", Error = (string?)row["error"] ?? "",
        }).ToList();
        if (data["visited"] == null)
        {
            result.Visited = result.Items.Count;
            result.Mapped = result.Items.Count(i => i.Outcome is "mapped" or "completed");
            result.AlreadyMapped = result.Items.Count(i => i.Outcome is "already mapped" or "already-mapped");
            result.Unsupported = result.Items.Count(i => i.Outcome == "unsupported");
            result.Failed = result.Items.Count(i => i.Outcome is "failed" or "unknown");
        }
        return result;
    }
    public async Task<WorkspaceStatusReport> VcStatusAsync(string? name, bool changedOnly)
    {
        if (!Engine) return await bridge.VcStatusAsync(name, changedOnly);
        const string tool = "GetVersionControlStatus";
        var arguments = Target(tool, name); arguments["changedOnly"] = changedOnly;
        var data = Evidence(await Read(tool, arguments));
        var summary = (JsonObject)data.DeepClone(); summary.Remove("items");
        var report = Model<WorkspaceStatusReport>(summary);
        report.Items = (data["objects"] as JsonArray ?? data["items"] as JsonArray ?? new()).OfType<JsonObject>().Select(row =>
        {
            var item = Model<MappedObjectInfo>(row);
            item.Name ??= (string?)row["object"] ?? "";
            item.CompareState = Enum.TryParse((string?)row["compareState"] ?? (string?)row["state"], true, out VcCompareState state)
                ? state : VcCompareState.Unknown;
            return item;
        }).ToList();
        report.WorkspaceName ??= name ?? "";
        if (data["total"] == null) report.Total = report.Items.Count;
        if (data["differing"] == null) report.Differing = report.Items.Count(i => i.CompareState != VcCompareState.Equal);
        return report;
    }
    public async Task<WorkspaceDiff> VcDiffAsync(string? name, string? file)
    {
        if (!Engine) return await bridge.VcDiffAsync(name, file);
        var workspace = (await VcListWorkspacesAsync()).SingleOrDefault(w => w.Name == name);
        return workspace == null ? new() { Available = false } : GitWorkspaceDiff.Read(workspace.Name, workspace.RootPath, file);
    }
    public async Task<SyncResult> VcSyncAsync(string? name, SyncDirection direction, bool dryRun)
    {
        const string tool = "SynchronizeVersionControlWorkspace";
        var arguments = Target(tool, name); arguments["direction"] = direction.ToString();
        var data = Evidence(await Write(tool, arguments, dryRun));
        var summary = (JsonObject)data.DeepClone(); summary.Remove("items");
        var result = Model<SyncResult>(summary);
        result.Direction = direction; result.DryRun = dryRun;
        result.WorkspaceName ??= name ?? "";
        result.Items = (data["items"] as JsonArray ?? new()).OfType<JsonObject>().Select(row => new SyncItem
        {
            Name = (string?)row["name"] ?? (string?)row["object"] ?? "",
            Outcome = (string?)row["outcome"] ?? (string?)row["execution"] ?? "",
            Error = (string?)row["error"] ?? "",
        }).ToList();
        if (data["synchronized"] == null)
        {
            result.Synchronized = result.Items.Count(i => i.Outcome == "completed");
            result.Failed = result.Items.Count(i => i.Outcome is "failed" or "unknown");
            result.SkippedEqual = (data["items"] as JsonArray ?? new()).OfType<JsonObject>().Count(i => (string?)i["stateBefore"] == "Equal");
        }
        return result;
    }
    public void Dispose() => bridge.Dispose();
}
