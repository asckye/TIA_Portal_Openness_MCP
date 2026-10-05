using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TiaMcp.Logic.V4.Inputs
{
    public enum ToolCallMode { ReadBatch, PreviewBatch, Transaction }

    public sealed class ToolTarget
    {
        public string Name { get; }
        public InputSchema Arguments { get; }
        public InputBudget Budget { get; }
        public bool Read { get; }
        public bool Preview { get; }
        public bool Transaction { get; }
        public bool Orchestration { get; }
        internal Func<ToolArguments, Error?>? BusinessValidation { get; }
        public ToolTarget(string name, InputSchema arguments, InputBudget budget, bool read = false, bool preview = false,
            bool transaction = false, bool orchestration = false, Func<ToolArguments, Error?>? businessValidation = null)
        {
            if (!InputGuard.Identifier(name)) throw new ArgumentException("A tool needs an identifier.");
            if (!arguments.Json.TryGetProperty("type", out var type) || type.GetString() != "object") throw new ArgumentException("A target inputSchema must describe an object.");
            Name = name; Arguments = arguments; Budget = budget; Read = read; Preview = preview;
            Transaction = transaction; Orchestration = orchestration; BusinessValidation = businessValidation;
        }
    }

    public static class ToolCallValidator
    {
        public static InputContract<ToolArguments> Arguments(ToolTarget target) => new InputContract<ToolArguments>(target.Arguments, target.Budget, value =>
        { InputGuard.Result(target.BusinessValidation?.Invoke(value)); return value; });

        // McpServer.Batch.cs:73-97 and ToolTransactionRules.cs:12-28. The caller
        // derives flags from its exact release catalog/transaction allowlist, never
        // from a verb prefix. There is no nested orchestration in these batches.
        public static InputContract<ToolCall[]> Create(ToolCallMode mode, IReadOnlyCollection<ToolTarget> targets, int nesting = 0)
        {
            if (!Enum.IsDefined(typeof(ToolCallMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            var catalog = targets.ToDictionary(t => t.Name, StringComparer.Ordinal);
            bool Admitted(ToolTarget target) => !target.Orchestration && !Nested(target.Name)
                && (mode == ToolCallMode.ReadBatch ? target.Read : mode == ToolCallMode.PreviewBatch ? target.Preview : target.Transaction);
            var branches = targets.Where(Admitted).Select(t => InputSchema.Object(new Dictionary<string, InputSchema> {
                ["name"] = InputSchema.String(allowed: new[] { t.Name }), ["arguments"] = t.Arguments }, new[] { "name", "arguments" })).ToArray();
            var row = branches.Length == 0 ? new InputSchema(V4Json.Deserialize<JsonElement>("false")) : InputSchema.Union(branches);
            return new InputContract<ToolCall[]>(InputSchema.Array(row, 1, mode == ToolCallMode.Transaction ? 20 : 50), new InputBudget(), calls =>
            {
                InputGuard.Require(nesting == 0);
                var normalized = new List<ToolCall>();
                foreach (var call in calls)
                {
                    InputGuard.Require(catalog.TryGetValue(call.Name, out _) && Admitted(catalog[call.Name]));
                    var target = catalog[call.Name];
                    var arguments = call.Arguments;
                    if (mode == ToolCallMode.PreviewBatch)
                    {
                        InputGuard.Require(target.Arguments.Json.TryGetProperty("properties", out var properties)
                            && properties.TryGetProperty("dryRun", out var dryRun) && dryRun.GetProperty("type").GetString() == "boolean");
                        var fields = arguments.Json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                        fields["dryRun"] = V4Json.Deserialize<JsonElement>("true");
                        arguments = new ToolArguments(V4Json.Data(fields)!.Value);
                    }
                    var result = Arguments(target).Validate(arguments, "arguments");
                    InputGuard.Result(result.Error);
                    normalized.Add(new ToolCall(call.Name, result.Value));
                }
                return normalized.ToArray();
            });
        }
        private static bool Nested(string name) => name.IndexOf("Batch", StringComparison.Ordinal) >= 0
            || new[] { "CallTool", "RunToolTransaction", "GetPlcCrossReferences" }.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
