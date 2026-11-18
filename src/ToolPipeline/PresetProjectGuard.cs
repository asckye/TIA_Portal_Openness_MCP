using System;
using System.IO;
using System.Text.Json.Nodes;
using TiaMcp.Logic.Generation;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost;

internal static class PresetProjectGuard
{
    internal static Error? Check(string tool, JsonObject arguments, string? boundProject = null, bool requireBound = false)
    {
        var context = EngineHostConfiguration.CurrentOrNull;
        if (context == null || !context.Presets.TryGetValue("expectedProjectFile", out var preset)) return null;
        boundProject ??= (string?)context.Worker.Binding?["identity"]?["projectPath"]
            ?? (string?)context.Worker.Binding?["projectPath"];
        Error Refuse() => new Error("The target or bound project differs from --preset-project-file (" + preset + "). The server preset requires this project.",
            new PreconditionFailedDetails("server-project-preset", preset));
        string StringValue(JsonNode? node) => node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
        string Text(string key) => StringValue(arguments[key]);
        if (tool is "CallTool" or "PreviewToolCall")
            return Check(Text("name"), arguments["arguments"] as JsonObject ?? new JsonObject(), boundProject);
        if (tool is "RunReadOnlyToolBatch" or "PreviewToolBatch" && arguments["operations"] is JsonArray operations)
            foreach (var operation in operations)
                if (operation is JsonObject item && item["arguments"] is JsonObject input
                    && Check(StringValue(item["name"]), input, boundProject) is { } refusal) return refusal;
        bool write = requireBound || tool is "SaveProject" or "CloseProject"
            || context.Catalog?.Find(tool)?.Classification?.Operation is "WRITE" or "ONLINE-WRITE"
            || ToolMetadata.Find(tool)?.Operation is "WRITE" or "ONLINE-WRITE";
        if (write && (boundProject == null || !PresetProjectPath.Same(preset, boundProject))) return Refuse();
        bool execute = arguments["dryRun"] is JsonValue mode && mode.TryGetValue<bool>(out var dryRun) && !dryRun;
        string? target = null;
        switch (tool)
        {
            case "RetrieveProjectArchive":
                // Its existing-file preset cannot be the unknown project inside a new retrieval directory.
                return execute ? Refuse() : null;
            case "SaveProjectCopy":
                if (string.IsNullOrEmpty(boundProject) || !PresetProjectPath.Same(preset, boundProject)) return Refuse();
                target = Path.Combine(Text("newProjectPath"), Path.GetFileName(boundProject)); break;
            case "OpenProject": target = Text("path"); break;
            case "AttachOpenProject": target = Text("expectedProjectFile"); break;
            case "ConnectPortal":
                return boundProject != null && !PresetProjectPath.Same(preset, boundProject) ? Refuse() : null;
            case "BuildProjectScaffold":
                if (!execute) return null;
                JsonObject? spec;
                try { spec = arguments["spec"] as JsonObject
                    ?? (arguments["spec"] is JsonValue value && value.TryGetValue<string>(out var json) ? JsonNode.Parse(json) as JsonObject : null); }
                catch (System.Text.Json.JsonException) { /* swallow(parse-fallback): an invalid spec cannot establish the preset target. */ return Refuse(); }
                if (spec == null) return Refuse();
                try { target = GenerationProjectPath.CreationFile(EngineHostConfiguration.ReleaseKey,
                    spec["directoryPath"] == null ? Path.GetTempPath() : StringValue(spec["directoryPath"]), StringValue(spec["projectName"])); }
                catch (ArgumentException) { /* swallow(parse-fallback): an invalid creation path cannot match the preset. */ return Refuse(); }
                break;
            case "CreateProject":
                try { target = GenerationProjectPath.CreationFile(EngineHostConfiguration.ReleaseKey, Text("directoryPath"), Text("projectName")); }
                catch (ArgumentException) { /* swallow(parse-fallback): an invalid creation path cannot match the preset. */ return Refuse(); }
                break;
        }
        if (target != null) return PresetProjectPath.Same(preset, target) ? null : Refuse();
        return null;
    }

    internal static Error? AfterBinding(string tool)
        => tool is "ConnectPortal" or "AttachOpenProject" or "OpenProject"
            ? Check(tool == "ConnectPortal" ? tool : "", new JsonObject(), requireBound: tool != "ConnectPortal") : null;
}
