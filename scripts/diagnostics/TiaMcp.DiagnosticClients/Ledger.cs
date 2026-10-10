using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;

namespace TiaMcp.DiagnosticClients;

public static class Ledger
{
    public static string Render(string root)
    {
        var directory = Path.Combine(root, "scripts/diagnostics/campaign");
        var history = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "ledger-history.json")))!;
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest/tools-list.json")).TrimStart('\uFEFF'))!;
        var tools = catalog is JsonArray list ? list : catalog["tools"]!.AsArray();
        var renames = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "historical_tool_names.json")))!;
        var previous = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "vm_ledger.json")))!["invoked"]!.AsObject();
        var runs = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        void Read(TextReader reader)
        {
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var row = JsonNode.Parse(line)!.AsObject(); var name = (string)row["tool"]!;
                name = (string?)renames[name] ?? name;
                if (!runs.TryGetValue(name, out var rows)) runs[name] = rows = [];
                rows.Add(row);
            }
        }
        var packed = Path.Combine(directory, "ledger-runs.jsonl.gz");
        if (File.Exists(packed)) { using var gzip = new GZipStream(File.OpenRead(packed), CompressionMode.Decompress); using var reader = new StreamReader(gzip, Encoding.UTF8); Read(reader); }
        if (Directory.Exists(Path.Combine(directory, "ledger"))) foreach (var file in Directory.GetFiles(Path.Combine(directory, "ledger"), "*.jsonl").Order(PythonStringComparer.Instance)) { using var reader = File.OpenText(file); Read(reader); }
        (string Status, string Note) Status(string name)
        {
            if (history["overrides"]?[name] is JsonArray row) return ((string)row[0]!, (string)row[1]!);
            if (runs.TryGetValue(name, out var rows)) return rows.Any(r => (bool?)r["ok"] == true) ? ("✅ 通过", "") : ("⚠ 参数/前置条件", Campaign.Trim((string?)rows[^1]["text"] ?? "", 90));
            return previous.ContainsKey(name) ? ("✅ 早期真机", "2.7.39–2.7.45 会话（DCC / Startdrive / SafetyValidation / Test Suite / Teamcenter / CFC / Unified 读取）") : ("❌ 未跑", "");
        }
        var counts = tools.Select(t => Status((string)t!["name"]!).Status.Split('（')[0]).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        var lines = history["intro"]!.AsArray().Select(s => (string)s!).ToList(); lines.Add("|---|---|---:|");
        foreach (var row in history["meanings"]!.AsArray()) { var status = (string)row![0]!; lines.Add($"| {status} | {row[1]} | {counts.GetValueOrDefault(status)} |"); }
        lines.Add(""); lines.Add((string)history["crashes"]!); lines.Add("");
        foreach (var group in tools.GroupBy(t => (string)t!["domain"]!).OrderBy(g => g.Key, PythonStringComparer.Instance))
        {
            lines.Add($"## {group.Key}（{group.Count()}）"); lines.Add(""); lines.Add("| 工具 | 状态 | 说明 |"); lines.Add("|---|---|---|");
            foreach (var tool in group.OrderBy(t => (string)t!["name"]!, PythonStringComparer.Instance))
            {
                var name = (string)tool!["name"]!; var (status, note) = Status(name);
                lines.Add($"| `{name}` | {status} | V3 historical evidence; V4 native acceptance NOT RUN. {note.Replace('|', '/')} |");
            }
            lines.Add("");
        }
        return string.Join('\n', lines);
    }
    public static void Write(string root, string output, bool check)
    {
        var bytes = new UTF8Encoding(false).GetBytes(Render(root));
        // This renderer owns the historical tool table. The current L5/capability ledger
        // is a different generator's input and must never be overwritten by old evidence.
        if (Path.GetFullPath(output) == Path.GetFullPath(Path.Combine(root, "docs/reference/real-machine-ledger.md")) && File.ReadAllText(output).Contains("<!-- behavior-capabilities:start -->", StringComparison.Ordinal))
            throw new ArgumentException("Current ledger contains L5/capability policy; use --output for the historical table and preserve Generate-Phase6Plan's block");
        if (check)
        {
            if (!File.Exists(output) || !File.ReadAllBytes(output).SequenceEqual(bytes)) throw new ArgumentException("Ledger bytes differ (including BOM or line endings)");
        }
        else File.WriteAllBytes(output, bytes);
    }
}
