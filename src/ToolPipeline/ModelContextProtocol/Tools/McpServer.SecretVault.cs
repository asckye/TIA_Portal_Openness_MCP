using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class SecretVaultArguments
    {
        // Credential destinations are reviewed individually; sensitive display keys are not an admission policy.
        internal static readonly IReadOnlyDictionary<string, string[]> Credentials = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["CompilePlcDiagnostics"] = new[] { "password" },
            ["CompilePlcSoftware"] = new[] { "password" },
            ["ConnectOnlinePlc"] = new[] { "password" },
            ["DownloadPlc"] = new[] { "password", "moduleAccessPassword", "blockBindingPassword", "masterSecretPassword" },
            ["GetPlcBlockFingerprints"] = new[] { "password" },
            ["UploadStationFromPlc"] = new[] { "password" },
            ["UploadDeviceParameters"] = new[] { "password" },
            ["OpenProject"] = new[] { "umacPassword" },
            ["RetrieveProjectArchive"] = new[] { "umacPassword" },
            ["ManageCfcChartProtection"] = new[] { "currentPassword" },
            ["ManagePlcBlockProtection"] = new[] { "password" },
            ["ManagePlcBlockWriteProtection"] = new[] { "password", "newPassword" },
            ["ManagePlcCertificate"] = new[] { "password" },
            ["ManagePlcProtection"] = new[] { "password", "newPassword" },
            ["ManageDeviceUsers"] = new[] { "password" },
            ["ManageProjectUserManagement"] = new[] { "password" },
            ["ManagePlcSafety"] = new[] { "password" },
            ["ManageUmcUsers"] = new[] { "serverPassword" },
            ["ManageHardwareUtilities"] = new[] { "password" },
            ["ManageDriveSecurity"] = new[] { "password" },
            ["ManageTeamcenterConnection"] = new[] { "password" },
            ["ManageSinumerikArchive"] = new[] { "password" },
            ["GetPlcWebVars"] = new[] { "password" },
            ["WritePlcWebVars"] = new[] { "password" },
            ["GetPlcWebDiagnostics"] = new[] { "password" },
            ["SetPlcWebOperatingMode"] = new[] { "password" }
        };

        private sealed class ApprovedInvocation(string tool, JsonNode arguments)
        {
            internal readonly string Tool = tool;
            internal readonly JsonNode Arguments = arguments;
            internal bool Active = true;
        }
        private static readonly AsyncLocal<ApprovedInvocation?> Approval = new AsyncLocal<ApprovedInvocation?>();
        internal static IDisposable Approved(string tool, JsonElement arguments, ApprovalOutcome? outcome)
            => new ApprovalScope(tool, arguments, outcome);
        private sealed class ApprovalScope : IDisposable
        {
            private readonly ApprovedInvocation? previous = Approval.Value;
            private readonly ApprovedInvocation? issued;
            internal ApprovalScope(string tool, JsonElement arguments, ApprovalOutcome? outcome)
            {
                bool completed = outcome is { Reason: null, Disabled: false } && outcome.Request.Tool == tool && !ForwardsArguments(tool)
                    && outcome.Request.InvocationArgumentsDigest == PendingApproval.Create(outcome.Request.Host, outcome.Request.ReleaseKey,
                        tool, arguments.GetRawText(), outcome.Request.ProjectIdentity, outcome.Request.TimeoutSeconds).InvocationArgumentsDigest;
                issued = completed ? new ApprovedInvocation(tool, JsonNode.Parse(arguments.GetRawText())!) : null;
                Approval.Value = issued;
            }
            public void Dispose()
            {
                if (issued != null) Volatile.Write(ref issued.Active, false);
                Approval.Value = previous;
            }
        }
        private static bool Authorized(string tool, JsonElement arguments)
            => !McpServer.IsReadOnlyApprovalPreview && !AuditInvocation.IsReadOnlyPreview && Approval.Value is { } approved
                && Volatile.Read(ref approved.Active) && approved.Tool == tool && JsonNode
                    .DeepEquals(approved.Arguments, JsonNode.Parse(arguments.GetRawText()));
        internal static bool NeedsUmcDestination(string tool, JsonElement arguments)
            => tool == "ManageUmcUsers" || tool is "OpenProject" or "RetrieveProjectArchive"
                && arguments.TryGetProperty("umacUserType", out var type) && type.GetString() == "Global";

        internal static Error? Validate(string tool, JsonElement arguments) => Prepare(tool, arguments, false, out _, out _);

        internal static bool IsPreview(JsonElement arguments, JsonElement schema, bool internalPrecheck = false)
        {
            bool defaultPreview = schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("dryRun", out var dryRun)
                && (!dryRun.TryGetProperty("default", out var value) || value.ValueKind != JsonValueKind.False);
            return internalPrecheck || McpServer.IsReadOnlyApprovalPreview || AuditInvocation.IsReadOnlyPreview
                || HostBehavior.IsPreview(JsonNode.Parse(arguments.GetRawText())!.AsObject(), defaultPreview);
        }

        internal static bool RequiresApproval(string tool, JsonElement arguments, JsonElement schema)
            => !ForwardsArguments(tool) && !IsPreview(arguments, schema) && Credentials.TryGetValue(tool, out var allowed)
                && arguments.EnumerateObject().Any(pair => allowed.Contains(pair.Name, StringComparer.Ordinal)
                    && pair.Value.ValueKind == JsonValueKind.String && CallJournalPayload.IsVaultReference(pair.Value.GetString()!));

        internal static ApprovalSettings ApprovalSettingsFor(bool vault, ApprovalSettings settings)
            => vault && !settings.Enabled ? settings.WithRequiredApproval(true) : settings;

        internal static string? ApprovalIdentity(string tool, JsonElement arguments, string? identity, string? umcServer = null)
        {
            if (!Credentials.TryGetValue(tool, out var allowed) || !arguments.EnumerateObject().Any(pair => allowed.Contains(pair.Name)
                && pair.Value.ValueKind == JsonValueKind.String && CallJournalPayload.IsVaultReference(pair.Value.GetString()!))) return identity;
            var target = JsonNode.Parse(identity ?? "{}")!.AsObject();
            var args = JsonNode.Parse(arguments.GetRawText())!.AsObject();
            string? destination = NeedsUmcDestination(tool, arguments) ? umcServer : null;
            if (NeedsUmcDestination(tool, arguments) && string.IsNullOrWhiteSpace(destination))
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException(
                    "The project's configured UMC server could not be identified before vault approval.", "vault-destination-unavailable", null);
            if (destination == null)
            {
                var addresses = new List<string>();
                foreach (string key in new[] { "host", "hostUrl", "server", "serverUrl", "serverAddress", "endpointUrl", "targetIpAddress", "ipAddress" })
                    if (args[key] is JsonValue value && value.TryGetValue<string>(out var address) && !string.IsNullOrWhiteSpace(address))
                    {
                        if (key == "host" && tool is "GetPlcWebVars" or "WritePlcWebVars" or "GetPlcWebDiagnostics" or "SetPlcWebOperatingMode")
                            address = Runtime.RuntimeChannelsLogic.CredentialAuthority(Runtime.RuntimeChannelsLogic.S7WebEndpoint(address));
                        if (key == "hostUrl" && tool == "ManageTeamcenterConnection")
                            address = Runtime.RuntimeChannelsLogic.CredentialAuthority(Runtime.RuntimeChannelsLogic.CheckedCredentialEndpoint(address));
                        addresses.Add(key + "=" + address);
                    }
                if (addresses.Count == 0) addresses.Add("Openness host=" + Environment.MachineName);
                var project = target["identity"]?["projectPath"] ?? target["projectPath"];
                if (project != null && tool is not ("OpenProject" or "RetrieveProjectArchive")) addresses.Add("project=" + project.ToJsonString());
                foreach (string key in new[] { "softwarePath", "devicePath", "itemPath", "path", "archivePath", "instance" })
                    if (args[key] != null) addresses.Add(key + "=" + args[key]!.ToJsonString());
                destination = string.Join("; ", addresses);
            }
            if (tool is "OpenProject" or "RetrieveProjectArchive")
            {
                string field = tool == "OpenProject" ? "path" : "archivePath";
                destination = "target project " + field + "=" + args[field]?.ToJsonString()
                    + (NeedsUmcDestination(tool, arguments) ? "; UMC server=" + umcServer : "; " + destination);
            }
            target["vaultDestination"] = destination;
            return target.ToJsonString();
        }

        internal static string? TargetProject(string tool, JsonElement arguments)
            => tool is "OpenProject" or "RetrieveProjectArchive" && arguments.TryGetProperty(tool == "OpenProject" ? "path" : "archivePath", out var path)
                ? path.GetString() : null;

        internal static async Task<string?> ReadUmcDestinationAsync(Func<string, JsonObject, CancellationToken, Task<JsonNode?>> call,
            CancellationToken token, string? targetProject = null)
        {
            var request = new TiaMcp.Adapters.Contracts.SecurityDeepRequest {
                Operation = "ManageUmcUsers", Kind = "server", Action = "read", ReadVaultDestination = true };
            var reply = await call("project-security.ExecuteSecurityDeep", new JsonObject {
                ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = true }, token).ConfigureAwait(false);
            if (targetProject != null)
            {
                string? observed = (string?)reply?["Meta"]?["vaultDestinationProject"];
                if (string.IsNullOrEmpty(observed) || !string.Equals(Path.GetFullPath(observed), Path.GetFullPath(targetProject),
                    StringComparison.OrdinalIgnoreCase)) return null;
            }
            return (string?)reply?["Meta"]?["vaultDestination"];
        }

        internal static Error? Prepare(string tool, JsonElement arguments, bool execute, out JsonObject resolved, out bool changed)
        {
            resolved = JsonNode.Parse(arguments.GetRawText())!.AsObject();
            bool found = false;
            Error? error = null;
            SecretVault? vault = null;
            JsonNode? Visit(JsonNode? node, string pointer, string owner, bool argumentRoot = false, bool credential = false)
            {
                if (error != null) return node;
                if (node is JsonObject obj)
                {
                    if (argumentRoot)
                    {
                        string? endpointKey = owner is "GetPlcWebVars" or "WritePlcWebVars" or "GetPlcWebDiagnostics" or "SetPlcWebOperatingMode"
                            ? "host" : owner == "ManageTeamcenterConnection" && obj["password"] is JsonValue password
                                && password.TryGetValue<string>(out var passwordReference) && passwordReference.StartsWith("vault:", StringComparison.Ordinal)
                                    ? "hostUrl" : null;
                        if (endpointKey != null && obj[endpointKey] is JsonValue endpoint && endpoint.TryGetValue<string>(out var address)
                            && (endpointKey == "host" || !string.IsNullOrEmpty(address)))
                        {
                            try
                            {
                                _ = endpointKey == "host" ? Runtime.RuntimeChannelsLogic.S7WebEndpoint(address)
                                    : Runtime.RuntimeChannelsLogic.CheckedCredentialEndpoint(address);
                            }
                            catch (ArgumentException) /* swallow(privacy): refuse malformed destinations without echoing userinfo. */
                            {
                                error = new Error("Credential destination is invalid or ambiguous.",
                                    new InvalidArgumentDetails(pointer + "/" + endpointKey, Array.Empty<string>()));
                                return node;
                            }
                        }
                    }
                    foreach (var pair in obj.ToArray())
                    {
                        string path = pointer + "/" + InvalidArgumentDetails.Segment(pair.Key);
                        if (argumentRoot && owner is "CallTool" or "PreviewToolCall" && pair.Key == "arguments" && pair.Value is JsonObject
                            && obj["name"] is JsonValue target && target.TryGetValue<string>(out var name))
                            obj[pair.Key] = Visit(pair.Value.DeepClone(), path, name, true);
                        else if (argumentRoot && owner is "PreviewToolBatch" or "RunReadOnlyToolBatch" && pair.Key == "operations"
                            && pair.Value is JsonArray operations)
                        {
                            for (int i = 0; i < operations.Count; i++)
                            {
                                if (operations[i] is not JsonObject operation) continue;
                                foreach (var field in operation.ToArray())
                                {
                                    string childPath = path + "/" + i.ToString(CultureInfo.InvariantCulture) + "/"
                                        + InvalidArgumentDetails.Segment(field.Key);
                                    string childName = operation["name"] is JsonValue operationName
                                        && operationName.TryGetValue<string>(out var operationText) ? operationText : "";
                                    bool child = field.Key == "arguments" && field.Value is JsonObject && childName.Length > 0;
                                    operation[field.Key] = Visit(field.Value?.DeepClone(), childPath, childName, child);
                                }
                            }
                        }
                        else
                            obj[pair.Key] = Visit(pair.Value?.DeepClone(), path, owner, credential: argumentRoot
                                && Credentials.TryGetValue(owner, out var allowed) && allowed.Contains(pair.Key, StringComparer.Ordinal));
                    }
                }
                else if (node is JsonArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                        array[i] = Visit(array[i]?.DeepClone(), pointer + "/" + i.ToString(CultureInfo.InvariantCulture), owner);
                }
                else if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.StartsWith("vault:", StringComparison.Ordinal))
                {
                    if (!credential)
                    {
                        error = new Error("Vault references are allowed only in declared credential parameters; refused " + pointer + ".",
                            new InvalidArgumentDetails(pointer, Array.Empty<string>(), expected: "declared credential parameter"));
                        return node;
                    }
                    if (execute && !Authorized(tool, arguments))
                    {
                        error = new Error("A completed Workbench vault approval is required before credential resolution.",
                            new PreconditionFailedDetails("vault-approval-required", pointer));
                        return node;
                    }
                    string? condition = null;
                    bool valid = CallJournalPayload.IsVaultReference(text);
                    try
                    {
                        if (valid)
                        {
                            vault ??= new SecretVault();
                            if (!execute && vault.ContainsName(text.Substring(6))) return node;
                            if (execute && vault.TryResolve(text.Substring(6), out var value))
                            {
                                found = true;
                                return JsonValue.Create(value);
                            }
                        }
                    }
                    catch (Exception failure) /* swallow(privacy): classify vault access without values. */
                        when (failure is IOException || failure is UnauthorizedAccessException || failure is System.Security.SecurityException)
                    { condition = "secret-vault-unreadable"; }
                    error = new Error((valid ? "Cannot resolve " + text : "Invalid vault reference") + " at " + pointer
                        + (condition == null ? ". Configure the entry in the Workbench under the same Windows user."
                            : ": " + condition + ". Repair the vault under the same Windows user."),
                        new InvalidArgumentDetails(pointer, Array.Empty<string>(), expected: condition ?? "existing vault:<name> reference"));
                }
                return node;
            }
            Visit(resolved, "", tool, true);
            changed = found;
            return error;
        }

        // Both the original arguments and the resolved copy remain invocation-local.
        internal static IDisposable Begin(JsonElement original, JsonObject resolved)
        {
            var effective = JsonSerializer.SerializeToElement(resolved);
            NativeFailureDiagnostics.IncludeArguments(effective, resolved: true);
            return new InvocationScope(original, effective);
        }

        private sealed class InvocationScope : IDisposable
        {
            private readonly IDisposable diagnostics;
            private readonly IDisposable references;
            private readonly IDisposable logicReferences;
            internal InvocationScope(JsonElement original, JsonElement resolved)
            {
                diagnostics = NativeFailureDiagnostics.Begin(NativeFailureDiagnostics.RequestId ?? InvocationJournal.CorrelationId, original);
                NativeFailureDiagnostics.IncludeArguments(resolved, resolved: true);
                var positions = original.EnumerateObject().Where(pair => pair.Value.ValueKind == JsonValueKind.String
                    && CallJournalPayload.IsVaultReference(pair.Value.GetString()!)).Select(pair => "/" + pair.Name).ToArray();
                references = CallJournalPayload.UseVaultArguments(JsonNode.Parse(original.GetRawText())!,
                    JsonNode.Parse(resolved.GetRawText())!, positions);
                logicReferences = NativeFailureDiagnostics.UseVaultArguments(original, resolved, positions);
            }
            public void Dispose()
            {
                logicReferences.Dispose();
                references.Dispose();
                diagnostics.Dispose();
            }
        }

        internal static CallToolResult Scrub(CallToolResult result)
        {
            var node = JsonSerializer.SerializeToNode(result, McpJsonUtilities.DefaultOptions)!;
            void Visit(JsonNode? current)
            {
                if (current is JsonObject obj)
                    foreach (var pair in obj.ToArray())
                        if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                            obj[pair.Key] = NativeFailureDiagnostics.RedactSecrets(text);
                        else Visit(pair.Value);
                else if (current is JsonArray array)
                    for (int i = 0; i < array.Count; i++)
                        if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                            array[i] = NativeFailureDiagnostics.RedactSecrets(text);
                        else Visit(array[i]);
            }
            Visit(node);
            return node.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)!;
        }

        internal static bool ForwardsArguments(string name) => ToolTaxonomy.DispatchesTargets(name) || name == "PreviewToolCall";
    }

    internal sealed class SecretVaultTool(McpServerTool inner) : McpServerTool
    {
        public override Tool ProtocolTool => inner.ProtocolTool;
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            if (SecretVaultArguments.ForwardsArguments(ProtocolTool.Name))
                return await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            var arguments = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
            var error = SecretVaultArguments.Prepare(ProtocolTool.Name, arguments,
                !SecretVaultArguments.IsPreview(arguments, ProtocolTool.InputSchema), out var resolved, out bool changed);
            if (error != null) return McpServer.V4Reject(ProtocolTool.Name, error);
            if (!changed) return await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            using var secrets = SecretVaultArguments.Begin(arguments, resolved);
            var original = request.Params;
            request.Params = new CallToolRequestParams { Name = ProtocolTool.Name, Meta = original?.Meta, ProgressToken = original?.ProgressToken,
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(resolved.ToJsonString()) };
            try { return SecretVaultArguments.Scrub(await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false)); }
            catch (Exception failure)
            { return SecretVaultArguments.Scrub(McpServer.TargetFailure(ProtocolTool.Name, failure, true)); }
            finally { request.Params = original; }
        }
    }
}
