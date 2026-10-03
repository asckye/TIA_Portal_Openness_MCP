using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.PlcFoundation
{
    internal sealed class PlcDeviceAddRequest
    {
        internal string Release="", Project="", RootIdentity="", PreferredMlfb="", PreferredVersion="", Name="", Family="S7-1500", ExpectedHash="", ExpectedProject="";
        internal int ProcessId;
        internal bool DryRun=true, Confirm;
    }
    internal sealed class PlcDeviceAddItem
    {
        internal string Name="", Identity="", ParentIdentity="";
        internal bool IsGroup;
        internal bool ParentVerified;
    }
    internal static class PlcDeviceAddPolicy
    {
        internal static void ValidateOptions(PlcDeviceAddRequest r)
        {
            PlcHardwareCatalogPolicy.RequireRelease(r.Release);
            if(r.Family!="S7-1200" && r.Family!="S7-1500") throw new ArgumentException("Only explicit S7-1200/S7-1500 hints are accepted; no HMI or family fallback.");
            Text(r.PreferredMlfb,256,"Exact catalog identifier or article number required.");
            if(r.PreferredMlfb.StartsWith("OrderNumber:",StringComparison.Ordinal))
            {
                if(r.PreferredVersion!="") throw new ArgumentException("A full exact TypeIdentifier requires an empty preferredVersion.");
            }
            else Text(r.PreferredVersion,64,"Exact catalog version required; no default or fallback versions.");
            Text(r.Name,128,"Exact device name required.");
            if(r.Name=="." || r.Name==".." || r.Name.Any(c=>"/\\:*?\"<>|".IndexOf(c)>=0) || r.Name.Trim()!=r.Name) throw new ArgumentException("Ambiguous device name.");
            if(!r.DryRun)
            {
                if(!r.Confirm || r.ExpectedHash==null || r.ExpectedHash.Length!=64 || r.ExpectedHash.Any(c=>!"0123456789abcdef".Contains(c))) throw new ArgumentException("Apply requires confirm=true and the exact preview hash.");
                MutationIdentityPolicy.AbsoluteFile(r.ExpectedProject);
            }
        }
        private static void Text(string s,int maximum,string error)
        {
            if(string.IsNullOrWhiteSpace(s) || s.Length>maximum || s.Any(char.IsControl)) throw new ArgumentException(error);
        }
        private static PlcHardwareCatalogCandidate Select(PlcDeviceAddRequest r,Func<IEnumerable<PlcHardwareCatalogCandidate>> read)
        {
            var rows=read().Take(1001).ToArray();
            if(rows.Length>1000 || rows.Any(x=>x==null)) throw new InvalidOperationException("Incomplete or oversized catalog selection.");
            foreach(var row in rows)
            {
                Text(row.TypeIdentifier!,16384,"Missing/oversized catalog identifier.");
                if((row.ArticleNumber?.Length??0)>256 || (row.Version?.Length??0)>64) throw new InvalidOperationException("Oversized catalog fields.");
            }
            var matched=rows.Where(x=>r.PreferredMlfb.StartsWith("OrderNumber:",StringComparison.Ordinal)
                ? x.TypeIdentifier==r.PreferredMlfb
                : x.ArticleNumber==r.PreferredMlfb && x.Version==r.PreferredVersion).ToArray();
            if(matched.Length!=1) throw new InvalidOperationException("Exactly one exact catalog candidate required; no inferred substitution or probing.");
            var chosen=matched[0];
            if(!chosen.TypeIdentifier!.StartsWith("OrderNumber:",StringComparison.Ordinal)) throw new ArgumentException("Only exact Siemens OrderNumber identifiers are accepted.");
            Text(chosen.ArticleNumber!,256,"Selected article number required."); Text(chosen.Version!,64,"Selected version required.");
            var allowed=r.Family=="S7-1200" ? new[]{"6ES7211-1BE40-0XB0","6ES7 211-1BE40-0XB0"} : new[]{"6ES7513-1AM03-0AB0","6ES7 513-1AM03-0AB0"};
            if(!allowed.Contains(chosen.ArticleNumber,StringComparer.Ordinal) || chosen.TypeIdentifier!="OrderNumber:"+chosen.ArticleNumber+"/"+chosen.Version || chosen.Version!.Any(c=>c=='*'||c=='?'||c=='/'||char.IsControl(c))) throw new NotSupportedException("Only the two documented standard PLC article identities are admitted, with the matching family and exact catalog identifier/version; all other types fail closed.");
            return chosen;
        }
        private static PlcDeviceAddItem[] Snapshot(Func<IEnumerable<PlcDeviceAddItem>> read)
        {
            var items=read().Take(4097).ToArray();
            if(items.Length>4096 || items.Any(x=>x==null || !x.ParentVerified)) throw new InvalidOperationException("Incomplete, oversized or wrong-parent device inventory.");
            foreach(var x in items) { Text(x.Name,128,"Device name unavailable.");Text(x.Identity,128,"Device identity unavailable.");Text(x.ParentIdentity,128,"Device parent identity unavailable."); }
            if(items.Where(x=>!x.IsGroup).Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=items.Count(x=>!x.IsGroup) || items.Select(x=>x.Identity).Distinct(StringComparer.Ordinal).Count()!=items.Length) throw new InvalidOperationException("Ambiguous device inventory.");
            return items.OrderBy(x=>x.Identity,StringComparer.Ordinal).ToArray();
        }
        private static string Field(string s)=>s.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+s;
        private static string Hash(PlcDeviceAddRequest r,PlcHardwareCatalogCandidate c,PlcDeviceAddItem[] items)
        {
            var fields=new[]{"exact-catalog-root-device-add-v1",r.Release,MutationIdentityPolicy.AbsoluteFile(r.Project).ToUpperInvariant(),r.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),r.RootIdentity,r.PreferredMlfb,r.PreferredVersion,r.Name,r.Family,c.TypeIdentifier!,c.ArticleNumber!,c.Version!}.Concat(items.SelectMany(x=>new[]{x.Identity,x.Name,x.ParentIdentity,x.IsGroup?"group":"device"}));
            using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Concat(fields.Select(Field))))).Replace("-","").ToLowerInvariant();
        }
        internal static PlcDeviceAddResult Run(PlcDeviceAddRequest r,Func<IEnumerable<PlcHardwareCatalogCandidate>> catalog,Func<IEnumerable<PlcDeviceAddItem>> inventory,Action check,Func<string,string,PlcDeviceAddItem> create)
        {
            ValidateOptions(r); Text(r.RootIdentity,128,"Explicit project root identity required.");
            if(r.ProcessId<=0) throw new ArgumentException("Explicit attached process required.");
            MutationIdentityPolicy.AbsoluteFile(r.Project);
            if(!r.DryRun) MutationIdentityPolicy.RequireSameProject(r.ExpectedProject,r.Project);
            check(); var selected=Select(r,catalog); var before=Snapshot(inventory);
            if(before.Any(x=>!x.IsGroup && string.Equals(x.Name,r.Name,StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Device name collision; no create attempted.");
            var hash=Hash(r,selected,before);
            var result=new PlcDeviceAddResult { Release=r.Release,ProjectFile=r.Project,ProcessId=r.ProcessId,DeviceName=r.Name,Family=r.Family,TypeIdentifier=selected.TypeIdentifier!,ArticleNumber=selected.ArticleNumber!,Version=selected.Version!,PlanHash=hash,Inventory=before.Where(x=>!x.IsGroup).Select(x=>x.Name).ToArray() };
            if(r.DryRun) return result;
            if(hash!=r.ExpectedHash) throw new InvalidOperationException("Reviewed device selection or project inventory changed.");
            check(); var freshSelection=Select(r,catalog); var freshInventory=Snapshot(inventory);
            if(Hash(r,freshSelection,freshInventory)!=hash) throw new InvalidOperationException("Device selection or inventory changed before creation.");
            check(); result.Attempted=true;
            try
            {
                var created=create(selected.TypeIdentifier!,r.Name); // Exactly one call, including on native exception.
                if(created==null || !created.ParentVerified || created.Name!=r.Name || created.IsGroup || created.ParentIdentity!=r.RootIdentity || string.IsNullOrWhiteSpace(created.Identity) || before.Any(x=>x.Identity==created.Identity)) throw new InvalidOperationException("Returned created device identity not verified.");
                check(); var after=Snapshot(inventory);
                if(after.Length!=before.Length+1 || !after.Any(x=>x.Identity==created.Identity && x.Name==r.Name && !x.IsGroup && x.ParentIdentity==r.RootIdentity) || !before.All(x=>after.Any(y=>y.Identity==x.Identity && y.Name==x.Name && y.ParentIdentity==x.ParentIdentity && y.IsGroup==x.IsGroup))) throw new InvalidOperationException("Post-create inventory differs from the one planned addition.");
                result.Status="created-verified"; result.Executed=true;
            }
            catch(Exception ex) { result.Status="outcome-unknown";result.RequiresSessionReset=true;var error=ex.GetType().Name+": "+ex.Message;result.Error=error.Length<=2048?error:error.Substring(0,2048); }
            return result;
        }
    }
}
