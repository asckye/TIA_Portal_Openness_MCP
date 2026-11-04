using System;
using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ToolClassificationAttribute : Attribute
    {
        public ToolMetadata.Classification Value { get; }
        public ToolClassificationAttribute(string layer, string domain, string operation, bool inferred = false, bool batchRead = false, bool batchWrite = false)
        { Value = new ToolMetadata.Classification(layer, domain, operation, inferred, batchRead, batchWrite); }
    }

    // Explicit catalog metadata. Prose may be translated without changing admission,
    // taxonomy, enum hints or the placeholder calls frozen for P6-26.
    public static partial class ToolMetadata
    {
        public sealed class Classification
        {
            public string Layer { get; }
            public string Domain { get; }
            public string Operation { get; }
            public bool Inferred { get; }
            public bool BatchRead { get; }
            public bool BatchWrite { get; }
            public bool? ReadOnlyHint { get; }
            public bool? DestructiveHint { get; }
            public bool? IdempotentHint { get; }
            public bool? OpenWorldHint { get; }
            public Classification(string layer, string domain, string operation, bool inferred, bool batchRead, bool batchWrite,
                bool? readOnlyHint = null, bool? destructiveHint = null, bool? idempotentHint = null, bool? openWorldHint = null)
            {
                Layer = layer; Domain = domain; Operation = operation; Inferred = inferred; BatchRead = batchRead; BatchWrite = batchWrite;
                ReadOnlyHint = readOnlyHint; DestructiveHint = destructiveHint; IdempotentHint = idempotentHint; OpenWorldHint = openWorldHint;
            }
        }
        public sealed class ParameterHint
        {
            public string ExampleJson { get; }
            public IReadOnlyList<string> AllowedValues { get; }
            public string? Pattern { get; }
            public ParameterHint(string exampleJson, params string[] allowedValues)
                : this(exampleJson, allowedValues, null) { }
            public ParameterHint(string exampleJson, string[] allowedValues, string? pattern)
            { ExampleJson = exampleJson; AllowedValues = Array.AsReadOnly(allowedValues); Pattern = pattern; }
        }
        public static string? RuntimeOperation(string method) => RuntimeOperations.TryGetValue(method, out var operation) ? operation : null;

        public static Classification? Find(string name) => Classifications.TryGetValue(name, out var row) ? row : null;
        public static ParameterHint? Parameter(string name, string parameter) => Parameters.TryGetValue(name + "." + parameter, out var row) ? row : null;
        public static string SearchAliases(string name) => name == "WritePlcSclSourceFile" ? "mojibake/乱码" : "";
    }
}
