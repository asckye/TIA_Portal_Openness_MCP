using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;

internal static class HardwareCatalogContract
{
    private static void Keys(JsonObject node,params string[] allowed)
    { if(node.Any(p=>!allowed.Contains(p.Key))) throw new InvalidDataException("Unexpected hardware catalog response field."); }
    internal static void ValidateArguments(JsonObject arguments)
    {
        string query=arguments["keyword"]!.GetValue<string>();
        int limit=arguments["limit"]!.GetValue<int>();
        if(string.IsNullOrWhiteSpace(query) || query.Length>256 || query.Any(char.IsControl) || query.All(c=>char.IsWhiteSpace(c) || c=='*' || c=='?') || limit<1 || limit>100)
            throw new ArgumentException("A substantive keyword of at most 256 characters and limit between 1 and 100 are required.");
    }
    internal static JsonObject Validate(JsonNode? result,JsonObject arguments)
    {
        ValidateArguments(arguments);
        string query=arguments["keyword"]!.GetValue<string>().Trim();
        int limit=arguments["limit"]!.GetValue<int>();
        if(result is not JsonObject root || root["Keyword"]?.GetValue<string>()!=query || root["Items"] is not JsonArray rows || root["Meta"] is not JsonObject meta || root["Count"]?.GetValue<int>()!=rows.Count || rows.Count>limit)
            throw new InvalidDataException("Invalid hardware catalog response envelope.");
        Keys(root,"Keyword","Count","Items","Meta");
        Keys(meta,"releaseKey","scope","queryCount","visitedEntries","resultLimit","maximumEntries","maximumResponseCharacters","truncated","truncationReason","complete","order","insertionCompatibilityChecked","nativeAcceptance","boundsScope");
        if(meta["scope"]?.GetValue<string>()!="installed-catalog-single-literal-query" || meta["maximumEntries"]?.GetValue<int>()!=1000 || meta["maximumResponseCharacters"]?.GetValue<int>()!=262144 || meta["order"]?.GetValue<string>()!="native-enumeration-first-seen" || meta["nativeAcceptance"]?.GetValue<string>()!="NOT RUN" || meta["boundsScope"]?.GetValue<string>()!="Client enumeration and response only; the documented synchronous Find API has no server-side result limit or cancellation parameter.")
            throw new InvalidDataException("Invalid fixed catalog bounds and scope.");
        string release=meta["releaseKey"]?.GetValue<string>()??"";
        if((release!="19" && release!="20" && release!="21") || meta["queryCount"]?.GetValue<int>()!=1 || meta["resultLimit"]?.GetValue<int>()!=limit || meta["insertionCompatibilityChecked"]?.GetValue<bool>()!=false)
            throw new InvalidDataException("Invalid hardware catalog capability evidence.");
        bool truncated=meta["truncated"]?.GetValue<bool>()??throw new InvalidDataException("Missing catalog truncation flag.");
        string reason=meta["truncationReason"]?.GetValue<string>()??"";
        if(meta["complete"]?.GetValue<bool>()!=!truncated || (!truncated && reason!="none") || (truncated && !new[]{"entry-budget","result-limit","field-character-budget","response-character-budget"}.Contains(reason)))
            throw new InvalidDataException("Conflicting hardware catalog completeness evidence.");
        int visited=meta["visitedEntries"]?.GetValue<int>()??-1;
        if(visited<rows.Count || visited>1000) throw new InvalidDataException("Invalid catalog traversal count.");
        var seen=new HashSet<string>(StringComparer.Ordinal); int chars=0;
        foreach(var item in rows)
        {
            if(item is not JsonObject row || row["Keyword"]?.GetValue<string>()!=query || row["Source"]?.GetValue<string>()!="HardwareCatalog" || row["Insertable"]!=null || row["Score"]!=null)
                throw new InvalidDataException("Invalid hardware catalog candidate evidence.");
            Keys(row,"Source","Keyword","ArticleNumber","CatalogPath","Description","TypeIdentifier","TypeIdentifierNormalized","TypeName","Version","Insertable","Score");
            var id=row["TypeIdentifier"]?.GetValue<string>();
            if(string.IsNullOrWhiteSpace(id) || !seen.Add(id)) throw new InvalidDataException("Missing or duplicate exact catalog identity.");
            foreach(var field in new[]{"ArticleNumber","CatalogPath","Description","TypeIdentifier","TypeIdentifierNormalized","TypeName","Version"})
            {
                if(row[field]==null) continue;
                var text=row[field]!.GetValue<string>();
                if(text.Length>16384) throw new InvalidDataException("Oversized catalog field.");
                chars+=text.Length;
            }
            chars+=query.Length+32;
            if(chars>262144) throw new InvalidDataException("Oversized catalog response.");
        }
        var response=(JsonObject)root.DeepClone();
        response["Message"]=truncated?"Hardware catalog search completed with explicit truncation.":"Hardware catalog search completed.";
        return response;
    }
}
