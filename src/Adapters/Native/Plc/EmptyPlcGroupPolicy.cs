using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Native.Plc
{
    internal static class EmptyPlcGroupPolicy
    {
        internal static string[] Parse(string path) => PlcOrganisationPaths.Parse(path, false);
        internal static Dictionary<string, object?> Execute<T>(string path, bool dryRun, Func<string[],T?> find,
            Func<T,int> blockCount, Func<T,int> childCount, Func<string> state,
            Action<T> delete) where T : class
        {
            var parts=Parse(path);
            var target=find(parts) ?? throw new PlcSoftwareException("NotFound", "PLC user block group not found: " + path);
            int blocks=blockCount(target), groups=childCount(target);
            if (blocks!=0 || groups!=0) throw new PlcSoftwareException("InvalidParams",
                $"Group '{path}' is not empty: {blocks} block(s), {groups} subgroup(s). Nothing deleted.");
            var online=state();
            var result=new Dictionary<string, object?> { ["groupPath"]=string.Join("/",parts), ["dryRun"]=dryRun,
                ["blockCount"]=blocks, ["subgroupCount"]=groups, ["onlineState"]=online,
                ["canDelete"]=online=="Offline", ["deleted"]=false, ["verifiedAbsent"]=false };
            if (dryRun) return result;
            if (online!="Offline") throw new PlcSoftwareException("InvalidState",
                "Deletion requires a confirmed Offline state; actual state: " + online);
            // Recheck immediately before the native Delete call while holding exclusive access.
            if (blockCount(target)!=0 || childCount(target)!=0 || state()!="Offline")
                throw new PlcSoftwareException("InvalidState", "Group contents or online state changed; nothing deleted.");
            delete(target);
            try
            {
                if (find(parts)!=null) throw new InvalidOperationException("Group is still present.");
            }
            catch (Exception ex) { throw new PlcSoftwareException("OpennessError",
                "Delete returned, but absence verification failed. Do not automatically retry. " + ex.Message, null, ex); }
            result["deleted"]=true; result["verifiedAbsent"]=true;
            return result;
        }
    }
}
