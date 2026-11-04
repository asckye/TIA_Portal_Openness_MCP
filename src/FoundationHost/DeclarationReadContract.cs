using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

// Additional foundation tools, not entries in the pinned V17 PLC profile.
// Preserve their existing PascalCase array contract and native declaration text.
internal static class DeclarationReadContract
{
    internal static void ValidateArguments(JsonObject arguments)
    {
        bool scoped=(bool?)arguments["includeUnits"]==true || !string.IsNullOrEmpty((string?)arguments["unitName"]);
        if((bool?)arguments["includeUnits"]==true && !string.IsNullOrEmpty((string?)arguments["unitName"]))
            throw new ArgumentException("unitName and includeUnits cannot be combined.");
        foreach(var key in scoped ? new[]{"plc"} : new[]{"plc","table"})
            if(string.IsNullOrWhiteSpace(arguments[key]?.GetValue<string>()))
                throw new ArgumentException("An exact nonempty "+key+" path is required.");
    }
    internal static JsonArray Validate(string tool,string table,JsonNode? result,string unitName="",bool includeUnits=false)
    {
        string kind=tool switch {
            "ListPlcTags"=>"tag", "ListPlcUserConstants"=>"user-constant", "ListPlcSystemConstants"=>"system-constant", _=>
                throw new InvalidDataException("Unknown declaration reader.")
        };
        if(result is not JsonArray rows) throw new InvalidDataException("Worker returned an invalid declaration list for "+tool);
        var paths=new HashSet<string>(StringComparer.Ordinal);
        // The table may be the exact encoded path or (P6-55) a unique raw single-segment name, which the worker
        // answers with the canonical encoded path; every row must then share that one prefix.
        string? prefix=null;
        foreach(var node in rows)
        {
            if(node is not JsonObject row || row.Count!=5)
                throw new InvalidDataException("Worker returned an invalid declaration for "+tool);
            foreach(var field in new[]{"Name","Path","Kind","DataType","Value"})
                if(row[field] is not JsonValue value || !value.TryGetValue<string>(out _))
                    throw new InvalidDataException("Worker returned an invalid declaration field for "+tool);
            string name=row["Name"]!.GetValue<string>(), path=row["Path"]!.GetValue<string>();
            string leaf="/"+Uri.EscapeDataString(name);
            string? rowPrefix=path.EndsWith(leaf,StringComparison.Ordinal) ? path.Substring(0,path.Length-leaf.Length) : null;
            string? relative=rowPrefix;
            if(rowPrefix?.StartsWith("units/",StringComparison.Ordinal)==true && (includeUnits || unitName!=""))
            {
                var parts=rowPrefix.Split('/');
                if(parts.Length<4 || parts[2]!="tag-tables" || string.IsNullOrWhiteSpace(Uri.UnescapeDataString(parts[1]))
                    || Uri.EscapeDataString(Uri.UnescapeDataString(parts[1]))!=parts[1] || unitName!="" && parts[1]!=Uri.EscapeDataString(unitName))
                    throw new InvalidDataException("Worker returned conflicting unit declaration identity.");
                relative=string.Join("/",parts.Skip(3));
            }
            else if(includeUnits && rowPrefix?.StartsWith("root/tag-tables/",StringComparison.Ordinal)==true) relative=rowPrefix
                .Substring("root/tag-tables/".Length);
            else if(includeUnits) relative=null;
            else if(unitName!="") relative=null;
            bool scoped=includeUnits || unitName!="";
            bool canonical=relative!=null && relative.Split('/')
                .All(segment=>!string.IsNullOrWhiteSpace(Uri.UnescapeDataString(segment)) && Uri.UnescapeDataString(segment) is not ("." or "..") && Uri
                    .EscapeDataString(Uri.UnescapeDataString(segment))==segment);
            bool knownPrefix=canonical && (relative==table || (!table.Contains('/') && relative==Uri.EscapeDataString(table)) || scoped && table=="");
            if(string.IsNullOrWhiteSpace(name) || row["Kind"]!.GetValue<string>()!=kind || !knownPrefix ||
                (!scoped && prefix!=null && rowPrefix!=prefix) || !paths.Add(path))
                throw new InvalidDataException("Worker returned conflicting declaration identity for "+tool);
            prefix=rowPrefix;
        }
        // Empty addresses, data types and constant text remain exactly as returned.
        // This is engineering metadata, never a live PLC value or expression evaluation.
        return (JsonArray)rows.DeepClone();
    }
}
