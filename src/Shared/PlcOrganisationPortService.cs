using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed partial class PlcOrganisationPortService
    {
        private readonly Func<string, JsonObject, JsonNode?> call;
        private readonly Func<bool> hasProject;
        private readonly Func<string> projectIdentity;
        internal PlcOrganisationPortService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> projectIdentity)
        { this.call = call; this.hasProject = hasProject; this.projectIdentity = projectIdentity; }
        public PlcOrganisationPortService()
            : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.HasProject, HardwareAddressWorkerBridge.ProjectIdentity) { }

        internal string BoundProject => projectIdentity();
        internal bool HasProject => hasProject();

        private JsonObject Invoke(PlcSoftwareRequest request, Action? beforeDispatch = null)
        {
            string? unsupported = PlcSoftwareCapabilities
                .Unsupported(HardwareContract.ReleaseKey, request.Operation, request.Action, request.Family, request.UnitName, request.UnitKind, request
                    .TargetKind, request.CopyMode, request.GenerateOption);
            unsupported ??= PlcB4Capabilities.Unsupported(HardwareContract.ReleaseKey, request);
            if (unsupported != null) throw new NotSupportedException(unsupported);
            var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
            if (!request.DryRun) { arguments["confirm"] = true; arguments["expectedProjectFile"] = projectIdentity(); }
            beforeDispatch?.Invoke();
            var reply = call(PortedFamilies.ForTool(request.Operation).OperationPrefix + ".ExecutePlcOrganisation", arguments) as JsonObject
                ?? throw new InvalidOperationException("Missing PLC organisation worker reply.");
            if (reply["Failure"] is JsonObject failure)
            {
                string code = (string?)failure["Code"] ?? "";
                string message = (string?)failure["Message"] ?? "PLC organisation failed.";
                if (Enum.TryParse<PortalErrorCode>(code, out var nativeCode))
                {
                    var error = new PortalException(nativeCode, message,
                        failure["Candidates"] == null ? null : JsonSerializer.Deserialize<string[]>(failure["Candidates"]!.ToJsonString()));
                    if (failure["Evidence"] is JsonObject evidence && evidence.Count > 0) error.Data["nativeResultEvidence"] = evidence.DeepClone();
                    error.Data["foundationKnownNoMutation"] = (bool?)reply["MayHaveChanged"] == false;
                    throw error;
                }
                // Keep the typed precondition and its recovery evidence across the worker boundary.
                Exception mapped = code == nameof(AdapterPreconditionException) && (bool?)reply["MayHaveChanged"] != true
                    ? new AdapterPreconditionException(message, null, false)
                    : code == nameof(ArgumentException) ? new ArgumentException(message)
                    : code == nameof(NotSupportedException) ? new NotSupportedException(message)
                    : code == nameof(System.IO.IOException) ? new System.IO.IOException(message)
                    : new InvalidOperationException(message);
                if (failure["Evidence"] is JsonObject details && details.Count > 0) mapped.Data["nativeResultEvidence"] = details.DeepClone();
                mapped.Data["foundationKnownNoMutation"] = (bool?)reply["MayHaveChanged"] == false;
                throw mapped;
            }
            return reply;
        }

        private static ResponseMessage Step(JsonObject reply) => new ResponseMessage {
            Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone()
        };
        private ResponseMessage OrganisationStep(PlcSoftwareRequest request)
            => !hasProject() ? NoProject(request.Operation) : Step(Invoke(request));
        private static ResponseMessage NoProject(string tool) => new ResponseMessage {
            Message = "Project is null", Meta = ResponseMeta
                .Step(tool, ("error", JsonValue.Create("Project is null")), ("status", JsonValue.Create("InvalidState")),
                    ("operationSuccess", JsonValue.Create(false)))
        };
        private JsonObject Delete(string tool, string softwarePath, string path, bool dryRun, bool crossReferences)
        {
            string field = tool == "DeletePlcBlock" ? "blockPath" : tool == "DeletePlcType" ? "typePath" : "tagTableName";
            if (string.IsNullOrWhiteSpace(path)) throw new PortalException(PortalErrorCode.InvalidParams, tool + ": " + field + " is empty");
            var leaf = tool == "DeletePlcTagTable" ? path.Replace('\\', '/').Trim('/') : path;
            leaf = leaf.Contains("/") ? leaf.Substring(leaf.LastIndexOf('/') + 1) : leaf;
            if (leaf.IndexOfAny(new[] { '.', '*', '+', '?', '^', '$', '[', ']', '(', ')', '{', '}', '|', '\\' }) >= 0)
                throw new PortalException(PortalErrorCode.InvalidParams, tool == "DeletePlcTagTable"
                    ? "DeletePlcTagTable requires one exact tag table name or group-qualified path; regular expressions and wildcards are not allowed"
                    : tool + " requires one exact " + (tool == "DeletePlcBlock" ? "block" : "type")
                        + " path; regular expressions and wildcards are not allowed");
            if (!hasProject()) throw new PortalException(PortalErrorCode.InvalidState, tool + ": no project is open. ConnectPortal / AttachOpenProject first.");
            return (JsonObject)Invoke(new PlcSoftwareRequest { Operation = tool, SoftwarePath = softwarePath, Path = path,
                DryRun = dryRun, CrossReferences = crossReferences })["Data"]!.DeepClone();
        }
        public JsonObject DeletePlcBlock(string softwarePath, string blockPath, bool dryRun, bool crossReferences = false)
            => Delete("DeletePlcBlock", softwarePath, blockPath, dryRun, crossReferences);
        public JsonObject DeletePlcType(string softwarePath, string typePath, bool dryRun, bool crossReferences = false)
            => Delete("DeletePlcType", softwarePath, typePath, dryRun, crossReferences);
        public JsonObject DeletePlcTagTable(string softwarePath, string tagTableName, bool dryRun, bool crossReferences = false)
            => Delete("DeletePlcTagTable", softwarePath, tagTableName, dryRun, crossReferences);

        internal static void ValidateGroup(string path, bool types)
        {
            try { PlcOrganisationPaths.Parse(path, types); }
            catch (PlcSoftwareException error) { throw new AdapterPreconditionException(error.Message, "groupPath", isArgument: true, innerException: error); }
        }
        public JsonObject CreatePlcTypeGroup(string softwarePath, string groupPath, bool dryRun = true)
            => ManagePlcUserGroup(softwarePath, "types", groupPath, "create", dryRun: dryRun).Meta!;
        public JsonObject DeleteEmptyPlcBlockGroup(string softwarePath, string groupPath, bool dryRun = true)
            => ManagePlcUserGroup(softwarePath, "blocks", groupPath, "deleteEmpty", dryRun: dryRun).Meta!;
        public object? EnsurePlcBlockGroup(string softwarePath, string groupPath, out List<string> created)
        {
            created = new List<string>();
            if (!hasProject()) return null;
            var result = ManagePlcUserGroup(softwarePath, "blocks", groupPath, "create", dryRun: false);
            if (result.Meta?["result"]?["createdPaths"] is JsonArray paths)
                created.AddRange(paths.Select(path => path!.GetValue<string>()).Select(path => path.Substring(path.LastIndexOf('/') + 1)));
            return result;
        }
        public string MoveBlockToGroup(string softwarePath, string blockName, string targetGroupPath, bool autoCreateGroup = true)
            => MoveBlockToGroup(softwarePath, blockName, targetGroupPath, autoCreateGroup, true, false, "", "").Message ?? "";
        public ResponseMessage MoveBlockToGroup(string softwarePath, string blockName, string targetGroupPath, bool autoCreateGroup,
            bool dryRun, bool confirm, string expectedPlanHash, string expectedProjectFile)
        {
            bool dispatched = false;
            string recovery = "";
            try
            {
                if (!hasProject()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
                if (!dryRun && (!confirm || string.IsNullOrWhiteSpace(expectedPlanHash) || expectedProjectFile != projectIdentity()))
                    throw new AdapterPreconditionException("Move requires confirmation, the reviewed plan hash and exact project file.",
                        "expectedPlanHash", false);
                recovery = System.IO.Path.Combine(TiaOpenness.Shared.DataLocations.Current.RecoveryAttemptedPath,
                    "plc-move-" + Guid.NewGuid().ToString("N"), "original.xml");
                var result = Step(Invoke(new PlcSoftwareRequest { Operation = "MovePlcBlockToGroup", SoftwarePath = softwarePath,
                    Name = blockName, GroupPath = targetGroupPath, AutoCreateGroup = autoCreateGroup, FilePath = recovery,
                    DryRun = dryRun, ExpectedPlanHash = expectedPlanHash }, () => dispatched = true));
                result.Meta!["projectFile"] = projectIdentity();
                return result;
            }
            catch (Exception error)
            {
                var evidence = error.Data["nativeResultEvidence"] as JsonObject ?? new JsonObject();
                error.Data["nativeResultEvidence"] = evidence;
                bool? deleteStarted = (bool?)evidence["deleteStarted"];
                bool noDelete = deleteStarted == false || deleteStarted != true && (dryRun || !dispatched
                    || error.Data["foundationKnownNoMutation"] is true || error.Data["foundationRequestSent"] is false);
                if (noDelete)
                {
                    evidence["deleteStarted"] = false; evidence["nativeMutationIssued"] = false;
                    evidence.Remove("recoveryExportPath");
                }
                else evidence["recoveryExportPath"] ??= recovery;
                evidence["recoveryExportStatus"] ??= (bool?)evidence["recoveryExportVerified"] == true ? "verified"
                    : dryRun && !dispatched ? "not-attempted" : "unconfirmed";
                throw;
            }
        }
        public ResponseMessage ManagePlcUserGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
        {

            var unavailable = PlcSoftwareCapabilities.Unsupported(HardwareContract.ReleaseKey, "ManagePlcUserGroup", action, family);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            ValidateGroup(groupPath, family == "types");
            if (!hasProject()) return NoProject("ManagePlcUserGroup");
            return Step(Invoke(new PlcSoftwareRequest { Operation = "ManagePlcUserGroup", SoftwarePath = softwarePath,
                Family = family, GroupPath = groupPath, Action = action, NewName = newName, DryRun = dryRun }));
        }
        public ResponseMessage ManagePlcBlockProtection(string softwarePath, string blockPath, string action, string password = "",
                bool confirmProtectionChange = false, bool dryRun = true)
        {
            var unavailable = PlcSoftwareCapabilities.Unsupported(HardwareContract.ReleaseKey, "ManagePlcBlockProtection", action);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            if (!hasProject()) return NoProject("ManagePlcBlockProtection");
            return Step(Invoke(new PlcSoftwareRequest { Operation = "ManagePlcBlockProtection", SoftwarePath = softwarePath,
                Path = blockPath, Action = action, Password = password, Confirm = confirmProtectionChange, DryRun = dryRun }));
        }
        public ResponseMessage ReadPlcSystemGroups(string softwarePath, string unitName = "", string unitKind = "unit", bool includeBlocks = true,
                int maxDepth = 4)
        {
            var unavailable = PlcSoftwareCapabilities.Unsupported(HardwareContract.ReleaseKey, "ListPlcSystemGroups", unitName: unitName, unitKind: unitKind);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            if (!hasProject()) return NoProject("ListPlcSystemGroups");
            return Step(Invoke(new PlcSoftwareRequest { Operation = "ListPlcSystemGroups", SoftwarePath = softwarePath,
                UnitName = unitName, UnitKind = unitKind, IncludeBlocks = includeBlocks, MaxDepth = maxDepth }));
        }
    }
}
