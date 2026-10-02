using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcHardwareCatalogCandidate
    {
        public string Source { get; set; } = "HardwareCatalog";
        public string Keyword { get; set; } = "";
        public string? ArticleNumber { get; set; }
        public string? CatalogPath { get; set; }
        public string? Description { get; set; }
        public string? TypeIdentifier { get; set; }
        public string? TypeIdentifierNormalized { get; set; }
        public string? TypeName { get; set; }
        public string? Version { get; set; }
        public bool? Insertable { get; set; }
        public int? Score { get; set; }
    }
    public sealed class PlcHardwareCatalogSearchResult
    {
        public string Keyword { get; set; } = "";
        public int Count { get; set; }
        public PlcHardwareCatalogCandidate[] Items { get; set; } = new PlcHardwareCatalogCandidate[0];
        public Dictionary<string,object> Meta { get; set; } = new Dictionary<string,object>();
    }
    internal static class PlcHardwareCatalogPolicy
    {
        internal const int MaximumResults=100, MaximumEntries=1000, MaximumCharacters=262144, MaximumFieldCharacters=16384;
        internal static string Query(string keyword,int limit)
        {
            if(keyword==null || string.IsNullOrWhiteSpace(keyword) || keyword.Length>256 || keyword.Any(char.IsControl))
                throw new ArgumentException("A nonempty keyword of at most 256 characters without control characters is required.");
            var query=keyword.Trim();
            // Reject broad wildcard-only searches; pass all other text literally to one documented Find call.
            if(query.All(c=>char.IsWhiteSpace(c) || c=='*' || c=='?')) throw new ArgumentException("A substantive catalog keyword is required.");
            if(limit<1 || limit>MaximumResults) throw new ArgumentOutOfRangeException(nameof(limit),"limit must be between 1 and 100.");
            return query;
        }
        internal static void RequireRelease(string release)
        {
            if(release!="19" && release!="20" && release!="21") throw new NotSupportedException("Hardware catalog search requires release-specific API and manual evidence; this release is not enabled.");
        }
        internal static PlcHardwareCatalogSearchResult Search(string release,string keyword,int limit,Func<string,IEnumerable<PlcHardwareCatalogCandidate>> find)
        {
            RequireRelease(release);
            var query=Query(keyword,limit);
            var rows=new List<PlcHardwareCatalogCandidate>();
            var seen=new HashSet<string>(StringComparer.Ordinal);
            var visited=0; var characters=0; string? reason=null;
            var source=find(query) ?? throw new InvalidOperationException("Hardware catalog returned an unavailable result collection.");
            using(var cursor=source.GetEnumerator())
            {
                while(true)
                {
                    if(visited==MaximumEntries) { reason="entry-budget"; break; }
                    if(!cursor.MoveNext()) break;
                    visited++;
                    var entry=cursor.Current ?? throw new InvalidOperationException("Hardware catalog returned a null entry.");
                    var fields=new[]{entry.ArticleNumber,entry.CatalogPath,entry.Description,entry.TypeIdentifier,entry.TypeIdentifierNormalized,entry.TypeName,entry.Version};
                    if(fields.Any(s=>s!=null && s.Length>MaximumFieldCharacters)) { reason="field-character-budget"; break; }
                    if(string.IsNullOrWhiteSpace(entry.TypeIdentifier)) throw new InvalidOperationException("Catalog entry has no exact type identifier.");
                    // Preserve exact identifiers, including casing, whitespace and version. Never synthesize an insertion identifier.
                    if(!seen.Add(entry.TypeIdentifier!)) continue;
                    if(rows.Count==limit) { reason="result-limit"; break; }
                    var size=fields.Sum(s=>s?.Length??0)+query.Length+32;
                    if(characters+size>MaximumCharacters) { reason="response-character-budget"; break; }
                    characters+=size;
                    rows.Add(new PlcHardwareCatalogCandidate { Keyword=query,ArticleNumber=entry.ArticleNumber,CatalogPath=entry.CatalogPath,Description=entry.Description,TypeIdentifier=entry.TypeIdentifier,TypeIdentifierNormalized=entry.TypeIdentifierNormalized,TypeName=entry.TypeName,Version=entry.Version });
                }
            }
            return new PlcHardwareCatalogSearchResult { Keyword=query,Count=rows.Count,Items=rows.ToArray(),Meta=new Dictionary<string,object> {
                ["releaseKey"]=release,["scope"]="installed-catalog-single-literal-query",["queryCount"]=1,["visitedEntries"]=visited,["resultLimit"]=limit,["maximumEntries"]=MaximumEntries,["maximumResponseCharacters"]=MaximumCharacters,
                ["truncated"]=reason!=null,["truncationReason"]=reason??"none",["complete"]=reason==null,["order"]="native-enumeration-first-seen",["insertionCompatibilityChecked"]=false,["nativeAcceptance"]="NOT RUN",
                ["boundsScope"]="Client enumeration and response only; the documented synchronous Find API has no server-side result limit or cancellation parameter."
            }};
        }
    }
}
