using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.LegacyHost;

internal static class CandidateWire
{
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static T Read<T>(JsonNode? body) where T : class => body?.Deserialize<T>(Options) ?? throw new InvalidDataException("Missing candidate primitive observation.");
    private static string Action(JsonObject args) => (string?)args["candidate"]?["Action"] ?? throw new InvalidDataException("Missing candidate primitive action.");
    private static void Attempt(bool issued, bool reset, bool fault)
    { if (reset != (issued && fault) || !issued && !fault) throw new InvalidDataException("Conflicting candidate native outcome."); }
    internal static DeviceCandidateReply Device(JsonNode? body, JsonObject args)
    {
        var reply = Read<DeviceCandidateReply>(body); string action = Action(args);
        if (reply.Fault != null)
        { if (reply.RequiresSessionReset || reply.Attempt != null) throw new InvalidDataException("Conflicting candidate preflight observation."); return reply; }
        if (action == "execute")
        {
            var attempt = reply.Attempt ?? throw new InvalidDataException("Missing candidate native outcome.");
            Attempt(attempt.Issued, attempt.RequiresSessionReset, attempt.Fault != null);
            if (reply.RequiresSessionReset != attempt.RequiresSessionReset || attempt.Fault == null && (attempt.Created == null || attempt.Residue.Status != "checked"))
                throw new InvalidDataException("Incomplete candidate readback.");
            if (attempt.Fault == null)
                CandidateExecution.VerifyDeviceReadback(Read<DeviceCandidateCall>(args["candidate"]).Check!, reply.RootId, attempt.Created!, attempt.After);
        }
        else if (reply.RequiresSessionReset || reply.Attempt != null || action == "identity" && reply.Identity == null || action == "catalog" && reply.Catalog == null || action == "inventory" && reply.Inventory == null)
            throw new InvalidDataException("Incomplete candidate observation.");
        return reply;
    }

    internal static SessionCandidateReply Session(JsonNode? body, JsonObject args)
    {
        var reply = Read<SessionCandidateReply>(body); string action = Action(args);
        if (reply.Fault != null)
        { if (reply.RequiresSessionReset || reply.Attempt != null) throw new InvalidDataException("Conflicting session preflight observation."); return reply; }
        if (action == "execute")
        {
            var attempt = reply.Attempt ?? throw new InvalidDataException("Missing session native outcome.");
            Attempt(attempt.Issued, attempt.RequiresSessionReset, attempt.Fault != null);
            if (attempt.Reason != null && attempt.Reason != SessionPrimitives.ConfirmationReason) throw new InvalidDataException("Unknown session timeout reason.");
            if (reply.RequiresSessionReset != attempt.RequiresSessionReset) throw new InvalidDataException("Conflicting session reset state.");
            if (attempt.After != null) TiaMcp.Logic.V4.SessionCandidateSession.ValidateObservation(attempt.After);
            if (attempt.Fault == null) CandidateExecution.VerifySessionReadback(Read<SessionCandidateCall>(args["candidate"]).Check!, attempt.After!);
        }
        else
        {
            if (action != "observe" || reply.Attempt != null || reply.RequiresSessionReset || reply.Observation == null) throw new InvalidDataException("Incomplete session observation.");
            TiaMcp.Logic.V4.SessionCandidateSession.ValidateObservation(reply.Observation);
        }
        return reply;
    }
    internal static SaveCloseReply SaveClose(JsonNode? body, JsonObject args)
    {
        var reply = Read<SaveCloseReply>(body); string action = Action(args);
        if (reply.Fault != null)
        { if (reply.RequiresSessionReset || reply.Attempt != null || reply.Observation != null) throw new InvalidDataException("Conflicting save/close preflight reply."); return reply; }
        if (action == "execute")
        {
            var attempt = reply.Attempt ?? throw new InvalidDataException("Missing save/close outcome.");
            Attempt(attempt.Issued, attempt.RequiresSessionReset, attempt.Fault != null);
            if (reply.RequiresSessionReset != attempt.RequiresSessionReset || reply.Observation != null) throw new InvalidDataException("Conflicting save/close reset state.");
            if (attempt.After != null) TiaMcp.Logic.V4.SaveCloseSession.ValidateObservation(attempt.After);
            if (attempt.Fault == null) CandidateExecution.VerifySaveCloseReadback(Read<SaveCloseCall>(args["candidate"]).Check!, attempt.After!);
        }
        else
        {
            if (action != "observe" || reply.Attempt != null || reply.RequiresSessionReset || reply.Observation == null) throw new InvalidDataException("Incomplete save/close observation.");
            TiaMcp.Logic.V4.SaveCloseSession.ValidateObservation(reply.Observation);
        }
        return reply;
    }
    internal static ImportCandidateReply Import(JsonNode? body, JsonObject args)
    {
        var reply = Read<ImportCandidateReply>(body); string action = Action(args);
        if (reply.Fault != null)
        { if (reply.RequiresSessionReset || reply.Attempt != null) throw new InvalidDataException("Conflicting candidate preflight observation."); return reply; }
        if (action == "execute")
        {
            var attempt = reply.Attempt ?? throw new InvalidDataException("Missing candidate native outcome.");
            Attempt(attempt.Issued, attempt.RequiresSessionReset, attempt.Fault != null);
            if (reply.RequiresSessionReset != attempt.RequiresSessionReset || attempt.Fault == null && (attempt.Imported == null || attempt.ContentHash.Length != 64 || attempt.Residue.Status != "checked"))
                throw new InvalidDataException("Incomplete candidate content readback.");
            if (attempt.Fault == null)
                CandidateExecution.VerifyImportReadback(Read<ImportCandidateCall>(args["candidate"]).Check!, attempt.Imported!, attempt.ContentHash, attempt.After);
        }
        else if (reply.RequiresSessionReset || reply.Attempt != null || action == "identity" && reply.Identity == null || action == "inputs" && reply.Inputs == null || action == "inventory" && reply.Inventory == null)
            throw new InvalidDataException("Incomplete candidate observation.");
        return reply;
    }
    internal static ExportCandidateReply Export(JsonNode? body, JsonObject args)
    {
        var reply = Read<ExportCandidateReply>(body); string action = Action(args);
        if (reply.Fault != null)
        { if (reply.RequiresSessionReset || reply.Attempt != null) throw new InvalidDataException("Conflicting export preflight observation."); return reply; }
        if (action == "execute")
        {
            var attempt = reply.Attempt ?? throw new InvalidDataException("Missing export native outcome.");
            Attempt(attempt.Issued, attempt.RequiresSessionReset, attempt.Fault != null);
            if (reply.RequiresSessionReset != attempt.RequiresSessionReset) throw new InvalidDataException("Conflicting export reset state.");
            if (attempt.Fault == null) TiaMcp.Logic.V4.PlcExportSession.VerifyStaged(Read<ExportCandidateCall>(args["candidate"]).Check!, attempt);
        }
        else if (reply.RequiresSessionReset || reply.Attempt != null || action == "identity" && reply.Identity == null || action == "objects" && reply.Objects == null)
            throw new InvalidDataException("Incomplete export observation.");
        return reply;
    }

}
