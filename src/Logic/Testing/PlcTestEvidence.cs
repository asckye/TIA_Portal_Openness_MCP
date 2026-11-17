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
        public static string Scrub(string? text)
        {
            string safe = NativeFailureDiagnostics.Text(text) ?? "";
            return Regex.Replace(safe, @"(?:[A-Za-z]:[\\/]|\\\\)[^\r\n""'<>]*|(?<![\w:])/(?:[^\s/]+/)+[^\s""'<>]*",
                "[path]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
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
            return new JsonObject { ["code"] = code.Length == 0 ? verdict == "failed" ? "ASSERTION_FAILED" : "TEST_PASSED" : code,
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
