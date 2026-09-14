using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        internal sealed class CompilerMessageCollectResult
        {
            public List<string> Raw { get; } = new List<string>();
            public List<string> Errors { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();
            public List<string> Info { get; } = new List<string>();
            public List<string> CollectFailures { get; } = new List<string>();
            internal JsonArray Nodes { get; } = new JsonArray();
            internal JsonArray DeclaredTotals { get; } = new JsonArray();
            internal bool HasError, HasWarning, Truncated;
            internal long ElapsedMs;
            internal int MaxNodes;

            internal JsonObject Summary(string rootState, int rootErrors, int rootWarnings)
            {
                var errors = HasError || rootErrors > 0 || rootState.Equals("Error", StringComparison.OrdinalIgnoreCase);
                var countIndicatesMissing = rootErrors > Nodes.Count || rootWarnings > Nodes.Count ||
                    DeclaredTotals.OfType<JsonObject>().Any(x => Count(x,"errors") > Nodes.Count || Count(x,"warnings") > Nodes.Count);
                var incomplete = Truncated || CollectFailures.Count > 0 || countIndicatesMissing;
                var state = errors ? "Error" : incomplete ? "Unknown" : HasWarning || rootWarnings > 0 ? "Warning" : rootState;
                var success = !errors && !incomplete && (state == "Success" || state == "Warning");
                return new JsonObject {
                    ["success"] = success, ["operationSuccess"] = success, ["effectiveState"] = state,
                    ["rootState"] = rootState, ["rootCounts"] = new JsonObject { ["errors"] = rootErrors, ["warnings"] = rootWarnings },
                    ["countSource"] = "Root API counts only; child/subtree and description totals are separate scopes, never added",
                    ["stateSource"] = "Root state/counts plus all observed subtree states/counts and declared errors",
                    ["visibleNodeCount"] = Nodes.Count, ["visibleErrorNodeCount"] = Errors.Count, ["visibleWarningNodeCount"] = Warnings.Count,
                    ["nodes"] = Nodes.DeepClone(), ["declaredTotals"] = DeclaredTotals.DeepClone(),
                    ["truncated"] = Truncated, ["incomplete"] = incomplete,
                    ["declaredCountExceedsVisibleNodes"] = countIndicatesMissing,
                    ["diagnosticsComplete"] = incomplete ? (JsonNode?)JsonValue.Create(false) : null,
                    ["coverage"] = incomplete ? "Partial" : "All exposed API nodes read; upstream compiler completeness unverified",
                    ["collectFailures"] = new JsonArray(CollectFailures.Select(x => JsonValue.Create(x)).ToArray()),
                    ["collectionElapsedMs"] = ElapsedMs, ["nodeLimit"] = MaxNodes, ["collectionBudgetMs"] = 30000,
                    ["budgetScope"] = "Checked between Openness calls; a single blocking API call cannot be preempted",
                    ["compileMode"] = "ICompilable.Compile; rebuild not requested" };
            }
        }

        internal static CompilerMessageCollectResult CollectCompilerMessages(object? messagesRoot, int maxNodes = 10000)
        {
            var result = new CompilerMessageCollectResult { MaxNodes = Math.Max(1, Math.Min(50000, maxNodes)) };
            var watch = Stopwatch.StartNew();
            var seen = new HashSet<object>(CompilerReferenceComparer.Instance);
            Walk(messagesRoot, "messages", 0);
            result.ElapsedMs = watch.ElapsedMilliseconds;
            return result;

            void Walk(object? list, string parent, int depth)
            {
                if (list == null) return;
                if (depth > 64) { result.Truncated = true; return; }
                if (list is not IEnumerable sequence || list is string)
                { result.CollectFailures.Add(parent + ": messages is not a collection"); return; }
                try
                {
                    int index = 0;
                    foreach (var message in sequence)
                    {
                        var treePath = parent + "/" + index++;
                        if (message == null) continue;
                        if (result.Nodes.Count >= result.MaxNodes || watch.ElapsedMilliseconds >= 30000) { result.Truncated = true; return; }
                        if (!seen.Add(message)) { result.CollectFailures.Add(treePath + ": repeated object/cycle"); continue; }
                        var node = new JsonObject { ["treePath"] = treePath, ["parentPath"] = parent, ["depth"] = depth };
                        foreach (var property in new[] { "State", "Description", "Path", "ObjectPath", "DateTime", "ErrorCount", "WarningCount", "Line", "Column", "ErrorCode" })
                        {
                            var value = Read(message, property, treePath);
                            if (value != null) node[property] = value.ToString();
                        }
                        // Each proxy property and child collection is read once. Avoid repeated
                        // unsupported GetAttribute probes against the engineering process.
                        var children = Read(message, "Messages", treePath);
                        result.Nodes.Add(node);
                        var line = string.Join("; ", node.Select(kv => kv.Key + "=" + kv.Value));
                        result.Raw.Add(line);
                        var state = node["State"]?.ToString() ?? "";
                        var desc = node["Description"]?.ToString() ?? "";
                        var error = state.Equals("Error", StringComparison.OrdinalIgnoreCase) || Count(node, "ErrorCount") > 0;
                        var warning = state.Equals("Warning", StringComparison.OrdinalIgnoreCase) || Count(node, "WarningCount") > 0;
                        var declared = ParseDeclared(desc);
                        if (declared != null)
                        {
                            declared["treePath"] = treePath; declared["source"] = "Description"; declared["raw"] = desc;
                            result.DeclaredTotals.Add(declared);
                            error |= Count(declared, "errors") > 0;
                            warning |= Count(declared, "warnings") > 0;
                        }
                        var cap = Regex.IsMatch(desc, @"(?i)(maximum|maximal|limit|truncat|最多|最大|上限|超过).{0,100}(1[,.]?000|display|显示|messages|meldungen)|1[,.]?000.{0,60}(maximum|limit|最多|最大)");
                        if (cap) { node["upstreamLimitNotice"] = true; result.Truncated = true; }
                        result.HasError |= error; result.HasWarning |= warning;
                        if (error) result.Errors.Add(line); else if (warning) result.Warnings.Add(line); else result.Info.Add(line);
                        Walk(children, treePath + "/messages", depth + 1);
                    }
                }
                catch (Exception ex) { result.CollectFailures.Add(parent + ": " + (ex.InnerException ?? ex).Message); }
            }

            object? Read(object message, string property, string path)
            {
                try { return message.GetType().GetProperty(property)?.GetValue(message); }
                catch (Exception ex) { result.CollectFailures.Add(path + "." + property + ": " + (ex.InnerException ?? ex).Message); return null; }
            }
        }

        private static int Count(JsonObject node, string property) => int.TryParse(node[property]?.ToString(), out var n) ? n : 0;
        private static JsonObject? ParseDeclared(string text)
        {
            var result = new JsonObject();
            foreach (var pair in new[] { ("errors", @"errors?|Fehler|错误"), ("warnings", @"warnings?|Warnungen?|警告") })
            {
                var match = Regex.Match(text, @"(?i)(\d+)\s*(?:个\s*)?(?:" + pair.Item2 + @")(?![a-zA-Z])|(?:" + pair.Item2 + @")\s*[:：=]\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, out var n)) result[pair.Item1] = n;
            }
            return result.Count == 0 ? null : result;
        }
        private sealed class CompilerReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly CompilerReferenceComparer Instance = new CompilerReferenceComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
