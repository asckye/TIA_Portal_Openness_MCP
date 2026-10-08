using System;
using TiaMcp.Adapters.Contracts;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.Adapters
{
    internal sealed class PlcExternalSourceDeleteIdentities
    {
        private readonly List<KeyValuePair<object,string>> identities=new List<KeyValuePair<object,string>>();
        internal string Get(object value)
        {
            foreach(var pair in identities) if(object.Equals(pair.Key,value)) return pair.Value;
            if(identities.Count>=8192) throw new AdapterPreconditionException("Delete identity budget exhausted; new explicitly reviewed session required.","softwarePath",false);
            var id=Guid.NewGuid().ToString("N");identities.Add(new KeyValuePair<object,string>(value,id));return id;
        }
        internal object Resolve(string identity)=>identities.Single(p=>p.Value==identity).Key;
    }
    internal sealed class PlcExternalSourceDeleteItem
    {
        internal string Name="", Identity="";
        internal bool ParentVerified;
    }
    internal sealed class PlcExternalSourceDeleteRequest
    {
        internal string Release="", Project="", Software="", Group="", Name="", RootIdentity="", ExpectedHash="", ExpectedProject="";
        internal int ProcessId;
        internal bool DryRun=true, Confirm;
    }
    internal static class PlcExternalSourceDeletePolicy
    {
        internal static void ValidateOptions(PlcExternalSourceDeleteRequest r)
        {
            if(!new[]{"14sp1","15.1","16","17","18","19","20","21"}.Contains(r.Release)) throw new AdapterPreconditionException("Unknown exact release.","release");
            if(r.Group!="") throw new AdapterPreconditionException("Only the empty external-source root group is supported.","groupPath");
            if(string.IsNullOrWhiteSpace(r.Name) || r.Name.Length>256 || r.Name.Any(c=>char.IsControl(c) || c=='/' || c=='\\') || r.Name=="." || r.Name=="..") throw new AdapterPreconditionException("One exact source name is required; no path or extension fallback.","externalSourceName");
            if(!r.DryRun && (!r.Confirm || r.ExpectedHash.Length!=64 || r.ExpectedHash.Any(c=>!"0123456789abcdef".Contains(c)))) throw new AdapterPreconditionException("Delete requires confirm=true and the exact preview plan hash.","expectedPlanHash");
            if(!r.DryRun) MutationIdentityPolicy.AbsoluteFile(r.ExpectedProject);
        }
        private static PlcExternalSourceDeleteItem[] Snapshot(Func<IEnumerable<PlcExternalSourceDeleteItem>> read)
        {
            var items=read().Take(4097).ToArray();
            if(items.Length>4096 || items.Any(x=>x==null || !x.ParentVerified || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length>256 || string.IsNullOrWhiteSpace(x.Identity))) throw new AdapterPreconditionException("Incomplete, oversized or wrong-parent source inventory.","softwarePath",false);
            if(items.Select(x=>x.Name).Distinct(StringComparer.Ordinal).Count()!=items.Length || items.Select(x=>x.Identity).Distinct(StringComparer.Ordinal).Count()!=items.Length) throw new AdapterPreconditionException("Ambiguous source inventory.","softwarePath",false);
            return items.OrderBy(x=>x.Name,StringComparer.Ordinal).ToArray();
        }
        private static string Field(string s)=>s.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+s;
        private static string Hash(PlcExternalSourceDeleteRequest r,PlcExternalSourceDeleteItem[] items)
        {
            var data=string.Concat(new[]{"root-external-source-delete-v1",r.Release,MutationIdentityPolicy.AbsoluteFile(r.Project).ToUpperInvariant(),r.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),r.Software,r.Group,r.Name,r.RootIdentity}.Concat(items.SelectMany(x=>new[]{x.Name,x.Identity})).Select(Field));
            using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(data))).Replace("-","").ToLowerInvariant();
        }
        internal static PlcExternalSourceDeleteResult Run(PlcExternalSourceDeleteRequest r,Func<IEnumerable<PlcExternalSourceDeleteItem>> read,Action check,Action<string> validateTarget,Action<string> delete)
        {
            ValidateOptions(r);
            if(r.ProcessId<=0 || string.IsNullOrWhiteSpace(r.RootIdentity) || string.IsNullOrWhiteSpace(r.Software)) throw new AdapterPreconditionException("Exact process, root and software identities required.","softwarePath");
            if(!r.DryRun) MutationIdentityPolicy.RequireSameProject(r.ExpectedProject,r.Project);
            check(); var before=Snapshot(read); var hash=Hash(r,before);
            var target=before.SingleOrDefault(x=>x.Name==r.Name);
            var result=new PlcExternalSourceDeleteResult {Release=r.Release,ProjectFile=r.Project,ProcessId=r.ProcessId,SoftwarePath=r.Software,SourceName=r.Name,RootIdentity=r.RootIdentity,TargetIdentity=target?.Identity??"",PlanHash=hash,Inventory=before.Select(x=>x.Name).ToArray()};
            if(!r.DryRun && r.ExpectedHash!=hash) throw new AdapterPreconditionException("Target/root inventory or reviewed plan changed; no delete attempted.","softwarePath",false);
            if(target==null) {result.Status="not-found-not-deleted";return result;}
            if(r.DryRun) return result;
            check(); var fresh=Snapshot(read);
            if(Hash(r,fresh)!=hash) throw new AdapterPreconditionException("Source identities changed before delete; no delete attempted.","softwarePath",false);
            check(); validateTarget(target.Identity);
            result.Attempted=true;
            try
            {
                delete(target.Identity); // exactly one attempt; never retried, including native exceptions
                check();var after=Snapshot(read);
                var expected=before.Where(x=>x.Identity!=target.Identity).ToArray();
                if(after.Length!=expected.Length || !after.Select(x=>Field(x.Name)+Field(x.Identity)).SequenceEqual(expected.Select(x=>Field(x.Name)+Field(x.Identity)),StringComparer.Ordinal)) throw new InvalidOperationException("Post-delete root inventory differs from the single planned removal.");
                result.Status="deleted-verified";result.Executed=true;result.Deleted=true;
            }
            catch(Exception ex) {result.Status="outcome-unknown";result.RequiresSessionReset=true;result.Error=ex.GetType().Name+": "+ex.Message;}
            return result;
        }
    }
}
