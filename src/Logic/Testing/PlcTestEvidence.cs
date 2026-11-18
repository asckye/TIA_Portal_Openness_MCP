using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.Logic.V4;

namespace TiaMcp.Logic.Testing
{
    public static class PlcTestEvidence
    {
        public static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        public static string Scrub(string? text) => NativeFailureDiagnostics.Text(ScrubPaths(text)) ?? "";

        // Reports preserve multiline messages and source text; native diagnostics additionally remove stack traces.
        public static string ScrubPaths(string? text)
        {
            const string root = @"(?:[A-Za-z]:[\\/]|\\\\|(?<![\w:/])/)";
            string FileName(string path) => path.Replace('\\', '/').TrimEnd('/').Split('/').Last();
            string safe = Regex.Replace(text ?? "", @"(?<quote>[""'])(?<path>" + root + @"[^\r\n""']+)\k<quote>",
                match => match.Groups["quote"].Value + FileName(match.Groups["path"].Value) + match.Groups["quote"].Value,
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            // Prefer the first complete file name; a later path on the line is a separate match.
            const string file = @"(?:[^\\/\r\n""'<>|;:]*?[\\/])*?[^\s\\/""'<>|;,:]+\.[A-Za-z0-9]+(?=$|[\s""'<>|;,):])";
            safe = Regex.Replace(safe, root + file + "|" + root + @"[^\s""'<>|;,:]+",
                match => FileName(match.Value), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return safe;
        }

        public static JsonObject Normalize(JsonObject outcome, string verdict, string? diagnostic = null)
        {
            var source = outcome["data"] as JsonObject;
            var details = new JsonObject { ["outcome"] = outcome["meta"]?["outcome"]?.DeepClone(),
                ["execution"] = outcome["meta"]?["execution"]?.DeepClone() };
            foreach (string key in new[] { "steps", "result", "api", "verdict", "status", "executedSteps", "assertionsPassed", "failedSteps",
                "operatingModeRestoreError" })
                if (source?.ContainsKey(key) == true) details[key] = Clean(source[key]);
            string message = Scrub(diagnostic);
            if (message.Length == 0) message = Scrub(Text(outcome["error"]?["message"]));
            if (message.Length == 0) message = Scrub(Text(source?["summary"]));
            if (message.Length == 0) message = verdict;
            string code = Text(outcome["error"]?["code"]);
            if (code.Length == 0) code = verdict switch {
                "passed" => "TEST_PASSED", "failed" => "ASSERTION_FAILED", "skipped" => "TEST_SKIPPED",
                "unknown" => "OUTCOME_UNKNOWN", _ => "TEST_ERROR"
            };
            return new JsonObject { ["code"] = code,
                ["message"] = message, ["details"] = details };
        }

        private static JsonNode? Clean(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                var result = new JsonObject();
                foreach (var pair in obj.Where(p => p.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) < 0
                    && !p.Key.Equals("password", StringComparison.OrdinalIgnoreCase))) result[Scrub(pair.Key)] = Clean(pair.Value);
                return result;
            }
            if (node is JsonArray array) return new JsonArray(array.Select(Clean).ToArray());
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? JsonValue.Create(Scrub(text)) : node?.DeepClone();
        }
    }
}
