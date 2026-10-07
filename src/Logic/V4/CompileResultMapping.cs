using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.Logic.V4
{
    public static class CompileResultMapping
    {
        public const int MaximumErrorMessages = 10;
        public static Error? Errors(JsonObject data)
        {
            var evidence = data["evidence"] as JsonObject;
            int Count(string key, string root) => int.TryParse((data[key] ?? evidence?["rootCounts"]?[root])?.ToString(), out int value) ? Math.Max(0, value) : 0;
            int errors = Count("errorCount", "errors"), warnings = Count("warningCount", "warnings");
            string state = (data["state"] ?? evidence?["effectiveState"] ?? evidence?["rootState"])?.ToString() ?? "";
            var messages = new List<CompileErrorMessage>();
            var nodes = (data["diagnosticMessages"] ?? evidence?["nodes"] ?? data["nodes"]) as JsonArray;
            IEnumerable<CompileErrorMessage> Flatten(JsonArray tree, string parentPath = "")
            {
                var objects = tree.OfType<JsonObject>().ToArray();
                var byTreePath = objects.Where(n => n["treePath"] != null).ToDictionary(n => n["treePath"]!.ToString());
                var parents = new HashSet<string>(objects.Select(n => n["parentPath"]?.ToString() ?? ""), StringComparer.Ordinal);
                string PathOf(JsonObject node)
                {
                    string path = (node["path"] ?? node["Path"] ?? node["ObjectPath"])?.ToString() ?? "";
                    if (path != "") return path;
                    string ancestor = node["parentPath"]?.ToString() ?? "";
                    if (ancestor.EndsWith("/messages", StringComparison.Ordinal)) ancestor = ancestor.Substring(0, ancestor.Length - 9);
                    return byTreePath.TryGetValue(ancestor, out var parent) ? PathOf(parent) : parentPath;
                }
                foreach (var node in tree.OfType<JsonObject>())
                {
                    string path = PathOf(node);
                    if ((node["messages"] ?? node["Messages"]) is JsonArray children && children.Count > 0)
                        foreach (var child in Flatten(children, path)) yield return child;
                    else if (!parents.Contains(node["treePath"]?.ToString() + "/messages"))
                    {
                        string severity = (node["state"] ?? node["State"])?.ToString() ?? "";
                        string description = (node["description"] ?? node["Description"])?.ToString() ?? "";
                        if (severity.Equals("Error", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(description))
                            yield return new CompileErrorMessage(path, description);
                    }
                }
            }
            if (nodes != null) messages.AddRange(Flatten(nodes));
            if (messages.Count == 0 && data["errors"] is JsonArray formatted)
                foreach (var text in formatted.Select(x => x?.ToString() ?? ""))
                {
                    string Field(string name) => Regex.Match(text, @"(?:^|; )" + name + @"=(.*?)(?=; (?:Path|DateTime|ErrorCount|WarningCount|Description|State)=|$)").Groups[1].Value;
                    string description = Field("Description");
                    if (description == "" && text.StartsWith("State=", StringComparison.Ordinal)) continue;
                    messages.Add(new CompileErrorMessage(Field("Path"), description != "" ? description : text));
                }
            if (errors == 0 && state != "Error" && messages.Count == 0) return null;
            errors = Math.Max(errors, messages.Count);
            var first = messages.Take(MaximumErrorMessages).ToArray();
            string message = "Compile finished with " + errors + " errors and " + warnings + " warnings";
            if (first.Length > 0) message += "; first: " + (first[0].Path == "" ? "" : first[0].Path + ": ") + first[0].Description;
            return new Error(message.EndsWith(".", StringComparison.Ordinal) ? message : message + ".", new CompileErrorsDetails(errors, warnings, first));
        }
    }
}
