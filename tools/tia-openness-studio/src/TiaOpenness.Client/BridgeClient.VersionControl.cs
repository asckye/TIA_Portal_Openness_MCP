using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Rpc;
using TiaOpenness.Core.Inspection;

namespace TiaOpenness.Client
{
    public sealed partial class BridgeClient
    {
        private async Task<List<WorkspaceInfo>> Workspaces(CancellationToken ct)
        {
            var r = await Tool("GetVersionControlWorkspaces", new JObject(), ct).ConfigureAwait(false);
            return G(G(r, "meta"), "workspaces")?.ToObject<List<WorkspaceInfo>>()
                ?? throw new InvalidOperationException("Update the MCP engine to the integrated build for structured VCI data.");
        }
        private async Task<object> VersionControl(string method, JObject p, CancellationToken ct)
        {
            var workspace = p.Value<string>("workspaceName");
            switch (method)
            {
                case RpcMethods.VcWorkspaceList: return await Workspaces(ct).ConfigureAwait(false);
                case RpcMethods.VcWorkspaceCreate:
                    var name = p.Value<string>("name");
                    await Write("CreateVersionControlWorkspace", new JObject { ["workspaceName"] = name, ["folderPath"] = p.Value<string>("folderPath") }, ct).ConfigureAwait(false);
                    return (await Workspaces(ct).ConfigureAwait(false)).Single(w => w.Name == name);
                case RpcMethods.VcStatus:
                    var status = await Tool("GetVersionControlStatus", new JObject { ["workspaceName"] = workspace ?? "", ["changedOnly"] = p.Value<bool?>("changedOnly") ?? true }, ct).ConfigureAwait(false);
                    var meta = G(status, "meta");
                    return new WorkspaceStatusReport { WorkspaceName = S(meta, "workspaceName"), RootPath = S(meta, "rootPath"),
                        Total = G(meta, "total").Value<int>(), Differing = G(meta, "differing").Value<int>(),
                        Items = G(meta, "objects").ToObject<List<MappedObjectInfo>>() };
                case RpcMethods.VcMapProject:
                    // DeviceId is a PLC software path in the integrated UI, not a hardware station name.
                    // This toolbar action maps the whole project, matching the engine's default.
                    var map = await Write("ConnectProjectToWorkspace", new JObject { ["workspaceName"] = workspace ?? "", ["dryRun"] = p.Value<bool?>("dryRun") ?? true }, ct).ConfigureAwait(false);
                    var mapping = G(map, "meta").ToObject<MappingResult>();
                    mapping.Items = (G(map, "items") as JArray ?? new JArray()).Select(x => new MappingItem { Target = "Project", Outcome = x.ToString() }).ToList();
                    return mapping;
                case RpcMethods.VcSync:
                    var direction = Enum.Parse<SyncDirection>(p.Value<string>("direction") ?? "ProjectToWorkspace");
                    var sync = await Write("SyncVersionControlWorkspace", new JObject { ["workspaceName"] = workspace ?? "", ["direction"] = direction.ToString(), ["dryRun"] = p.Value<bool?>("dryRun") ?? true }, ct).ConfigureAwait(false);
                    var synced = G(sync, "meta").ToObject<SyncResult>();
                    synced.Direction = direction;
                    synced.Items = (G(sync, "items") as JArray ?? new JArray()).Select(x => new SyncItem { Name = "Workspace", Outcome = x.ToString() }).ToList();
                    return synced;
                case RpcMethods.VcDiff:
                    var spaces = await Workspaces(ct).ConfigureAwait(false);
                    var selected = string.IsNullOrWhiteSpace(workspace) ? spaces.First() : spaces.Single(w => w.Name == workspace);
                    return GitWorkspaceDiff.Read(selected.Name, selected.RootPath, p.Value<string>("file"));
                default: throw new NotSupportedException("Unknown VCI method: " + method);
            }
        }
    }
}
