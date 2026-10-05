using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Logic.V4
{
    public static class CandidateHostMapping
    {
        public static PlanIdentity Plan(CandidateIdentity i) => new PlanIdentity(i.ProcessId, i.ProcessStartUtc, i.ProjectFile, i.BindingEpoch, null, Array.Empty<PlanFile>());
        public static CandidateIdentity Identity(PlanIdentity i) => new CandidateIdentity(i.ProcessId, i.ProcessStartUtc, i.ProjectFile, i.BindingEpoch);
        public static CandidateFile[] Files(PlanFile[] files) => files.Select(f => new CandidateFile { Path = f.Path, Exists = f.Exists, ByteLength = f.ByteLength, Sha256 = f.Sha256 }).ToArray();
        public static DeviceCreationRejection Device(CandidateFault fault, string? hash = null) => new DeviceCreationRejection(fault.Kind == "project"
            ? new Error("No project is bound.", new ProjectNotBoundDetails()) : fault.Kind == "identity"
            ? new Error("The project or process binding changed since preview.", new IdentityMismatchDetails("project-binding", null, null))
            : fault.Kind == "stale" ? new Error("The catalog, inventory or arguments changed since preview.", new PlanStaleDetails(hash, "device-plan-changed"))
            : new Error("Preflight could not establish a complete device-creation plan. No Create was issued.", new PreconditionFailedDetails(fault.Subject == "complete-inventory" ? "complete-inventory" : "device-create-preflight", null)));
        public static PlcImportRejection Import(CandidateFault fault, string? hash = null)
        {
            ErrorDetails details;
            string message;
            switch (fault.Kind)
            {
                case "project": details = new ProjectNotBoundDetails(); message = "No project is bound."; break;
                case "identity": details = new IdentityMismatchDetails("project-binding", null, null); message = "The process or project binding changed since preview."; break;
                case "stale": details = new PlanStaleDetails(hash, fault.Subject); message = "The import plan changed since preview."; break;
                case "invalid": details = new InvalidArgumentDetails(fault.Subject, Array.Empty<string>()); message = "Invalid PLC import argument."; break;
                case "unsupported": details = new UnsupportedCapabilityDetails(fault.Message, "P6-IMPORT", fault.Subject); message = "The release/object does not support the requested import capability."; break;
                case "limit": details = new LimitExceededDetails(fault.Subject, fault.Limit, fault.Actual); message = fault.Message; break;
                case "not-found": details = new NotFoundDetails(fault.Subject); message = "Exact target group not found."; break;
                case "io": details = new IoFailedDetails("read", null); message = "Import preflight could not establish the reviewed target; no native import was issued for this item."; break;
                default: details = new PreconditionFailedDetails(fault.Subject == "complete-inventory" ? "complete-inventory" : "plc-import-preflight", null);
                    message = "Import preflight could not establish the reviewed target; no native import was issued for this item."; break;
            }
            return new PlcImportRejection(new Error(message, details));
        }
        public static JsonObject DeviceResidue(DeviceResidue residue) => residue.Status == "checked"
            ? new JsonObject { ["status"] = "checked", ["added"] = JsonNode.Parse(V4Json.Serialize(residue.Added)), ["removed"] = JsonNode.Parse(V4Json.Serialize(residue.Removed)), ["changed"] = JsonNode.Parse(V4Json.Serialize(residue.Changed)) }
            : new JsonObject { ["status"] = "unavailable", ["reason"] = residue.Reason };
        public static JsonObject ImportResidue(ImportResidue residue) => residue.Status == "checked"
            ? new JsonObject { ["status"] = "checked", ["added"] = JsonNode.Parse(V4Json.Serialize(residue.Added)), ["removed"] = JsonNode.Parse(V4Json.Serialize(residue.Removed)), ["changed"] = JsonNode.Parse(V4Json.Serialize(residue.Changed)) }
            : new JsonObject { ["status"] = "unavailable", ["reason"] = residue.Reason };
    }
}
