using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcpServer.Siemens
{
    internal static class UnifiedTagDefinitions
    {
        private static readonly string[] Fields = { "Name", "TagTableName", "TagType", "Connection", "PlcName", "PlcTag", "Address", "DataType", "HmiDataType", "InitialValue", "Persistent", "AcquisitionMode", "AcquisitionCycle", "AccessMode", "Scope", "MaxLength", "DBNameMultiplexing" };
        internal static IEnumerable<JsonObject> Read(object hmi)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in MigrationRead.Property(hmi, "Connections", "/Connections", (v, p) => MigrationRead.Graph(v, p))) yield return row;
            foreach (var row in Groups(hmi, "", true, seen, new HashSet<object>())) yield return row;
            foreach (var row in MigrationRead.Property(hmi, "Tags", "/Tags", (v, p) => MigrationRead.Collection(v!, p, (tag, index) => Root(tag, index, null, seen)))) yield return row;
        }
        private static IEnumerable<JsonObject> Groups(object group, string path, bool root, HashSet<string> seen, HashSet<object> ancestors)
        {
            if (!ancestors.Add(group) || ancestors.Count > 128) { yield return MigrationRead.Failure(path, "GroupCycleOrDepthLimit"); yield break; }
            try
            {
                foreach (var row in MigrationRead.Property(group, "TagTables", path + "/TagTables", (v, p) => MigrationRead.Collection(v!, p, (table, index) => Table(table, path, seen)))) yield return row;
                foreach (var row in MigrationRead.Property(group, root ? "TagTableGroups" : "Groups", path + "/Groups", (v, p) => MigrationRead.Collection(v!, p,
                    (child, index) => Groups(child, path + "/Groups/" + MigrationRead.Segment(MigrationRead.Get(child, "Name")!.ToString()), false, seen, ancestors)))) yield return row;
            }
            finally { ancestors.Remove(group); }
        }
        private static IEnumerable<JsonObject> Table(object table, string group, HashSet<string> seen)
        {
            var path = group + "/TagTables/" + MigrationRead.Segment(MigrationRead.Get(table, "Name")!.ToString());
            yield return new JsonObject { ["path"] = path, ["kind"] = "tagTable", ["status"] = "ok", ["groupPath"] = group, ["name"] = MigrationRead.Get(table, "Name")!.ToString() };
            foreach (var row in MigrationRead.Property(table, "Tags", path + "/Tags", (v, p) => MigrationRead.Collection(v!, p, (tag, index) => Root(tag, index, path, seen)))) yield return row;
        }
        private static IEnumerable<JsonObject> Root(object tag, string evidence, string? table, HashSet<string> seen)
        {
            var name = MigrationRead.Get(tag, "Name")?.ToString() ?? throw new InvalidOperationException("Tag name missing.");
            var path = "/Tags/" + MigrationRead.Segment(name);
            if (!seen.Add(name)) { yield return new JsonObject { ["path"] = evidence, ["kind"] = "tagAlias", ["status"] = "ok", ["canonicalPath"] = path, ["tablePath"] = table }; yield break; }
            foreach (var row in Tag(tag, path, path, table, evidence, null, new HashSet<object>())) yield return row;
        }
        private static IEnumerable<JsonObject> Tag(object tag, string path, string rootPath, string? table, string evidence, JsonObject? inherited, HashSet<object> ancestors)
        {
            if (ancestors.Count >= 128 || !ancestors.Add(tag)) { yield return MigrationRead.Failure(path, "MemberCycleOrDepthLimit"); yield break; }
            try
            {
                var own = new JsonObject();
                yield return new JsonObject { ["path"] = path, ["kind"] = "tag", ["status"] = "ok", ["rootPath"] = rootPath,
                    ["tablePath"] = table, ["groupPath"] = table == null ? null : table.Substring(0, table.LastIndexOf("/TagTables/", StringComparison.Ordinal)),
                    ["type"] = tag.GetType().FullName, ["evidence"] = evidence };
                foreach (var field in Fields)
                {
                    object? value = null; JsonObject? failure = null;
                    try { value = MigrationRead.Get(tag, field); }
                    catch (Exception ex) { failure = MigrationRead.Failure(path + "/" + field, MigrationRead.Cause(ex) is NotSupportedException ? "Unsupported" : "ReadFailed", ex); }
                    if (failure != null) { yield return failure; continue; }
                    if (MigrationRead.IsScalar(value))
                    {
                        var row = MigrationRead.Scalar(path + "/" + field, value, "tagField"); row["origin"] = "ownApiValue";
                        own[field] = row["value"]?.DeepClone(); yield return row;
                    }
                    else foreach (var row in MigrationRead.Graph(value, path + "/" + field)) yield return row;
                }
                var source = new JsonObject { ["path"] = path + "/$source", ["kind"] = "tagSource", ["status"] = "ok", ["own"] = own.DeepClone() };
                string conn = own["Connection"]?.ToString() ?? "";
                bool hasPlc = !string.IsNullOrWhiteSpace(own["PlcName"]?.ToString()) || !string.IsNullOrWhiteSpace(own["PlcTag"]?.ToString());
                bool isInternal = own.ContainsKey("Connection") && own.ContainsKey("PlcName") && own.ContainsKey("PlcTag") && string.IsNullOrEmpty(conn) && !hasPlc;
                string origin = hasPlc ? "PLC" : isInternal ? "internal" : "unresolvedConnection";
                // Empty member fields do not prove internal storage. Root-origin
                // inheritance is a separately labelled inference, never a replacement.
                if (inherited != null && !hasPlc && string.IsNullOrEmpty(conn))
                { source["origin"] = inherited["origin"]?.DeepClone(); source["originBasis"] = "inferredFromRoot"; source["inheritedFrom"] = rootPath; source["rootSourceEvidence"] = inherited.DeepClone(); }
                else { source["origin"] = origin; source["originBasis"] = "ownApiValues"; }
                if (source["origin"]?.ToString() == "unresolvedConnection") { source["status"] = "unsupported"; source["reason"] = "Connection is not proof of a PLC source; inspect the referenced connection. No PLC symbol or address has been fabricated."; }
                yield return source;
                if ((own["TagType"]?.ToString() ?? "").IndexOf("Array", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var bounds = Bounds(own["DataType"]?.ToString() ?? ""); bounds["path"] = path + "/$array"; yield return bounds;
                }
                var rootEvidence = inherited ?? source;
                foreach (var row in MigrationRead.Property(tag, "Members", path + "/Members", (v, p) => MigrationRead.Collection(v!, p,
                    (child, index) => Tag(child, p + "/" + MigrationRead.Segment(MigrationRead.Get(child, "Name")!.ToString()), rootPath, table, index, rootEvidence, ancestors)))) yield return row;
            }
            finally { ancestors.Remove(tag); }
        }
        internal static JsonObject Bounds(string dataType)
        {
            var result = new JsonObject { ["kind"] = "arrayDefinition", ["rawDataType"] = dataType, ["status"] = "unsupported",
                ["reason"] = "This API does not expose explicit array bounds. All actual Members are still visited; bounds are not inferred from element count.", ["dimensions"] = null };
            var match = Regex.Match(dataType, @"^\s*Array\s*\[([^\]]+)\]\s+of\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return result;
            var dims = new JsonArray();
            foreach (var segment in match.Groups[1].Value.Split(','))
            {
                var range = Regex.Match(segment, @"^\s*(-?\d+)\s*\.\.\s*(-?\d+)\s*$");
                if (!range.Success || !long.TryParse(range.Groups[1].Value, out long lower) || !long.TryParse(range.Groups[2].Value, out long upper) || upper < lower) return result;
                dims.Add(new JsonObject { ["lower"] = lower, ["upper"] = upper });
            }
            result["status"] = "ok"; result["reason"] = null; result["dimensions"] = dims; result["basis"] = "parsedFromOwnDataTypeText"; return result;
        }
    }
}
