using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Adapters.Contracts;
namespace TiaMcpServer.Siemens
{
    public static class CausalTraceParser
    {
        public static JsonObject AnalyzeXmlFileForTest(string xmlPath, string tag)
            => JsonSerializer.SerializeToNode(PlcCausalTraceParser.AnalyzeXmlFileForTest(xmlPath, tag))!.AsObject();
        public static string NormalizeOperand(string operand) => PlcCausalTraceParser.NormalizeOperand(operand);
        public static void AnalyzeBlock(XDocument doc, string blockName, string blockPath, string normTag, string displayTag,
            JsonArray writeSites, JsonArray readSites, HashSet<string> allConditions)
        {
            var writes = writeSites.OfType<JsonObject>().Select(w => (object?)new Dictionary<string, object?> { ["block"] = (string?)w["block"], ["network"] = (int?)w["network"] }).ToList();
            int previousWrites = writes.Count;
            var reads = new List<object?>();
            PlcCausalTraceParser.AnalyzeBlock(doc, blockName, blockPath, normTag, displayTag, writes, reads, allConditions);
            foreach (var row in writes.Skip(previousWrites)) writeSites.Add(JsonSerializer.SerializeToNode(row));
            foreach (var row in reads) readSites.Add(JsonSerializer.SerializeToNode(row));
        }
    }
}
