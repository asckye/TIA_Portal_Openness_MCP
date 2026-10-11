using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class VersionControlPortService
{
    private readonly Func<string, JsonObject, JsonNode?> call;
    private readonly Func<string> identity;

    public VersionControlPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }

    internal VersionControlPortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        this.call = call;
        this.identity = identity;
    }

    private T Invoke<T>(VersionControlRequest request)
    {
        if (!PortedFamilies.Available(HardwareContract.ReleaseKey, request.Operation))
            throw new NotSupportedException("VersionControlInterface requires V16 or later.");
        if (string.IsNullOrEmpty(identity()))
            return JsonSerializer.Deserialize<T>(Unbound(request).ToJsonString())!;
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
        if (!request.DryRun)
        {
            arguments["confirm"] = true;
            arguments["expectedProjectFile"] = identity();
        }
        var reply = call("version-control.ExecuteVersionControl", arguments)
            ?? throw new InvalidOperationException("Missing VCI worker reply.");
        return JsonSerializer.Deserialize<T>(reply.ToJsonString())!;
    }

    // The cached binding preserves the retired service's project admission. The
    // generic port dispatcher still refuses every attempted native call without
    // a portal; this path never dispatches or inspects a native object.
    private static JsonObject Unbound(VersionControlRequest request)
    {
        var meta = ResponseMeta.Basic(false);
        meta["mayHaveChanged"] = false;
        meta["v4Rejection"] = "PROJECT_NOT_BOUND";
        string message = request.Operation + " failed: No project is open. Call ConnectPortal, then AttachOpenProject / OpenProject first.";
        try
        {
            if (request.Operation == "CreateVersionControlWorkspace")
            {
                if (string.IsNullOrWhiteSpace(request.WorkspaceName)) throw new ArgumentException("workspaceName is required.");
                if (string.IsNullOrWhiteSpace(request.FolderPath)) throw new ArgumentException("folderPath is required.");
                var directory = new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(request.FolderPath.Trim()));
                if (!directory.Exists) throw new DirectoryNotFoundException("folderPath does not exist: " + directory.FullName + ". Create the folder (or clone the repo) first.");
            }
            if (request.Operation == "SynchronizeVersionControlWorkspace")
            {
                string direction = (request.Direction ?? "").Trim();
                if (!direction.Equals("ProjectToWorkspace", StringComparison.OrdinalIgnoreCase))
                {
                    meta.Remove("mayHaveChanged");
                    meta["v4Rejection"] = direction.Equals("WorkspaceToProject", StringComparison.OrdinalIgnoreCase) ? "UNSUPPORTED_CAPABILITY" : "INVALID_ARGUMENT";
                }
            }
        }
        catch (Exception error)
        {
            message = request.Operation + " failed: " + error.Message;
            meta["v4Rejection"] = error is ArgumentException ? "INVALID_ARGUMENT" : error is DirectoryNotFoundException ? "NOT_FOUND" : null;
        }
        return new JsonObject { ["Message"] = message, ["Items"] = new JsonArray(), ["Meta"] = meta };
    }

    public ResponseStringList GetVersionControlWorkspaces()
        => Invoke<ResponseStringList>(new VersionControlRequest { Operation = "ListVersionControlWorkspaces" });

    public ResponseStringList GetVersionControlStatus(string workspaceName = "", bool changedOnly = true)
        => Invoke<ResponseStringList>(new VersionControlRequest { Operation = "GetVersionControlStatus", WorkspaceName = workspaceName, ChangedOnly = changedOnly });

    public ResponseMessage CreateVersionControlWorkspace(string workspaceName, string folderPath)
        => Invoke<ResponseMessage>(new VersionControlRequest { Operation = "CreateVersionControlWorkspace", WorkspaceName = workspaceName, FolderPath = folderPath, DryRun = false });

    public ResponseStringList ConnectProjectToWorkspace(string workspaceName = "", bool dryRun = true, string deviceFilter = "", int maxObjects = 3000, bool walkTrace = false)
        => Invoke<ResponseStringList>(new VersionControlRequest { Operation = "ConnectProjectToWorkspace", WorkspaceName = workspaceName, DryRun = dryRun, DeviceFilter = deviceFilter, MaxObjects = maxObjects, WalkTrace = walkTrace });

    public ResponseStringList SyncVersionControlWorkspace(string direction = "ProjectToWorkspace", string workspaceName = "", bool dryRun = true, bool changedOnly = true)
        => Invoke<ResponseStringList>(new VersionControlRequest { Operation = "SynchronizeVersionControlWorkspace", Direction = direction, WorkspaceName = workspaceName, DryRun = dryRun, ChangedOnly = changedOnly });
}
