using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class EmptyPlcGroupDeletion
    {
        internal static string[] Parse(string path)
        {
            try { return TiaMcp.Adapters.Contracts.PlcOrganisationPaths.Parse(path, false); }
            catch (TiaMcp.Adapters.Contracts.PlcSoftwareException error) { throw new PortalException(PortalErrorCode.InvalidParams, error.Message); }
        }
        internal static JsonObject Execute<T>(string path, bool dryRun, Func<string[],T?> find,
            Func<T,int> blockCount, Func<T,int> childCount, Func<string> state,
            Action<T> delete) where T : class
        {
            var parts=Parse(path);
            var target=find(parts) ?? throw new PortalException(PortalErrorCode.NotFound, "PLC user block group not found: " + path);
            int blocks=blockCount(target), groups=childCount(target);
            if (blocks!=0 || groups!=0) throw new PortalException(PortalErrorCode.InvalidParams,
                $"Group '{path}' is not empty: {blocks} block(s), {groups} subgroup(s). Nothing deleted.");
            var online=state();
            var result=new JsonObject { ["groupPath"]=string.Join("/",parts), ["dryRun"]=dryRun,
                ["blockCount"]=blocks, ["subgroupCount"]=groups, ["onlineState"]=online,
                ["canDelete"]=online=="Offline", ["deleted"]=false, ["verifiedAbsent"]=false };
            if (dryRun) return result;
            if (online!="Offline") throw new PortalException(PortalErrorCode.InvalidState,
                "Deletion requires a confirmed Offline state; actual state: " + online);
            // Recheck immediately before the native Delete call while holding exclusive access.
            if (blockCount(target)!=0 || childCount(target)!=0 || state()!="Offline")
                throw new PortalException(PortalErrorCode.InvalidState, "Group contents or online state changed; nothing deleted.");
            delete(target);
            try
            {
                if (find(parts)!=null) throw new InvalidOperationException("Group is still present.");
            }
            catch (Exception ex) { throw new PortalException(PortalErrorCode.OpennessError,
                "Delete returned, but absence verification failed. Do not automatically retry. " + ex.Message, null, ex); }
            result["deleted"]=true; result["verifiedAbsent"]=true;
            return result;
        }
    }
}
