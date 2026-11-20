using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcpServer.Siemens;

namespace TiaMcp.Adapters.Native.Vci;

internal sealed partial class VersionControlAdapter
{
#if STUDIO_VCI && !STUDIO_VCI_MODERN
    internal static bool LegacyXmlExport => true;
#else
    internal static bool LegacyXmlExport => false;
#endif

    internal static T Select<T>(IReadOnlyList<T> workspaces, string name, Func<T, string> readName) where T : class
    {
        if (workspaces.Count == 0)
            throw AdapterPreconditionException.Failed("This project has no version control workspace yet. Create one with " +
                "CreateVersionControlWorkspace, then map objects into it with ConnectProjectToWorkspace (no UI interaction needed).", "no-workspace", null);
        if (string.IsNullOrWhiteSpace(name)) return workspaces[0];
        return workspaces.FirstOrDefault(w => string.Equals(readName(w), name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw AdapterPreconditionException.NotFound("No workspace named '" + name + "'. Available: " +
                string.Join(", ", workspaces.Select(readName)), "workspaceName");
    }

    internal static HmiObject FailureEvidence(Exception error, bool issued = false)
    {
        var meta = HmiMeta.Basic(false, ("mayHaveChanged", issued));
        if (error is AdapterPreconditionException { Condition: string condition }) meta["condition"] = condition;
        if (issued) return meta;
        meta["v4Rejection"] = error is PortalException { Code: PortalErrorCode.InvalidState } ? "PROJECT_NOT_BOUND"
            : error is AdapterPreconditionException { Condition: "not-found" } ? "NOT_FOUND"
            : error is AdapterPreconditionException ? "PRECONDITION_FAILED"
            : error is ArgumentException ? "INVALID_ARGUMENT"
            : error is DirectoryNotFoundException ? "NOT_FOUND" : null;
        return meta;
    }

    internal static VersionControlResult WithWriteOutcome(VersionControlRequest request, VersionControlResult reply)
    {
        reply.OutcomeUnknown = request.Operation is "ConnectProjectToWorkspace" or "SynchronizeVersionControlWorkspace"
            ? reply.Meta.TryGetValue("writeOutcomeUnknown", out var unknown) && unknown is true
            : !request.DryRun && reply.Meta.TryGetValue("success", out var success) && success is false
                && (reply.MayHaveChanged || reply.Meta.TryGetValue("failed", out var failed) && failed is int count && count > 0);
        if (reply.OutcomeUnknown) reply.Meta["writeOutcomeUnknown"] = true;
        return reply;
    }
}
