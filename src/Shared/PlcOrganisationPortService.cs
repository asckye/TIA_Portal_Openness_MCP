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
        public PlcOrganisationPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.HasProject, HardwareAddressWorkerBridge.ProjectIdentity) { }

        private JsonObject Invoke(PlcSoftwareRequest request)
        {
            string? unsupported = PlcSoftwareCapabilities.Unsupported(HardwareContract.ReleaseKey, request.Operation, request.Action, request.Family, request.UnitName, request.UnitKind, request.TargetKind, request.CopyMode, request.GenerateOption);
            if (unsupported != null) throw new NotSupportedException(unsupported);
            var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
            if (!request.DryRun) { arguments["confirm"] = true; arguments["expectedProjectFile"] = projectIdentity(); }
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
                    throw error;
                }
                if (code == nameof(ArgumentException)) throw new ArgumentException(message);
                if (code == nameof(NotSupportedException)) throw new NotSupportedException(message);
                throw new InvalidOperationException(message);
            }
            return reply;
        }
        private static ResponseMessage Step(JsonObject reply) => new ResponseMessage {
            Message = (string?)reply["Message"] ?? "", Meta = (JsonObject)reply["Meta"]!.DeepClone()
        };
        private static ResponseMessage NoProject(string tool) => new ResponseMessage {
            Message = "Project is null", Meta = ResponseMeta.Step(tool, ("error", JsonValue.Create("Project is null")), ("status", JsonValue.Create("InvalidState")), ("operationSuccess", JsonValue.Create(false)))
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
                    : tool + " requires one exact " + (tool == "DeletePlcBlock" ? "block" : "type") + " path; regular expressions and wildcards are not allowed");
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
            catch (PlcSoftwareException error) { throw new PortalException(PortalErrorCode.InvalidParams, error.Message); }
        }
        public JsonObject CreatePlcTypeGroup(string softwarePath, string groupPath, bool dryRun = true)
        {
            ValidateGroup(groupPath, true);
            if (!hasProject()) throw new PortalException(PortalErrorCode.InvalidState, "No TIA project is open.");
            return (JsonObject)Invoke(new PlcSoftwareRequest { Operation = "CreatePlcTypeGroup", SoftwarePath = softwarePath,
                GroupPath = groupPath, DryRun = dryRun })["Data"]!.DeepClone();
        }
        public JsonObject DeleteEmptyPlcBlockGroup(string softwarePath, string groupPath, bool dryRun = true)
        {
            ValidateGroup(groupPath, false);
            if (!hasProject()) throw new PortalException(PortalErrorCode.InvalidState, "No TIA project is open.");
            return (JsonObject)Invoke(new PlcSoftwareRequest { Operation = "DeleteEmptyPlcBlockGroup", SoftwarePath = softwarePath,
                GroupPath = groupPath, DryRun = dryRun })["Data"]!.DeepClone();
        }
        public object? EnsurePlcBlockGroup(string softwarePath, string groupPath, out List<string> created)
        {
            created = new List<string>(); if (!hasProject()) return null;
            var reply = Invoke(new PlcSoftwareRequest { Operation = "CreatePlcBlockGroup", SoftwarePath = softwarePath,
                GroupPath = groupPath, DryRun = false });
            created = JsonSerializer.Deserialize<List<string>>(reply["Created"]!.ToJsonString())!;
            return (bool?)reply["Found"] == true ? reply : null;
        }
        public string MoveBlockToGroup(string softwarePath, string blockName, string targetGroupPath, bool autoCreateGroup = true)
        {
            if (!hasProject()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            return (string)Invoke(new PlcSoftwareRequest { Operation = "MovePlcBlockToGroup", SoftwarePath = softwarePath,
                Name = blockName, GroupPath = targetGroupPath, AutoCreateGroup = autoCreateGroup, DryRun = false })["Message"]!;
        }
        public ResponseMessage ManagePlcUserGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
        {
            var unavailable = PlcSoftwareCapabilities.Unsupported(HardwareContract.ReleaseKey, "ManagePlcUserGroup", action, family);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            if (!hasProject()) return NoProject("ManagePlcUserGroup");
            return Step(Invoke(new PlcSoftwareRequest { Operation = "ManagePlcUserGroup", SoftwarePath = softwarePath,
                Family = family, GroupPath = groupPath, Action = action, NewName = newName, DryRun = dryRun }));
        }
        public ResponseMessage ManagePlcBlockProtection(string softwarePath, string blockPath, string action, string password = "", bool confirmProtectionChange = false, bool dryRun = true)
        {
            var unavailable = PlcSoftwareCapabilities.Unsupported(HardwareContract.ReleaseKey, "ManagePlcBlockProtection", action);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            if (!hasProject()) return NoProject("ManagePlcBlockProtection");
            return Step(Invoke(new PlcSoftwareRequest { Operation = "ManagePlcBlockProtection", SoftwarePath = softwarePath,
                Path = blockPath, Action = action, Password = password, Confirm = confirmProtectionChange, DryRun = dryRun }));
        }
        public ResponseMessage ReadPlcSystemGroups(string softwarePath, string unitName = "", string unitKind = "unit", bool includeBlocks = true, int maxDepth = 4)
        {
            var unavailable = PlcSoftwareCapabilities.Unsupported(HardwareContract.ReleaseKey, "ListPlcSystemGroups", unitName: unitName, unitKind: unitKind);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            if (!hasProject()) return NoProject("ListPlcSystemGroups");
            return Step(Invoke(new PlcSoftwareRequest { Operation = "ListPlcSystemGroups", SoftwarePath = softwarePath,
                UnitName = unitName, UnitKind = unitKind, IncludeBlocks = includeBlocks, MaxDepth = maxDepth }));
        }
    }
}
