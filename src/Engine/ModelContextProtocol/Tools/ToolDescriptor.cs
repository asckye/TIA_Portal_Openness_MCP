using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed class ToolDescriptor
    {
        public string Name { get; }
        public string RawDescription { get; }
        public Tool Tool { get; }
        public ToolDescriptorClassification? Classification { get; }
        public string Signature { get; }
        public IReadOnlyList<ToolParameterDescriptor> Parameters { get; }
        public ToolDryRunDescriptor DryRun { get; }
        public string? CandidateFamily { get; }
        public string Execution { get; }

        public ToolDescriptor(string name, string rawDescription, Tool tool, ToolDescriptorClassification? classification,
            string signature, IReadOnlyList<ToolParameterDescriptor> parameters, ToolDryRunDescriptor dryRun,
            string? candidateFamily, string execution)
        {
            Name = name; RawDescription = rawDescription; Tool = tool; Classification = classification;
            Signature = signature; Parameters = parameters; DryRun = dryRun; CandidateFamily = candidateFamily; Execution = execution;
        }
    }

    internal sealed class ToolDescriptorClassification
    {
        public string Level { get; }
        public string Domain { get; }
        public string Operation { get; }
        public bool BatchRead { get; }
        public bool BatchWrite { get; }
        public ToolDescriptorClassification(string level, string domain, string operation, bool batchRead, bool batchWrite)
        { Level = level; Domain = domain; Operation = operation; BatchRead = batchRead; BatchWrite = batchWrite; }
    }

    internal sealed class ToolParameterDescriptor
    {
        public string Name { get; }
        public string FriendlyType { get; }
        public string ClrType { get; }
        public bool Required { get; }
        public string? DefaultJson { get; }
        public string? DefaultText { get; }
        public string Description { get; }
        public bool Synthesized { get; }
        public IReadOnlyList<string> AllowedValues { get; }
        public ToolParameterDescriptor(string name, string friendlyType, string clrType, bool required, string? defaultJson,
            string? defaultText, string description, bool synthesized, IReadOnlyList<string> allowedValues)
        {
            Name = name; FriendlyType = friendlyType; ClrType = clrType; Required = required; DefaultJson = defaultJson;
            DefaultText = defaultText; Description = description; Synthesized = synthesized; AllowedValues = allowedValues;
        }
    }

    internal sealed class ToolDryRunDescriptor
    {
        public bool Present { get; }
        public bool Default { get; }
        public ToolDryRunDescriptor(bool present, bool @default) { Present = present; Default = @default; }
    }

    internal interface IToolCatalogView
    {
        IReadOnlyDictionary<string, ToolDescriptor> All { get; }
        IReadOnlyDictionary<string, ToolDescriptor> IncludingUnavailable { get; }
        IReadOnlyList<ToolDescriptor> Lite { get; }
        ToolDescriptor? Find(string name, bool includeUnavailable = false);
        JsonArray BehaviorCapabilities { get; }
    }

    internal interface IToolInvoker
    {
        Error? Bind(string name, ToolArguments arguments, out IBoundToolCall? call);
        ToolInvocationResult Invoke(string name, ToolArguments arguments, bool preview);
        Error? ValidateArguments(ToolDescriptor tool, JsonElement arguments, JsonElement schema, bool typedFamiliesOnly = false);
        McpServerTool CreateTool(ToolDescriptor tool);
    }

    internal interface IBoundToolCall
    {
        ToolInvocationResult Invoke(bool preview);
    }

    internal sealed class ToolInvocationResult
    {
        internal CallToolResult Result { get; }
        internal bool NativeCallIssued { get; }
        internal ToolInvocationResult(CallToolResult result, bool nativeCallIssued)
        { Result = result; NativeCallIssued = nativeCallIssued; }
    }

    public static partial class McpServer
    {
        private static readonly object ToolCatalogSync = new object();
        internal static IToolCatalogView CatalogView
        {
            get
            {
#if !TIA_ENGINE_HOST
                lock (ToolCatalogSync)
                    if (_bridgeCatalog == null) InitializeToolCatalog(ref _bridgeCatalog, ref _bridgeInvoker);
#endif
                return _bridgeCatalog ?? throw new InvalidOperationException("Tool catalog is not configured.");
            }
        }
        internal static IToolInvoker ToolInvoker { get { _ = CatalogView; return _bridgeInvoker!; } }
        static partial void InitializeToolCatalog(ref IToolCatalogView? catalog, ref IToolInvoker? invoker);

        internal static Dictionary<string, ToolDescriptor> AllToolDescriptors(bool includeUnavailable = false)
            => (includeUnavailable ? CatalogView.IncludingUnavailable : CatalogView.All)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        internal static List<PreflightLogic.ParameterSpec> SpecsOf(ToolDescriptor tool)
            => tool.Parameters.Select(p => new PreflightLogic.ParameterSpec(p.Name, p.FriendlyType, p.Required,
                p.DefaultJson, p.Description, p.Synthesized, p.AllowedValues)).ToList();
        internal static string ToolDescription(ToolDescriptor tool)
            => tool.RawDescription + TiaOpenness.Shared.ToolUsageCatalog.Hint(tool.Name);
        private static ToolDescriptorClassification? ClassificationOf(ToolDescriptor tool) => tool.Classification;
        private static bool IsWrite(ToolDescriptorClassification? classification)
            => classification?.Operation is "WRITE" or "ONLINE-WRITE";
        internal static BehaviorPolicy ToolBehaviorPolicy(string entry, BehaviorPolicy fallback)
        {
            foreach (var row in CatalogView.BehaviorCapabilities.OfType<JsonObject>())
                if (row["entries"]!.AsArray().Any(name => (string?)name == entry))
                    return (string?)row["state"] == "safe-v4" ? BehaviorPolicy.SafeV4 : BehaviorPolicy.Current;
            return fallback;
        }
    }
}
