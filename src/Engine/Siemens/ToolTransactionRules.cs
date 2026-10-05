using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class ToolTransactionRules
    {
        // ---- transactions ------------------------------------------------------------------------------------------------------
        internal sealed class ToolCall { public string Name = ""; public string ArgumentsJson = "{}"; }
        internal static ToolCall[] ParseToolCalls(string json)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(json ?? ""); } catch (System.Text.Json.JsonException ex) { throw new ArgumentException("callsJson must be a JSON array of {name, arguments}: " + ex.Message); }
            if (node is not JsonArray array) throw new ArgumentException("callsJson must be a JSON array of {name, arguments}.");
            if (array.Count < 1 || array.Count > 20) throw new ArgumentException("callsJson needs 1..20 tool calls.");
            var calls = new List<ToolCall>();
            foreach (var entry in array)
            {
                if (entry is not JsonObject o || o["name"] is not JsonValue nameValue) throw new ArgumentException("Each call is {\"name\":\"<tool>\",\"arguments\":{...}}.");
                var name = nameValue.ToString();
                if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsLetterOrDigit(c))) throw new ArgumentException("Tool names are plain identifiers: " + name);
                if (string.Equals(name, "RunToolTransaction", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "CallTool", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Nested transactions / CallTool are refused inside a transaction.");
                var arguments = o["arguments"];
                if (arguments != null && arguments is not JsonObject) throw new ArgumentException("arguments of " + name + " must be a JSON object.");
                calls.Add(new ToolCall { Name = name, ArgumentsJson = arguments?.ToJsonString() ?? "{}" });
            }
            return calls.ToArray();
        }
        internal static void ValidateTransactionRequest(string text, ToolCall[] calls, bool confirmChange, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200) throw new ArgumentException("text (the undo description shown in TIA) must be 1..200 characters; TIA refuses an empty one.");
            if (calls.Length == 0) throw new ArgumentException("At least one tool call is required.");
            HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);
        }
        // Every transaction call must execute: TIA V21 evidence, 2026-09-19 (docs/reference/real-machine-ledger.md): only an
        // existing dryRun key was flipped, so calls without one ran as previews (tool default true) while the transaction still
        // reported committed=true; the key is now written unconditionally (CallTool ignores it on tools without a dryRun parameter).
        internal static string ForceRealExecution(string argumentsJson)
        {
            var o = JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
            var key = o.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, "dryRun", StringComparison.OrdinalIgnoreCase)) ?? "dryRun";
            o[key] = false;
            return o.ToJsonString();
        }
    }
}
