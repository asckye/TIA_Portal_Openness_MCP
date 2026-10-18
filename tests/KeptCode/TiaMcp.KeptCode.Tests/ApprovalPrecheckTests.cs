using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class ApprovalPrecheckTests
    {
        public static class Probe
        {
            internal static int Previews, Writes;
            internal static string Fault = "";
            [McpServerTool(Name = "CreatePlcTag"), ToolClassification("L1", "PLC-Software", "WRITE", batchWrite: true)]
            public static CallToolResult Write(string condition = "", bool dryRun = true)
            {
                if (dryRun) Previews++; else Writes++;
                if (condition == "cancel") throw new OperationCanceledException();
                if (condition == "no-effect") return McpServer.V4Result("CreatePlcTag", new JsonObject {
                    ["status"] = "not-found-not-deleted", ["attempted"] = false, ["executed"] = false,
                    ["deleted"] = false, ["targetIdentity"] = "" });
                if (condition.Length > 0 || Fault == "changed") throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Fixture precondition.", condition.Length > 0 ? condition : "name");
                // "unknown" reaches a journaled native boundary first; "unknown-unissued" fails before any native call.
                if (!dryRun && Fault == "unknown") InvocationJournal.NativeCallStarted();
                if (!dryRun && Fault is "unknown" or "unknown-unissued") return McpServer.V4Result("CreatePlcTag", null, new Error("Fixture unknown.", new OutcomeUnknownDetails("fixture", new Dictionary<string, JsonElement>())), Outcome.Unknown, Execution.Unknown, Completeness.Unknown);
                return McpServer.V4Result("CreatePlcTag", new JsonObject { ["planned"] = dryRun }, completed: !dryRun);
            }
            [McpServerTool(Name = "CompileDevice"), ToolClassification("L2", "Hardware", "EXECUTE")]
            public static CallToolResult Compile()
            {
                Writes++;
                return McpServer.V4Result("CompileDevice", new JsonObject { ["evidence"] = new JsonObject { ["nativeOutcomeUnknown"] = true } }, new Error("Fixture unknown compile.", new OutcomeUnknownDetails("fixture", new Dictionary<string, JsonElement>())), Outcome.Unknown, Execution.Unknown, Completeness.Unknown);
            }
            [McpServerTool(Name = "ListPlcTags"), ToolClassification("L1", "PLC-Software", "READ")]
            public static CallToolResult Read() => McpServer.V4Result("ListPlcTags", new JsonObject());
        }
        private static ToolArguments Args(string json) => new ToolArguments(JsonSerializer.Deserialize<JsonElement>(json));
        private static void Configure()
        {
            McpServer.ConfigureToolBridge(new ToolCatalog(new[] { typeof(McpServer), typeof(Probe) }), () => false, new HashSet<string>());
            Probe.Previews = Probe.Writes = 0; Probe.Fault = "";
        }
        [Theory]
        [InlineData("SW.Tags.PlcTagTable")][InlineData("SW.Types.PlcStruct")][InlineData("SW.Blocks.FC")]
        public void Single_xml_import_reads_object_names_and_refuses_existing_without_overwrite(string kind)
        {
            var document = System.Xml.Linq.XDocument.Parse($"<Document><{kind}><AttributeList><Name>Existing</Name></AttributeList></{kind}><{kind}><AttributeList><Name>Other</Name></AttributeList></{kind}></Document>");
            string prefix = kind.StartsWith("SW.Blocks.", StringComparison.Ordinal) ? "SW.Blocks." : kind.StartsWith("SW.Types.", StringComparison.Ordinal) ? "SW.Types." : "SW.Tags.PlcTagTable";
            var ex = Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => TiaMcp.Adapters.PlcFoundationPolicy.RequireImportAvailable(document, prefix, false, name => name == "Existing"));
            Assert.Equal("overwrite", ex.ParamName); Assert.Contains("Existing", ex.Message);
            TiaMcp.Adapters.PlcFoundationPolicy.RequireImportAvailable(document, prefix, true, _ => throw new Exception("Overwrite does not check existence."));
            TiaMcp.Adapters.PlcFoundationPolicy.RequireImportAvailable(document, prefix, false, _ => false);
        }
        [Theory]
        [InlineData(true)][InlineData(false)]
        public void Symbol_lookup_preserves_mixed_case_and_avoids_full_scan_when_find_is_case_insensitive(bool insensitive)
        {
            string[] members = Enumerable.Range(0, 10000).Select(i => "Tag" + i).ToArray(); int names = 0;
            string? Find(string name) => members.FirstOrDefault(s => string.Equals(s, name, insensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            string Name(string s) { names++; return s; }
            Assert.True(TiaMcp.Adapters.PlcFoundationPolicy.SymbolExists(members, Find, Name, "tAg9999"));
            names = 0; Assert.False(TiaMcp.Adapters.PlcFoundationPolicy.SymbolExists(members, Find, Name, "Missing"));
            Assert.Equal(insensitive ? 2 : 10001, names);
        }
    }
}
