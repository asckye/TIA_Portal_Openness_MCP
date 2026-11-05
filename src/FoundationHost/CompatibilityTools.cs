#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
using VersionPolicyTool = enginehost::TiaMcpServer.ModelContextProtocol.VersionPolicyTool;
using AuditCallTool = enginehost::TiaMcpServer.ModelContextProtocol.AuditCallTool;
#else
using VersionPolicyTool = TiaMcpServer.ModelContextProtocol.VersionPolicyTool;
using AuditCallTool = TiaMcpServer.ModelContextProtocol.AuditCallTool;
#endif
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

// Compatibility names dispatch the already wrapped canonical tool. They own no native calls, approval or result mapping.
internal static class CompatibilityTools
{
    internal sealed record Mapping(string Target, IReadOnlyDictionary<string, string> Parameters, IReadOnlyDictionary<string, string> Fixed);
    internal static readonly IReadOnlyDictionary<string, Mapping> Mappings = new Dictionary<string, Mapping>(StringComparer.Ordinal) {
        ["CreateDevice"] =
            new("CreateHardwareDevice", new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["orderNumber"] = "preferredMlfb", ["version"] = "preferredVersion" },
                new Dictionary<string, string>(StringComparer.Ordinal)),
        ["CreateHardwareCatalogDevice"] =
            new("CreateHardwareDevice", new Dictionary<string, string>(StringComparer.Ordinal) { ["keyword"] = "preferredMlfb" }, new Dictionary<string,
                    string>(StringComparer.Ordinal) {
                ["preferredVersion"] = "" }),
        ["CreatePlcBlockGroup"] = Group("blocks", "create"),
        ["CreatePlcTypeGroup"] = Group("types", "create"),
        ["DeleteEmptyPlcBlockGroup"] = Group("blocks", "deleteEmpty")
    };
    private static Mapping Group(string family, string action) => new("ManagePlcUserGroup", new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal) { ["family"] = family, ["action"] = action, ["newName"] = "" });

    internal static void Apply(List<McpServerTool> tools, string release, Func<JsonObject?>? identity = null)
    {
        var roster = tools.ToDictionary(t => t.ProtocolTool.Name, StringComparer.Ordinal);
        for (int index = 0; index < tools.Count; index++)
        {
            var source = tools[index];
            if (source.ProtocolTool.Name is "ManagePlcTagDefinition" or "ManagePlcExternalSources")
            {
                tools[index] = new AuditCallTool(new VersionPolicyTool(new ActionForwarder(source, roster, release)));
                continue;
            }
            if (!Mappings.TryGetValue(source.ProtocolTool.Name, out var mapping)) continue;
            // HardwareCatalog.Find is absent before V18; those SDKs retain the direct CreateWithItem route.
            if (source.ProtocolTool.Name == "CreateDevice" && release is "14sp1" or "15.1" or "16" or "17") continue;
            if (!roster.TryGetValue(mapping.Target, out var target))
                throw new InvalidDataException("Missing canonical implementation for " + source.ProtocolTool.Name + ": " + mapping.Target);
            tools[index] = new AuditCallTool(new VersionPolicyTool(new Forwarder(source.ProtocolTool, target, mapping, release, identity)));
        }
    }

    internal sealed class Forwarder : McpServerTool
    {
        private readonly McpServerTool target;
        private readonly Mapping mapping;
        private readonly string release;
        private readonly Tool tool;
        private readonly InputSchema inputSchema;
        private readonly Func<JsonObject?>? identity;
        internal Forwarder(Tool source, McpServerTool target, Mapping mapping, string release, Func<JsonObject?>? identity = null)
        {
            this.target = target; this.mapping = mapping; this.release = release; this.identity = identity;
            var schema = JsonNode.Parse(target.ProtocolTool.InputSchema.GetRawText())!.AsObject();
            var properties = schema["properties"]!.AsObject();
            var required = schema["required"]?.AsArray();
            foreach (var fixedParameter in mapping.Fixed.Keys)
            {
                properties.Remove(fixedParameter);
                if (required != null) foreach (var value in required.Where(v => (string?)v == fixedParameter).ToArray()) required.Remove(value);
            }
            foreach (var parameter in mapping.Parameters)
            {
                var value = properties[parameter.Value]!.DeepClone();
                properties.Remove(parameter.Value); properties[parameter.Key] = value;
                if (required != null) for (int i = 0; i < required.Count; i++) if ((string?)required[i] == parameter.Value) required[i] = parameter.Key;
            }
            if (source.Name == "CreateHardwareCatalogDevice") properties["preferredText"] = new JsonObject {
                ["type"] = "string", ["default"] = "", ["description"] = "Compatibility hint only; exact keyword is passed unchanged. No ranking or fallback."
            };
            tool = new Tool { Name = source.Name, Title = source.Title,
                Description = source.Description + " Compatibility forwarder to " + mapping.Target
                    + "; identical preview, approval, diagnostics and result envelope. "
                    + string.Join("; ", mapping.Parameters.Select(p => p.Key + " -> " + p.Value).Concat(mapping.Fixed.Select(p => p.Key + "=" + p.Value))),
                InputSchema = JsonSerializer.SerializeToElement(schema), OutputSchema = target.ProtocolTool.OutputSchema,
                Annotations = target.ProtocolTool.Annotations, Meta = target.ProtocolTool.Meta };
            inputSchema = new InputSchema(tool.InputSchema);
        }
        public override Tool ProtocolTool => tool;
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken =
                default)
        {
            var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
            var input = JsonSerializer.SerializeToElement(arguments);
            var error = inputSchema.Validate(input, "arguments");
            if (error != null) return FoundationV4Result.Reject(release, tool.Name, Guid.NewGuid().ToString("N"), error);
            // Retain the compatibility entry's portal admission before delegating to the canonical implementation.
            if (tool.Name == "CreatePlcBlockGroup" && identity != null && identity()?["processId"] == null)
                return FoundationV4Result.Reject(release, tool.Name, Guid.NewGuid().ToString("N"),
                    new Error("Connect to a TIA Portal project first.", new PreconditionFailedDetails("connect-portal", null)), true);
            var adapted = new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal);
            if (tool.Name == "CreateHardwareCatalogDevice") adapted.Remove("preferredText");
            foreach (var parameter in mapping.Parameters)
                if (adapted.Remove(parameter.Key, out var value)) adapted.Add(parameter.Value, value);
            foreach (var parameter in mapping.Fixed) adapted.Add(parameter.Key, JsonSerializer.SerializeToElement(parameter.Value));
            using var approval = GenerationStepApproval.EnterForwardedTool(tool.Name, JsonSerializer.SerializeToNode(arguments)!.AsObject(),
                target.ProtocolTool.Name, JsonSerializer.SerializeToNode(adapted)!.AsObject());
            var original = request.Params;
            request.Params = new CallToolRequestParams { Name = target.ProtocolTool.Name, Arguments = adapted, ProgressToken = original?.ProgressToken };
            try { return await target.InvokeAsync(request, cancellationToken); }
            finally { request.Params = original; }
        }
    }
    internal sealed class ActionForwarder : McpServerTool
    {
        private readonly McpServerTool original;
        private readonly IReadOnlyDictionary<string, McpServerTool> roster;
        private readonly string release;
        private readonly Tool tool;
        private readonly InputSchema inputSchema;
        private static readonly string[] ConfirmationFields = { "confirm", "expectedPlanHash", "expectedProjectFile" };
        internal ActionForwarder(McpServerTool original, IReadOnlyDictionary<string, McpServerTool> roster, string release)
        {
            this.original = original; this.roster = roster; this.release = release;
            var source = original.ProtocolTool;
            var schema = JsonNode.Parse(source.InputSchema.GetRawText())!.AsObject();
            var properties = schema["properties"]!.AsObject();
            foreach (string field in ConfirmationFields) properties[field] = new JsonObject {
                ["type"] = field == "confirm" ? "boolean" : "string",
                ["default"] = field == "confirm" ? JsonValue.Create(false) : JsonValue.Create(""),
                ["description"] = "Canonical Foundation confirmation for forwarded actions; preview first."
            };
            tool = new Tool {
                Name = source.Name, Description = source.Description + " Canonical targets: " + TiaMcp.Adapters.Contracts.PortedFamilies
                    .CompatibilityTargets[source.Name]
                    + ". Ordinary root create/import/delete/generate/group actions forward to the canonical Foundation tools with their confirmation fields " +
                        "and result. Unit scopes and advanced generation options retain their distinct official API route. Tag creation with extra " +
                        "properties is refused: create first, then preview update.",
                Title = source.Title, InputSchema = JsonSerializer.SerializeToElement(schema), OutputSchema = source.OutputSchema,
                Annotations = source.Annotations, Meta = source.Meta };
            inputSchema = new InputSchema(tool.InputSchema);
        }
        public override Tool ProtocolTool => tool;
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken =
                default)
        {
            var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
            var error = inputSchema.Validate(JsonSerializer.SerializeToElement(arguments), "arguments");
            if (error != null) return Reject(error);
            string Text(string name, string fallback = "") => arguments.TryGetValue(name, out var value) ? value.GetString() ?? fallback : fallback;
            bool Flag(string name, bool fallback = false) => arguments.TryGetValue(name, out var value) ? value.GetBoolean() : fallback;
            var adapted = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            void Copy(string source, string destination) { if (arguments.TryGetValue(source, out var value)) adapted[destination] = value; }
            void Set(string key, string value) => adapted[key] = JsonSerializer.SerializeToElement(value);
            string? target = null;
            string action = Text("action");
            if (tool.Name == "ManagePlcTagDefinition" && action == "create")
            {
                if (arguments.TryGetValue("properties", out var properties) && properties.ValueKind == JsonValueKind.Object && properties.EnumerateObject()
                        .Any())
                    return Reject(new Error("Create the declaration first, then preview ManagePlcTagDefinition update for extra properties.",
                                new UnsupportedCapabilityDetails(release, tool.Name, "create-with-properties")));
                target = Text("kind") == "tag" ? "CreatePlcTag" : "CreatePlcUserConstant";
                Copy("softwarePath", "plc"); Copy("tablePath", "table"); Copy("name", "name"); Copy("dataType", "dataType");
                Copy("addressOrValue", target == "CreatePlcTag" ? "address" : "value");
            }
            if (tool.Name == "ManagePlcExternalSources" && Text("unitName") == "")
            {
                if (action is "createGroup" or "renameGroup" or "deleteGroup")
                {
                    if (action == "deleteGroup" && !Flag("dryRun", true) && !Flag("confirmDelete"))
                        return Reject(new Error("confirmDelete=true is required.", new InvalidArgumentDetails("confirmDelete", Array.Empty<string>())));
                    target = "ManagePlcUserGroup"; Set("family", "externalSources");
                    Set("groupPath", string.Join("/", new[] { Text("groupPath"), Text("name") }.Where(part => part.Length > 0)));
                    Set("action", action == "createGroup" ? "create" : action == "renameGroup" ? "rename" : "deleteEmpty"); Copy("newName", "newName");
                }
                else if (Text("groupPath") == "" && (action is "createFromFile" or "delete"
                    || action == "generateBlocks" && Text("targetKind") == "" && Text("targetGroupPath") == "" && Text("generateOption", "None") == "None"))
                {
                    if (action != "generateBlocks" && (Text("targetKind") != "" || Text("targetGroupPath") != "" || Text("generateOption", "None") != "None"))
                        return Reject(new Error("Generation options apply only to generateBlocks.",
                                    new InvalidArgumentDetails("action", Array.Empty<string>())));
                    target = action == "createFromFile" ? "ImportPlcExternalSource" : action == "delete" ? "DeletePlcExternalSource"
                        : "GenerateBlocksFromExternalSource";
                    if (action == "createFromFile")
                    {
                        if (Text("name") != "" && Text("name") != Path.GetFileName(Text("filePath")))
                            return Reject(new Error("Foundation import uses the exact file name. Omit name or pass that file name.",
                                        new InvalidArgumentDetails("name", Array.Empty<string>())));
                        Copy("filePath", "filePath");
                    }
                    else Copy("name", "externalSourceName");
                    Set("groupPath", "");
                    if (action == "delete")
                    {
                        if (!Flag("dryRun", true) && !Flag("confirmDelete"))
                            return Reject(new Error("confirmDelete=true is required.", new InvalidArgumentDetails("confirmDelete", Array.Empty<string>())));
                        if (arguments.TryGetValue("confirm", out var explicitConfirmation) && explicitConfirmation.GetBoolean() != Flag("confirmDelete"))
                            return Reject(new Error("confirm and confirmDelete must agree for deletion.",
                                        new InvalidArgumentDetails("confirm", Array.Empty<string>())));
                        Copy("confirmDelete", "confirm");
                    }
                }
                Copy("softwarePath", "softwarePath");
            }
            if (target != null)
            {
                Copy("dryRun", "dryRun");
                foreach (var field in ConfirmationFields) if (!adapted.ContainsKey(field)) Copy(field, field);
                var targetTool = roster[target];
                var supported = targetTool.ProtocolTool.InputSchema.GetProperty("properties");
                foreach (var field in adapted.Keys.Where(field => !supported.TryGetProperty(field, out _)).ToArray())
                {
                    var value = adapted[field];
                    if (value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.False
                        && !(value.ValueKind == JsonValueKind.String && value.GetString() == ""))
                        return Reject(new Error("The canonical action does not accept this confirmation field.",
                                    new InvalidArgumentDetails(field, Array.Empty<string>())));
                    adapted.Remove(field);
                }
                return await Invoke(targetTool, adapted);
            }
            // Extra confirmation fields apply only to forwarded actions, never silently bypass identity checks on another route.
            if (ConfirmationFields.Any(field => arguments.TryGetValue(field, out var value) && value.ValueKind != JsonValueKind.Null
                && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && value.GetString() != "")))
                return Reject(new Error("Canonical confirmation fields apply only to forwarded actions.",
                            new InvalidArgumentDetails("action", Array.Empty<string>())));
            adapted = arguments.Where(pair => !ConfirmationFields.Contains(pair.Key, StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return await Invoke(original, adapted);

            CallToolResult Reject(Error failure) => FoundationV4Result.Reject(release, tool.Name, Guid.NewGuid().ToString("N"), failure);
            async ValueTask<CallToolResult> Invoke(McpServerTool destination, Dictionary<string, JsonElement> values)
            {
                var saved = request.Params;
                request.Params = new CallToolRequestParams { Name = destination.ProtocolTool.Name, Arguments = values, ProgressToken = saved?.ProgressToken };
                try { return await destination.InvokeAsync(request, cancellationToken); }
                finally { request.Params = saved; }
            }
        }
    }

}
