using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcp.Logic.Generation
{
    // CSV is a bounded, lossless view of the canonical machine document, not another generation model.
    public static class MachineCsv
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(true, true);
        private static readonly string[] Sheets = { "Machine", "Stations", "Units", "Devices", "Lists" };
        private const int MaximumBytes = 16 * 1024 * 1024, MaximumRows = 10001, MaximumColumns = 1024;

        public static string EmptyMachine(StandardPackage package)
        {
            var languages = package.Manifest.Languages.Required.Concat(new[] { package.Manifest.Languages.Default }).Distinct(StringComparer.Ordinal).ToArray();
            JsonObject Names(string text) { var names = new JsonObject(); foreach (var language in languages) names[language] = text; return names; }
            return new JsonObject {
            ["schema"] = "tiamcp.machine/1", ["machine"] = new JsonObject { ["id"] = "M1", ["name"] = Names("Machine") },
            ["standard"] = new JsonObject { ["package"] = package.Manifest.Id, ["version"] = package.Manifest.Version },
            ["target"] = new JsonObject { ["release"] = package.Manifest.Targets.Releases.Last(), ["project"] = new JsonObject { ["mode"] = "new", ["name"] = "Machine", ["directory"] = "C:/Projects" } },
            ["stations"] = new JsonArray(new JsonObject { ["id"] = "PLC1", ["role"] = "plc.main" }),
            ["topology"] = new JsonArray(new JsonObject { ["id"] = "U01", ["kind"] = "unit", ["name"] = Names("Unit") }),
            ["devices"] = new JsonArray(), ["options"] = new JsonObject { ["languages"] = new JsonArray(languages.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()) }
            }.ToJsonString();
        }

        public static IDictionary<string, byte[]> Export(StandardPackage package, string json)
        {
            package.ValidateMachine(json);
            var machine = JsonNode.Parse(json)!.AsObject();
            var rules = (package.Manifest.Parts.Rules ?? new List<string>()).Select(p => JsonNode.Parse(package.Documents[p].GetRawText())!).ToArray();
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var machineRow = new Dictionary<string, string>(StringComparer.Ordinal);
            Fields(machine["machine"]!, machineRow); machineRow["package"] = (string)machine["standard"]!["package"]!;
            machineRow["version"] = (string)machine["standard"]!["version"]!; machineRow["release"] = (string)machine["target"]!["release"]!;
            Fields(machine["target"]!["project"]!, machineRow, "Project:");
            machineRow["languages"] = machine["options"]!["languages"]!.ToJsonString(); machineRow["packageHash"] = package.ContentHash;
            Sheet("Machine", new[] { machineRow }, new[] { "id", "package", "version", "release", "Project:mode", "Project:name", "Project:directory", "Project:projectIdentity", "Project:softwarePath", "languages", "packageHash" });
            Sheet("Stations", machine["stations"]!.AsArray().Select(n => Row(n!)), new[] { "id", "role", "article", "firmware", "ip", "profinetName", "kind" });
            var units = new List<Dictionary<string, string>>();
            Flatten(machine["topology"]!.AsArray(), "", 0);
            Sheet("Units", units, new[] { "id", "kind", "parent", "childrenPresent" });
            var devices = machine["devices"]!.AsArray().Select(n => {
                var row = Row(n!, "params", "io", "alarms", "hmi");
                foreach (var field in new[] { "params", "io", "alarms", "hmi" })
                {
                    row[field + "Present"] = n!.AsObject().ContainsKey(field) ? "true" : "false";
                    if (n[field] is JsonObject obj) foreach (var property in obj)
                        row[Prefix(field) + property.Key] = field is "params" or "alarms" ? property.Value!.ToJsonString() : (string)property.Value!;
                }
                return row;
            }).ToArray();
            var dynamic = rules.SelectMany(r => r["params"]!["properties"]!.AsObject().Select(p => "Param:" + p.Key)
                .Concat(r["signals"]!.AsArray().Select(s => "IO:" + (string)s!["role"]!))).Distinct(StringComparer.Ordinal);
            Sheet("Devices", devices, new[] { "id", "type", "parent", "station", "note", "Hmi:screen", "paramsPresent", "ioPresent", "alarmsPresent", "hmiPresent" }
                .Concat(machine["options"]!["languages"]!.AsArray().Select(l => "Name:" + (string)l!)).Concat(dynamic));
            var lists = rules.Select(r => new Dictionary<string, string> { ["deviceType"] = (string)r["deviceType"]!, ["paramsSchema"] = r["params"]!.ToJsonString(), ["signals"] = r["signals"]!.ToJsonString() });
            Sheet("Lists", lists, new[] { "deviceType", "paramsSchema", "signals" });
            if (files.Values.Sum(b => (long)b.Length) > MaximumBytes) throw new ArgumentException("CSV output exceeds 16 MiB.");
            return files;

            void Flatten(JsonArray nodes, string parent, int depth)
            {
                if (depth > 32) throw new ArgumentException("Topology nesting exceeds 32.");
                foreach (var node in nodes)
                {
                    var row = Row(node!, "children"); row["parent"] = parent;
                    row["childrenPresent"] = node!.AsObject().ContainsKey("children") ? "true" : "false"; units.Add(row);
                    if (node["children"] is JsonArray children) Flatten(children, (string)node["id"]!, depth + 1);
                }
            }
            void Sheet(string name, IEnumerable<Dictionary<string, string>> values, IEnumerable<string> fixedColumns)
            {
                var rows = values.ToArray();
                var columns = fixedColumns.Concat(rows.SelectMany(r => r.Keys)).Distinct(StringComparer.Ordinal).ToArray();
                if (rows.Length > MaximumRows - 1 || columns.Length > MaximumColumns) throw new ArgumentException("CSV table exceeds row/column limits.");
                var text = new StringBuilder(); Line(columns);
                foreach (var row in rows) Line(columns.Select(c => row.TryGetValue(c, out var value) ? value : ""));
                var bytes = Utf8.GetPreamble().Concat(Utf8.GetBytes(text.ToString())).ToArray(); files.Add(name + ".csv", bytes);
                if (bytes.Length > 4 * 1024 * 1024) throw new ArgumentException("CSV table exceeds 4 MiB.");
                void Line(IEnumerable<string> cells)
                {
                    var values = cells.ToArray();
                    if (values.Any(c => c.Length > 65536)) throw new ArgumentException("CSV cell exceeds 65536 characters.");
                    text.Append(string.Join(",", values.Select(c => "\"" + c.Replace("\"", "\"\"") + "\""))).Append('\n');
                }
            }
        }

        public static string Import(StandardPackage package, string directory)
        {
            directory = StandardPackageStore.Absolute(directory, "inputPath"); StandardPackageStore.Safe(directory);
            if (!Directory.Exists(directory)) throw new ArgumentException("CSV input must be a package-generated directory.", "inputPath");
            var entries = Directory.EnumerateFileSystemEntries(directory).Take(Sheets.Length + 1).ToArray();
            if (entries.Length != Sheets.Length || Sheets.Any(s => !entries.Contains(Path.Combine(directory, s + ".csv"), StringComparer.Ordinal)))
                throw new ArgumentException("CSV input must contain exactly Machine, Stations, Units, Devices and Lists.csv.", "inputPath");
            long total = 0;
            var sheets = new Dictionary<string, Dictionary<string, string>[]>(StringComparer.Ordinal);
            var headers = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var sheet in Sheets)
            {
                string path = Path.Combine(directory, sheet + ".csv"); StandardPackageStore.Safe(path);
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                total += input.Length;
                if (input.Length > 4 * 1024 * 1024 || total > MaximumBytes) throw new ArgumentException("CSV size limit exceeded.", "inputPath");
                using var output = new MemoryStream(); input.CopyTo(output); var bytes = output.ToArray();
                int offset = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
                var rows = Parse(Utf8.GetString(bytes, offset, bytes.Length - offset));
                if (rows.Count == 0 || rows[0].Any(c => c.Length == 0) || rows[0].Distinct(StringComparer.Ordinal).Count() != rows[0].Length)
                    throw new ArgumentException("CSV headers must be nonempty and unique.", "inputPath");
                var columns = rows[0];
                headers[sheet] = columns;
                sheets[sheet] = rows.Skip(1).Select(row => row.Length != columns.Length ? throw new ArgumentException("CSV row width differs from its header.", "inputPath")
                    : columns.Select((c, i) => new { c, i }).ToDictionary(p => p.c, p => row[p.i], StringComparer.Ordinal)).ToArray();
            }
            if (sheets["Machine"].Length != 1) throw new ArgumentException("Machine.csv must contain exactly one machine.", "inputPath");
            var info = sheets["Machine"][0];
            string Cell(string key) => info.TryGetValue(key, out var value) ? value : throw new ArgumentException("Missing machine CSV field: " + key, "inputPath");
            if (Cell("packageHash") != package.ContentHash || Cell("package") != package.Manifest.Id)
                throw new ArgumentException("CSV template belongs to a different package; export a new template.", "inputPath");
            var identity = Object(info, new[] { "id" }, "Name:");
            var project = new JsonObject();
            foreach (var column in info.Where(c => c.Key.StartsWith("Project:", StringComparison.Ordinal) && c.Value.Length != 0)) project[column.Key.Substring(8)] = column.Value;
            var units = sheets["Units"];
            if (units.Select(u => u["id"]).Distinct(StringComparer.Ordinal).Count() != units.Length) throw new ArgumentException("Duplicate topology id.", "inputPath");
            var nodes = units.ToDictionary(u => u["id"], u => Object(u, new[] { "id", "kind" }, "Name:"), StringComparer.Ordinal);
            var topology = new JsonArray();
            foreach (var row in units)
            {
                var node = nodes[row["id"]];
                if (Present(row, "childrenPresent")) node["children"] = new JsonArray();
            }
            foreach (var row in units)
            {
                string parent = row["parent"];
                var seen = new HashSet<string>(StringComparer.Ordinal) { row["id"] };
                while (parent.Length != 0)
                {
                    if (!seen.Add(parent) || seen.Count > 32 || !nodes.ContainsKey(parent)) throw new ArgumentException("Invalid or cyclic topology parent.", "inputPath");
                    parent = units.Single(u => u["id"] == parent)["parent"];
                }
                parent = row["parent"];
                if (parent.Length == 0) topology.Add(nodes[row["id"]]);
                else
                {
                    nodes[parent]["children"] ??= new JsonArray(); nodes[parent]["children"]!.AsArray().Add(nodes[row["id"]]);
                }
            }
            var devices = new JsonArray();
            foreach (var row in sheets["Devices"])
            {
                var device = Object(row, new[] { "id", "type", "parent", "station", "note" }, "Name:");
                foreach (var field in new[] { "params", "io", "alarms", "hmi" })
                {
                    var obj = new JsonObject();
                    foreach (var cell in row.Where(c => c.Key.StartsWith(Prefix(field), StringComparison.Ordinal) && c.Value.Length != 0))
                        obj[cell.Key.Substring(Prefix(field).Length)] = field is "params" or "alarms" ? JsonNode.Parse(CanonicalJson.Parse(cell.Value).GetRawText()) : JsonValue.Create(cell.Value);
                    bool present = Present(row, field + "Present");
                    if (obj.Count > 0 || present) device[field] = obj;
                }
                devices.Add(device);
            }
            // Ignore Lists contents as executable input; its definitions always come from the selected package.
            var machine = new JsonObject { ["schema"] = "tiamcp.machine/1", ["machine"] = identity,
                ["standard"] = new JsonObject { ["package"] = Cell("package"), ["version"] = Cell("version") },
                ["target"] = new JsonObject { ["release"] = Cell("release"), ["project"] = project },
                ["stations"] = new JsonArray(sheets["Stations"].Select(row => (JsonNode)Object(row, new[] { "id", "role", "article", "firmware", "ip", "profinetName", "kind" })).ToArray()),
                ["topology"] = topology, ["devices"] = devices, ["options"] = new JsonObject { ["languages"] = JsonNode.Parse(CanonicalJson.Parse(Cell("languages")).GetRawText()) } };
            string json = machine.ToJsonString(); package.ValidateMachine(json);
            // Unknown columns are refused instead of silently losing a user's edits.
            var expected = Export(package, json);
            foreach (var sheet in Sheets)
            {
                var columns = Parse(Utf8.GetString(expected[sheet + ".csv"].Skip(3).ToArray()))[0];
                if (headers[sheet].Any(c => !columns.Contains(c, StringComparer.Ordinal))) throw new ArgumentException("Unknown CSV column in " + sheet + ".", "inputPath");
            }
            return Encoding.UTF8.GetString(CanonicalJson.Encode(CanonicalJson.Parse(json)));
        }

        private static bool Present(Dictionary<string, string> row, string field)
            => row.TryGetValue(field, out var value) && value is "true" or "false" ? value == "true"
                : throw new ArgumentException("CSV presence fields must be true or false: " + field, "inputPath");
        private static string Prefix(string field) => field == "params" ? "Param:" : field == "io" ? "IO:" : field == "alarms" ? "Alarm:" : "Hmi:";
        private static Dictionary<string, string> Row(JsonNode node, params string[] skip)
        { var row = new Dictionary<string, string>(StringComparer.Ordinal); Fields(node, row, "", skip); return row; }
        private static void Fields(JsonNode node, IDictionary<string, string> row, string prefix = "", params string[] skip)
        {
            foreach (var property in node.AsObject())
            {
                if (skip.Contains(property.Key)) continue;
                if (property.Key == "name" && property.Value is JsonObject names) foreach (var name in names) row["Name:" + name.Key] = (string)name.Value!;
                else row[prefix + property.Key] = (string)property.Value!;
            }
        }
        private static JsonObject Object(IDictionary<string, string> row, IEnumerable<string> columns, string localized = "")
        {
            var obj = new JsonObject();
            foreach (var column in columns) if (row.TryGetValue(column, out var value) && value.Length != 0) obj[column] = value;
            if (localized.Length != 0)
            {
                var name = new JsonObject(); foreach (var cell in row.Where(c => c.Key.StartsWith(localized, StringComparison.Ordinal) && c.Value.Length != 0)) name[cell.Key.Substring(localized.Length)] = cell.Value;
                obj["name"] = name;
            }
            return obj;
        }
        private static List<string[]> Parse(string text)
        {
            var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false, closed = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\0' || c == '\ufeff') throw new ArgumentException("Invalid CSV character.", "inputPath");
                if (quoted)
                {
                    if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else { quoted = false; closed = true; } }
                    else cell.Append(c);
                }
                else if (c == '"') { if (cell.Length != 0 || closed) throw new ArgumentException("Invalid CSV quoting.", "inputPath"); quoted = true; }
                else if (c == ',' || c == '\n' || c == '\r')
                {
                    row.Add(cell.ToString()); cell.Clear(); closed = false;
                    if (row.Count > MaximumColumns) throw new ArgumentException("Too many CSV columns.", "inputPath");
                    if (c != ',') { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; rows.Add(row.ToArray()); row.Clear(); }
                }
                else { if (closed) throw new ArgumentException("Unexpected text after a CSV quote.", "inputPath"); cell.Append(c); }
                if (rows.Count > MaximumRows || cell.Length > 65536) throw new ArgumentException("CSV row/cell limit exceeded.", "inputPath");
            }
            if (quoted) throw new ArgumentException("Unterminated CSV quote.", "inputPath");
            if (cell.Length > 0 || row.Count > 0 || closed) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
            return rows;
        }
    }
}
