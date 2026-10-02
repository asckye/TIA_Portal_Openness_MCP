using System.Text.Json.Nodes;

namespace TiaMcp.LegacyHost;

// Additional foundation tools, not entries in the pinned V17 PLC profile.
// Preserve their existing PascalCase array contract and native declaration text.
internal static class DeclarationReadContract
{
    internal static void ValidateArguments(JsonObject arguments)
    {
        foreach(var key in new[]{"plc","table"})
            if(string.IsNullOrWhiteSpace(arguments[key]?.GetValue<string>()))
                throw new ArgumentException("An exact nonempty "+key+" path is required.");
    }
    internal static JsonArray Validate(string tool,string table,JsonNode? result)
    {
        string kind=tool switch { "ReadPlcTags"=>"tag", "ReadPlcUserConstants"=>"user-constant", "ReadPlcSystemConstants"=>"system-constant", _=>throw new InvalidDataException("Unknown declaration reader.") };
        if(result is not JsonArray rows) throw new InvalidDataException("Worker returned an invalid declaration list for "+tool);
        var paths=new HashSet<string>(StringComparer.Ordinal);
        foreach(var node in rows)
        {
            if(node is not JsonObject row || row.Count!=5)
                throw new InvalidDataException("Worker returned an invalid declaration for "+tool);
            foreach(var field in new[]{"Name","Path","Kind","DataType","Value"})
                if(row[field] is not JsonValue value || !value.TryGetValue<string>(out _))
                    throw new InvalidDataException("Worker returned an invalid declaration field for "+tool);
            string name=row["Name"]!.GetValue<string>(), path=row["Path"]!.GetValue<string>();
            if(string.IsNullOrWhiteSpace(name) || row["Kind"]!.GetValue<string>()!=kind ||
                path!=table+"/"+Uri.EscapeDataString(name) || !paths.Add(path))
                throw new InvalidDataException("Worker returned conflicting declaration identity for "+tool);
        }
        // Empty addresses, data types and constant text remain exactly as returned.
        // This is engineering metadata, never a live PLC value or expression evaluation.
        return (JsonArray)rows.DeepClone();
    }
}
