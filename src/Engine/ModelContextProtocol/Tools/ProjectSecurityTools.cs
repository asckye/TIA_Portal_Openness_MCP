using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    // Security tools keep their native service boundary and expose its evidence in
    // the V4 envelope. Typed input admission belongs to the shared catalog.
    internal static class SecurityToolContract
    {
        internal static string Path(string[] value) => V4Json.Serialize(value ?? Array.Empty<string>());
        internal static string Map(AttributeMap<Scalar> value)
            => V4Json.Serialize(value ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>()));

        internal static CallToolResult Reject(string tool, string parameter, bool current)
            => Result(tool, null, McpServer.InvalidInput(parameter), Outcome.RejectedBeforeOperation,
                Execution.NotStarted, Completeness.None, current);

        internal static string TraceProperties(AttributeMap<Scalar> properties, string[]? signals)
        {
            var combined = JsonNode.Parse(Map(properties))!.AsObject();
            if (signals != null) combined["signals"] = JsonNode.Parse(V4Json.Serialize(signals));
            return combined.ToJsonString();
        }

        internal static CallToolResult Invoke(string tool, bool readOnly, bool current, Func<ResponseMessage> operation, params string[] secrets)
        {
            try { return MapResult(tool, operation(), readOnly, current, secrets); }
            catch (Exception) /* swallow(privacy): escaped native errors have no confirmed write outcome and must not expose credentials */
            {
                return Result(tool, null, readOnly ? NativeFailure() : UnknownFailure(),
                    readOnly ? Outcome.ReadFailed : Outcome.Unknown, readOnly ? Execution.ReadOnly : Execution.Unknown,
                    Completeness.Unknown, current);
            }
        }

        private static bool? Flag(JsonObject data, string key)
            => data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;

        internal static CallToolResult MapResult(string tool, ResponseMessage response, bool readOnly, bool current, params string[] secrets)
        {
            var data = response.Meta == null ? new JsonObject() : (JsonObject)response.Meta.DeepClone();
            bool? success = data.ContainsKey("operationSuccess") ? Flag(data, "operationSuccess") : Flag(data, "success");
            bool issued = Flag(data, "mayHaveChanged") == true || Flag(data, "mayHaveWrittenFiles") == true;
            bool reset = Flag(data, "requiresExplicitRebind") == true || Flag(data, "requiresSessionReset") == true;
            bool knownFailure = issued && Flag(data, "verifiedAbsent") == false;
            bool knownFile = Flag(data, "mayHaveChanged") == false && Flag(data, "mayHaveWrittenFiles") == true && data["file"] is JsonObject;
            string status = data["status"]?.ToString() ?? "";
            Outcome outcome; Execution execution; Error? error = null;
            if (issued && (reset || success != true && !knownFailure && !knownFile))
            { outcome = Outcome.Unknown; execution = Execution.Unknown; error = UnknownFailure(); }
            else if (knownFailure)
            { outcome = Outcome.Failed; execution = Execution.Completed; error = NativeFailure(); }
            else if (success != true && knownFile)
            { outcome = Outcome.Partial; execution = Execution.Partial; error = new Error("A verified output file remains after the operation failed.", new PartialFailureDetails(1, 1, 0)); }
            else if (status == "HmiReadSessionBlocked")
            { outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; error = new Error("The session requires an explicit reset.", new SessionResetRequiredDetails("security-session")); }
            else if (success != true && status == "InvalidState")
            {
                outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted;
                error = data.ContainsKey("mayHaveChanged")
                    ? new Error("Security operation preconditions were not satisfied.", new PreconditionFailedDetails("security-state", null))
                    : new Error("No project is bound.", new ProjectNotBoundDetails());
            }
            else if (success != true && (status == "NotSupportedOnVersion" || status == "NotFound" || status == "AccessDenied"))
            {
                outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted;
                error = status == "NotSupportedOnVersion" ? new Error("The requested security capability is unavailable.", new UnsupportedCapabilityDetails(McpServer.ReleaseKey, tool, null))
                    : status == "NotFound" ? new Error("The requested security target was not found.", new NotFoundDetails(null))
                    : new Error("Access to the security operation was denied.", new AccessDeniedDetails(tool, null));
            }
            else if (success == true)
            { outcome = Outcome.Succeeded; execution = readOnly ? Execution.ReadOnly : Execution.Completed; }
            else if (readOnly)
            { outcome = Outcome.ReadFailed; execution = Execution.ReadOnly; error = NativeFailure(); }
            else if (Flag(data, "mayHaveChanged") == false && Flag(data, "mayHaveWrittenFiles") != true)
            { outcome = Outcome.RejectedBeforeOperation; execution = Execution.NotStarted; error = new Error("Security operation preconditions were not satisfied.", new PreconditionFailedDetails("security-before-write", null)); }
            else
            { outcome = Outcome.Unknown; execution = Execution.Unknown; error = UnknownFailure(); }

            bool incomplete = Incomplete(data);
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : incomplete ? Completeness.Partial : Completeness.Complete;
            Paging? paging = null;
            if (data["offset"] is JsonValue offset && offset.TryGetValue<int>(out var start)
                && data["limit"] is JsonValue limit && limit.TryGetValue<int>(out var size)
                && data["expectedCount"] is JsonValue count && count.TryGetValue<int>(out var total) && start >= 0 && size > 0)
                paging = McpServer.OffsetPage(start, size, total);
            foreach (string key in new[] { "timestamp", "tool", "success", "offset", "limit", "nextOffset" }) data.Remove(key);
            string action = data["action"]?.ToString() ?? "";
            if (outcome == Outcome.Succeeded && readOnly && Flag(data, "dryRun") == true
                && !new[] { "read", "list", "template", "checkValidity", "checkTraceValidity", "checkConsistency", "listServerProjects", "readLockState", "listLocalSessions" }.Contains(action))
                data["plan"] = new JsonObject { ["action"] = action, ["executed"] = false, ["summary"] = "Preview completed; no requested change was executed." };
            // Comparison tools already carry a structured summary; retain it.
            if (outcome == Outcome.Succeeded && !data.ContainsKey("summary")) data["summary"] = response.Message;
            Sanitize(data, secrets);
            return Result(tool, data, error, outcome, execution, completeness, current, paging, reset);
        }

        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Incomplete);
            if (!(node is JsonObject obj)) return false;
            return Flag(obj, "dataComplete") == false || Flag(obj, "truncated") == true || Flag(obj, "treeTruncated") == true
                || obj.Any(p => p.Key.EndsWith("Error", StringComparison.Ordinal) && p.Value != null
                    || p.Key.EndsWith("Failures", StringComparison.OrdinalIgnoreCase) && p.Value is JsonObject failures && failures.Count > 0
                    || p.Key == "programSignaturesNote" || Incomplete(p.Value));
        }

        private static void Sanitize(JsonNode? node, string[] secrets)
        {
            if (node is JsonArray array)
            {
                for (int i = 0; i < array.Count; i++)
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = Redact(text, secrets);
                    else Sanitize(array[i], secrets);
                return;
            }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                string key = pair.Key.ToLowerInvariant();
                if (key == "password" || key == "serverpassword" || key == "newpassword" || key == "privatekey"
                    || key == "rawdata" || key == "pem" || key == "certificatedata" || key == "messagedata"
                    || key == "detailmessagedata" || key == "lastfailure") obj.Remove(pair.Key);
                else if (key == "error" || pair.Key.EndsWith("Error", StringComparison.Ordinal)) obj[pair.Key] = "Native observation failed.";
                else if (pair.Key.EndsWith("Failures", StringComparison.OrdinalIgnoreCase) && pair.Value is JsonObject failures)
                    foreach (var failure in failures.ToArray()) failures[failure.Key] = "Native observation failed.";
                else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)) obj[pair.Key] = Redact(text, secrets);
                else Sanitize(pair.Value, secrets);
            }
        }

        private static string Redact(string text, string[] secrets)
        {
            foreach (string secret in secrets.Where(s => !string.IsNullOrEmpty(s))) text = text.Replace(secret, "[redacted]");
            return text;
        }

        private static Error NativeFailure() => new Error("The security operation did not establish a successful result.",
            new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
        private static Error UnknownFailure() => new Error("The security operation outcome is unknown. Inspect the retained evidence and reset the session before further writes.",
            new OutcomeUnknownDetails("security-operation", new Dictionary<string, JsonElement>()));

        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Execution execution,
            Completeness completeness, bool current, Paging? paging = null, bool reset = false)
        {
            var warnings = new List<Warning>();
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The security observation covers only the reported scope and available fields.", new Dictionary<string, JsonElement>()));
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution,
                reset || outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired,
                current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, paging, warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }

    [McpServerToolType]
    internal sealed class ProjectSecurityTools
    {
        private readonly ProjectSecurityService _service;

        public ProjectSecurityTools(ProjectSecurityService service) => _service = service;

        [McpServerTool(Name="GetProjectUserManagement"), Description("[L2][Security][READ] Paginated UMAC listing of the bound project: category users/anonymousUser/systemRoles/customRoles/engineeringRights/customDeviceRights/umcUsers/umcUserGroups/passwordPolicy/deviceRights/roleDeviceRights. Optional exact name filter; deviceRights/roleDeviceRights need devicePath (+itemPath) arrays of exact names. Requires the native UmacConfigurator service (protected project); passwords never readable. Nothing modified.")]
        public CallToolResult GetProjectUserManagement(string category="users", string name="", string[] devicePath=null!, string[] itemPath=null!, int offset=0, int limit=100)
            => SecurityToolContract.Invoke("GetProjectUserManagement", true, false,
                () => _service.ReadProjectUserManagement(category,name,SecurityToolContract.Path(devicePath),SecurityToolContract.Path(itemPath),offset,limit));
        [McpServerTool(Name="ManageProjectUserManagement"), Description("[L2][Security][WRITE] UMAC mutation by exact names: createUser/deleteUser/setUserPassword/activateUser/deactivateUser/assignRole/unassignRole (name=user, roleName), createRole/deleteRole/assignEngineeringRight/unassignEngineeringRight/assignDeviceRight/unassignDeviceRight (name=custom role, rightName, devicePath), createDeviceRight/deleteDeviceRight (name, group, comment), activateAnonymousUser/deactivateAnonymousUser (no name; UmacConfigurator.ActivateAnonymousUser/DeactivateAnonymousUser, single anonymous user per protected project - roles are assigned with assignRole name=Anonymous). Default preview; real execution needs dryRun=false AND confirmChange=true. Password goes to the API as SecureString and is never logged. Readback verified; no save/compile/download; system roles and project protection itself are never changed.")]
        public CallToolResult ManageProjectUserManagement(
            [Description("createUser | deleteUser | setUserPassword | activateUser | deactivateUser | assignRole | unassignRole | createRole | deleteRole | assignEngineeringRight | unassignEngineeringRight | assignDeviceRight | unassignDeviceRight | createDeviceRight | deleteDeviceRight | activateAnonymousUser | deactivateAnonymousUser. ")] string action,
            string name,
            string password="",
            [Description("roleName: exact role name.")] string roleName="",
            [Description("rightName: exact name of the engineering / device right.")] string rightName="",
            [Description("comment: comment text.")] string comment="",
            [Description("group: group name.")] string group="",
            string[] devicePath=null!,
            string[] itemPath=null!,
            bool confirmChange=false,
            bool dryRun=true)
            => SecurityToolContract.Invoke("ManageProjectUserManagement", dryRun, false,
                () => _service.ManageProjectUserManagement(action,name,password,roleName,rightName,comment,group,SecurityToolContract.Path(devicePath),SecurityToolContract.Path(itemPath),confirmChange,dryRun), password);
        [McpServerTool(Name="GetProjectProtection"), Description("[L2][Security][READ] Read project protection indicators of the bound project: UMAC service availability (the only native indicator, no IsProtected scalar exists), UMAC object counts, anonymous user, password policy, UMC server configurator and advanced protection provider scalars. Never enables/disables protection.")]
        public CallToolResult GetProjectProtection()
            => SecurityToolContract.Invoke("GetProjectProtection", true, false,
                () => _service.ReadProjectProtection());
        [McpServerTool(Name="ManageMultiuserSession"), Description("[L2][Project][WRITE] Multiuser/project-server access by exact alias: read (servers, open local sessions, bound session up-to-date flag and markings), listServerProjects, readLockState, listLocalSessions (serverName + projectName), connectServer (serverName, protocol Https/Http, host, port), disconnectServer, commit (CloseAndCommit with commitComment; closes the bound session). Mutations default to preview and need dryRun=false AND confirmChange=true. Never opens/creates local sessions or saves implicitly.")]
        public CallToolResult ManageMultiuserSession(
            [Description("read | listServerProjects | readLockState | listLocalSessions | connectServer | disconnectServer | commit. ")] string action="read",
            [Description("serverName: multiuser server name.")] string serverName="",
            [Description("projectName: exact project name.")] string projectName="",
            [Description("protocol: Https or Http.")] string protocol="Https",
            [Description("host: server host name or IP address.")] string host="",
            [Description("port: TCP port (0 = default).")] int port=0,
            [Description("commitComment: comment text of the commit.")] string commitComment="",
            int offset=0,
            int limit=100,
            bool confirmChange=false,
            bool dryRun=true)
            => SecurityToolContract.Invoke("ManageMultiuserSession", dryRun || action == "read" || action == "listServerProjects" || action == "readLockState" || action == "listLocalSessions", action == "commit",
                () => _service.ManageMultiuserSession(action,serverName,projectName,protocol,host,port,commitComment,offset,limit,confirmChange,dryRun));
        [McpServerTool(Name="CompareLibraries"), Description("[L2][Library][READ] Native CompareToLibrary between two libraries: empty name = project library, otherwise exact open global library name. Result tree flattened (path/depth/state), summary counts all states, records exclude identical elements unless includeIdentical=true; paginated. Read-only, no library opened or modified.")]
        public CallToolResult CompareLibraries(
            [Description("leftLibraryName: global library on the left side of the comparison ('' = the project library).")] string leftLibraryName="",
            [Description("rightLibraryName: global library on the right side of the comparison ('' = the project library).")] string rightLibraryName="",
            bool includeIdentical=false,
            int maxDepth=8,
            int offset=0,
            int limit=100)
            => SecurityToolContract.Invoke("CompareLibraries", true, false,
                () => _service.CompareLibraries(leftLibraryName,rightLibraryName,includeIdentical,maxDepth,offset,limit));
        [McpServerTool(Name="CompareProjects"), Description("[L2][Project][READ] Native OFFLINE comparison: kind=software (exact softwarePath vs targetSoftwarePath in the bound project, or vs the CPU at targetDevicePath/targetItemPath in another open project targetProjectName), softwareToLibrary (softwarePath vs project library or exact targetLibraryName), hardware (devicePath/itemPath vs target device, same or other open project). Flattened result tree, summary of all states, non-identical records paginated. No online access; nothing modified.")]
        public CallToolResult CompareProjects(
            string kind,
            string softwarePath="",
            string[] devicePath=null!,
            string[] itemPath=null!,
            [Description("targetProjectName: exact name of the project to compare with.")] string targetProjectName="",
            string targetSoftwarePath="",
            [Description("targetDevicePath: JSON array naming the target station.")] string[] targetDevicePath=null!,
            [Description("targetItemPath: array of device-item names on the target.")] string[] targetItemPath=null!,
            string targetLibraryName="",
            bool includeIdentical=false,
            int maxDepth=8,
            int offset=0,
            int limit=100)
            => SecurityToolContract.Invoke("CompareProjects", true, false,
                () => _service.CompareProjects(kind,softwarePath,SecurityToolContract.Path(devicePath),SecurityToolContract.Path(itemPath),targetProjectName,targetSoftwarePath,SecurityToolContract.Path(targetDevicePath),SecurityToolContract.Path(targetItemPath),targetLibraryName,includeIdentical,maxDepth,offset,limit));
        [McpServerTool(Name="GetProjectSettings"), Description("[L2][Project][READ] Read TIA Portal settings folders (exact slash-separated folderPath; empty lists root folders) with scalar setting values, paginated; optional customIdentityKey reads the project-root CustomIdentityProvider value. Read-only; nothing modified.")]
        public CallToolResult GetProjectSettings(
            string folderPath="",
            [Description("customIdentityKey: key of the custom identity to read ('' = all).")] string customIdentityKey="",
            int offset=0,
            int limit=100)
            => SecurityToolContract.Invoke("GetProjectSettings", true, false,
                () => _service.ReadProjectSettings(folderPath,customIdentityKey,offset,limit));
    }
}
