#!/usr/bin/env dotnet
// Generate the capability matrix from manifest/tools-list.json.
// Usage: dotnet run scripts/generate/Generate-ToolCapabilityMatrix.cs -- [--tools-list path] [--out-file path] [--check]
// `--check` verifies the generated UTF-8 bytes without changing the output.
// The JSON is parsed with JsonNode so this file app does not need reflection-based serialization.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

List<string> options = args.ToList();
bool check = options.Remove("--check");
string root = FindRoot(AppContext.BaseDirectory, Directory.GetCurrentDirectory());
string toolsPath = Option(options, "--tools-list") ?? Path.Combine(root, "manifest", "tools-list.json");
string outputPath = Path.GetFullPath(Option(options, "--out-file") ?? Path.Combine(root, "docs", "reference", "tool-matrix.md"));
if (options.Count != 0)
    throw new ArgumentException("Unexpected argument: " + options[0]);

JsonObject data = JsonNode.Parse(File.ReadAllText(toolsPath, Encoding.UTF8))!.AsObject();
JsonObject[] tools = data["tools"]!.AsArray().Select(x => x!.AsObject()).ToArray();
if (tools.Length != (int)data["toolCount"]!)
    throw new InvalidDataException($"tools-list.json toolCount ({data["toolCount"]}) differs from the tools array ({tools.Length})");
JsonArray categories = data["categories"]?.AsArray() ?? throw new InvalidDataException("tools-list.json has no categories block; regenerate it with the current C# tool-list generator");

var operationMeaning = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["UI"] = "改变工作台界面状态：切页、定位或预填，不改工程，不需要 TIA 会话",
    ["SESSION"] = "会话与发现，包含工程保存、另存与关闭；写操作须审批",
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
string Esc(string? value) => (value ?? "").Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
string StripPrefix(string? value) => MatrixPatterns.Prefix().Replace(value ?? "", "");
int CountTools(Func<JsonObject, bool> predicate) => tools.Count(predicate);
string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
bool Bool(JsonObject value, string key) => value[key]?.GetValue<bool>() ?? false;

Line("# MCP 工具能力矩阵");
Line();
Line("[文档目录](../README.md) · [能力与验收边界](capabilities.md) · [官方 API 覆盖清单](openness-coverage.md)");
Line();
Line("本文件由 `scripts/generate/Generate-ToolCapabilityMatrix.cs` 从 `manifest/tools-list.json`（FoundationHost 的 V21 full 产品目录）生成，分类来自主机共用的 `ToolTaxonomy`；运行时以 `tools/list` 为准。目录包含 Foundation 共享工具与引擎专用工具，不包含已撤回的生命周期工具。`FindTools(category=…)` / `FindTools(domain=…)` 可按分类检索。");
Line();
Line("- 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
Line("- 产品文件版本：" + Text(data, "fileVersion"));
Line("- 工具数量：" + tools.Length.ToString(CultureInfo.InvariantCulture));
Line();
Line("## 读法");
Line();
Line("每个工具的描述以 `[层][域][操作]` 开头。层：L0 会话与引导、L1 常用工程动作、L2 专用深度工具（默认 lite 配置不全部列出，经 `FindTools` + `CallTool` 调用）。操作类型：");
Line();
Line("| 操作 | 含义 | 工具数 |");
Line("|---|---|---:|");
foreach ((string op, string meaning) in operationMeaning)
    Line($"| `{op}` | {meaning} | {CountTools(t => Text(t, "operation") == op)} |");
int inferred = CountTools(t => Bool(t, "operationInferred"));
Line();
Line($"描述里未显式标注操作类型的工具（{inferred} 个）由 `ToolTaxonomy.OperationOf` 按工具名推断，表中以 `*` 标记；显式标注优先。");
Line();
Line("## 分类总览");
Line();
Line("| 大类 | 名称 | 工具数 | 域 |");
Line("|---|---|---:|---|");
foreach (JsonNode? categoryNode in categories)
{
    JsonObject category = categoryNode!.AsObject();
    string key = Text(category, "key");
    int count = CountTools(t => Text(t, "category") == key);
    IEnumerable<string> domains = category["domains"]!.AsArray().Select(d =>
    {
        string domain = d!.GetValue<string>();
        return $"`{domain}` ({CountTools(t => Text(t, "domain") == domain)})";
    });
    Line($"| `{key}` | {Text(category, "nameZh")} / {Text(category, "nameEn")} | {count} | {string.Join('、', domains)} |");
}
foreach (JsonNode? categoryNode in categories)
{
    JsonObject category = categoryNode!.AsObject();
    string key = Text(category, "key");
    JsonObject[] categoryTools = tools.Where(t => Text(t, "category") == key).ToArray();
    Line();
    Line($"## {key} — {Text(category, "nameZh")} / {Text(category, "nameEn")}（{categoryTools.Length}）");
    Line();
    Line(Esc(Text(category, "description")));
    foreach (JsonNode? domainNode in category["domains"]!.AsArray())
    {
        string domain = domainNode!.GetValue<string>();
        JsonObject[] inDomain = categoryTools.Where(t => Text(t, "domain") == domain)
            .OrderBy(t => Text(t, "layer"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => Text(t, "name"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => Text(t, "name"), StringComparer.Ordinal).ToArray();
        if (inDomain.Length == 0)
            continue;
        Line();
        Line($"### [{domain}]（{inDomain.Length}）");
        Line();
        Line("| 工具 | 层 | 操作 | 说明 |");
        Line("|---|---|---|---|");
        foreach (JsonObject tool in inDomain)
        {
            string op = Text(tool, "operation") + (Bool(tool, "operationInferred") ? "*" : "");
            Line($"| `{Text(tool, "name")}` | {Text(tool, "layer")} | {op} | {Esc(StripPrefix(Text(tool, "description")))} |");
        }
    }
}
string[] uncategorized = tools.Where(t => Text(t, "category") == "uncategorized").Select(t => Text(t, "name")).ToArray();
if (uncategorized.Length != 0)
    throw new InvalidDataException("Uncategorized tools present: " + string.Join(", ", uncategorized));

byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
if (check)
{
    string expected = Encoding.UTF8.GetString(bytes);
    string actual = File.Exists(outputPath) ? File.ReadAllText(outputPath, Encoding.UTF8) : "";
    // The timestamp is deliberately generated at write time. Compare all stable content so CI can
    // detect drift without requiring a commit to be regenerated at the same second.
    expected = MatrixPatterns.Timestamp().Replace(expected, "- 生成时间：<generated>");
    actual = MatrixPatterns.Timestamp().Replace(actual, "- 生成时间：<generated>");
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
        throw new InvalidDataException("Generated tool matrix differs from " + outputPath);
    Console.WriteLine($"tool-matrix.md: {tools.Length} tools in {categories.Count} categories; bytes match");
}
else
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    string temporary = Path.Combine(Path.GetDirectoryName(outputPath)!, ".tool-matrix-" + Guid.NewGuid().ToString("N") + ".tmp");
    try
    {
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, outputPath, true);
    }
    finally
    {
        if (File.Exists(temporary))
            File.Delete(temporary);
    }
    Console.WriteLine($"tool-matrix.md: {tools.Length} tools in {categories.Count} categories -> {outputPath}");
}

static string? Option(List<string> values, string name)
{
    int index = values.IndexOf(name);
    if (index < 0)
        return null;
    if (index + 1 >= values.Count)
        throw new ArgumentException("Missing value for " + name);
    string value = values[index + 1];
    values.RemoveAt(index + 1);
    values.RemoveAt(index);
    return value;
}

static string FindRoot(params string[] starts)
{
    foreach (string start in starts)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TiaPortalOpenness.Offline.slnx")))
                return directory.FullName;
        }
    }
    throw new DirectoryNotFoundException("Run this helper from within the repository.");
}

internal static partial class MatrixPatterns
{
    [GeneratedRegex(@"^\s*\[L\d\]\[(?:Category:)?[^\]]+\](?:\[[A-Za-z-]+\])?\s*")]
    internal static partial Regex Prefix();

    [GeneratedRegex(@"(?m)^- 生成时间：.*$")]
    internal static partial Regex Timestamp();
}
