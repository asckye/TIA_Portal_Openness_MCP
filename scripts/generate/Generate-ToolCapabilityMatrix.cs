#!/usr/bin/env dotnet
// Generate the capability matrix from manifest/tools-list.json.
// Usage: dotnet run scripts/generate/Generate-ToolCapabilityMatrix.cs -- [--tools-list path] [--out-file path] [--check]
// `--check` verifies the generated UTF-8 bytes without changing the output.
// The JSON is parsed with JsonNode so this file app does not need reflection-based serialization.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var options = args.ToList();
var check = options.Remove("--check");
var root = FindRoot(AppContext.BaseDirectory, Directory.GetCurrentDirectory());
var toolsPath = Option(options, "--tools-list") ?? Path.Combine(root, "manifest", "tools-list.json");
var outputPath = Path.GetFullPath(Option(options, "--out-file") ?? Path.Combine(root, "docs", "reference", "tool-matrix.md"));
if (options.Count != 0) throw new ArgumentException("Unexpected argument: " + options[0]);

var data = JsonNode.Parse(File.ReadAllText(toolsPath, Encoding.UTF8))!.AsObject();
var tools = data["tools"]!.AsArray().Select(x => x!.AsObject()).ToArray();
if (tools.Length != (int)data["toolCount"]!)
    throw new InvalidDataException($"tools-list.json toolCount ({data["toolCount"]}) differs from the tools array ({tools.Length})");
var categories = data["categories"]?.AsArray() ?? throw new InvalidDataException("tools-list.json has no categories block; regenerate it with the current C# tool-list generator");

var operationMeaning = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["SESSION"] = "会话与发现，不改工程",
    ["READ"] = "读取已打开工程，不改动",
    ["WRITE"] = "修改离线工程数据，默认预览，不自动保存/编译/下载",
    ["FILE"] = "导出/导入文件或生成离线产物",
    ["OFFLINE"] = "纯离线计算，不需要 TIA 会话",
    ["ONLINE"] = "联系 PLC/设备/运行时，只读",
    ["ONLINE-WRITE"] = "改变真实设备或运行时",
    ["EXECUTE"] = "执行编译/测试/自检"
};

var sb = new StringBuilder();
void Line(string value = "") => sb.Append(value).Append(Environment.NewLine);
string Esc(string? value) => (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();
string StripPrefix(string? value) => Regex.Replace(value ?? "", @"^\s*\[L\d\]\[(?:Category:)?[^\]]+\](?:\[[A-Za-z-]+\])?\s*", "");
int CountTools(Func<JsonObject, bool> predicate) => tools.Count(predicate);
string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
bool Bool(JsonObject value, string key) => value[key]?.GetValue<bool>() ?? false;

Line("# MCP 工具能力矩阵");
Line();
Line("[文档目录](../README.md) · [能力与验收边界](capabilities.md) · [官方 API 覆盖清单](openness-coverage.md)");
Line();
Line("本文件由 `scripts/generate/Generate-ToolCapabilityMatrix.cs` 从 `manifest/tools-list.json`（已编译 EXE 的反射清单）生成，分类来自引擎内的 `ToolTaxonomy`；运行时以 `tools/list` 为准。在会话中调用 `ListToolCategories` 可得到同一分类的实时计数，`FindTools(category=…)` / `FindTools(domain=…)` 可按分类检索。");
Line();
Line("- 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
Line("- 引擎文件版本：" + Text(data, "fileVersion"));
Line("- 工具数量：" + tools.Length.ToString(CultureInfo.InvariantCulture));
Line();
Line("## 读法");
Line();
Line("每个工具的描述以 `[层][域][操作]` 开头。层：L0 会话与引导、L1 常用工程动作、L2 专用深度工具（默认 lite 配置不全部列出，经 `FindTools` + `CallTool` 调用）。操作类型：");
Line();
Line("| 操作 | 含义 | 工具数 |");
Line("|---|---|---:|");
foreach (var (op, meaning) in operationMeaning) Line($"| `{op}` | {meaning} | {CountTools(t => Text(t, "operation") == op)} |");
var inferred = CountTools(t => Bool(t, "operationInferred"));
Line();
Line($"描述里未显式标注操作类型的工具（{inferred} 个）由 `ToolTaxonomy.OperationOf` 按工具名推断，表中以 `*` 标记；显式标注优先。");
Line();
Line("## 分类总览");
Line();
Line("| 大类 | 名称 | 工具数 | 域 |");
Line("|---|---|---:|---|");
foreach (var categoryNode in categories)
{
    var category = categoryNode!.AsObject();
    var key = Text(category, "key");
    var count = CountTools(t => Text(t, "category") == key);
    var domains = category["domains"]!.AsArray().Select(d =>
    {
        var domain = d!.GetValue<string>();
        return $"`{domain}` ({CountTools(t => Text(t, "domain") == domain)})";
    });
    Line($"| `{key}` | {Text(category, "nameZh")} / {Text(category, "nameEn")} | {count} | {string.Join('、', domains)} |");
}
foreach (var categoryNode in categories)
{
    var category = categoryNode!.AsObject();
    var key = Text(category, "key");
    var categoryTools = tools.Where(t => Text(t, "category") == key).ToArray();
    Line();
    Line($"## {key} — {Text(category, "nameZh")} / {Text(category, "nameEn")}（{categoryTools.Length}）");
    Line();
    Line(Esc(Text(category, "description")));
    foreach (var domainNode in category["domains"]!.AsArray())
    {
        var domain = domainNode!.GetValue<string>();
        var inDomain = categoryTools.Where(t => Text(t, "domain") == domain)
            .OrderBy(t => Text(t, "layer"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => Text(t, "name"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => Text(t, "name"), StringComparer.Ordinal).ToArray();
        if (inDomain.Length == 0) continue;
        Line();
        Line($"### [{domain}]（{inDomain.Length}）");
        Line();
        Line("| 工具 | 层 | 操作 | 说明 |");
        Line("|---|---|---|---|");
        foreach (var tool in inDomain)
        {
            var op = Text(tool, "operation") + (Bool(tool, "operationInferred") ? "*" : "");
            Line($"| `{Text(tool, "name")}` | {Text(tool, "layer")} | {op} | {Esc(StripPrefix(Text(tool, "description")))} |");
        }
    }
}
var uncategorized = tools.Where(t => Text(t, "category") == "uncategorized").Select(t => Text(t, "name")).ToArray();
if (uncategorized.Length != 0) throw new InvalidDataException("Uncategorized tools present: " + string.Join(", ", uncategorized));

var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
if (check)
{
    var expected = Encoding.UTF8.GetString(bytes);
    var actual = File.Exists(outputPath) ? File.ReadAllText(outputPath, Encoding.UTF8) : "";
    // The timestamp is deliberately generated at write time. Compare all stable content so CI can
    // detect drift without requiring a commit to be regenerated at the same second.
    expected = Regex.Replace(expected, @"(?m)^- 生成时间：.*$", "- 生成时间：<generated>");
    actual = Regex.Replace(actual, @"(?m)^- 生成时间：.*$", "- 生成时间：<generated>");
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
        throw new InvalidDataException("Generated tool matrix differs from " + outputPath);
    Console.WriteLine($"tool-matrix.md: {tools.Length} tools in {categories.Count} categories; bytes match");
}
else
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var temporary = Path.Combine(Path.GetDirectoryName(outputPath)!, ".tool-matrix-" + Guid.NewGuid().ToString("N") + ".tmp");
    try
    {
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, outputPath, true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
    Console.WriteLine($"tool-matrix.md: {tools.Length} tools in {categories.Count} categories -> {outputPath}");
}

static string? Option(List<string> values, string name)
{
    var index = values.IndexOf(name);
    if (index < 0) return null;
    if (index + 1 >= values.Count) throw new ArgumentException("Missing value for " + name);
    var value = values[index + 1];
    values.RemoveAt(index + 1);
    values.RemoveAt(index);
    return value;
}

static string FindRoot(params string[] starts)
{
    foreach (var start in starts)
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.Offline.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run this helper from within the repository.");
}
