using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcp.PlcWorker
{
    internal static class PlcVerifiedImportBridge
    {
        internal static PlcCompilerEvidence Compiler(object result, bool diagnose)
        {
            var type = result.GetType();
            var messages = type.GetProperty("Messages")!.GetValue(result);
            var collected = CompilerDiagnostics.CollectCompilerMessages(messages);
            string state = type.GetProperty("State")!.GetValue(result)!.ToString()!;
            int errors = (int)type.GetProperty("ErrorCount")!.GetValue(result)!;
            int warnings = (int)type.GetProperty("WarningCount")!.GetValue(result)!;
            var summary = collected.Summary(state, errors, warnings);
            var info = new List<string>(collected.Info);
            if (diagnose) foreach (var failure in collected.CollectFailures) info.Add("State=Information; Description=[diagnostic collection incomplete] " + failure);
            return new PlcCompilerEvidence { State = summary["effectiveState"]!.ToString(), ErrorCount = collected.HasError && errors == 0 ? (int?)null : errors,
                WarningCount = collected.HasWarning && warnings == 0 ? (int?)null : warnings, Errors = collected.Errors, Warnings = collected.Warnings, Info = info,
                RawMessages = collected.Raw, Meta = (Dictionary<string, object?>)Plain(summary)! };
        }
        internal static PlcVerifiedCandidate Validate(PlcSoftwareRequest request)
        {
            var xml = PlcDocumentEditing.Read(request.FilePath);
            return new PlcVerifiedCandidate { Xml = xml, Name = PlcDocumentEditing.Name(PlcDocumentEditing.Parse(xml)) };
        }
        internal static string Execute(PlcVerifiedExecution execution)
        {
            var request = execution.Request;
            var meta = JsonSerializer.SerializeToNode(execution.Meta)!.AsObject();
            var initial = new Dictionary<string, object?>(execution.Meta);
            void Compile() { execution.Compile!(); foreach (var pair in execution.Meta) if (!initial.TryGetValue(pair.Key, out var before) || !Equals(before, pair.Value)) meta[pair.Key] = JsonSerializer.SerializeToNode(pair.Value); }
            try { return PlcVerifiedImport.Execute(execution.CandidateXml, execution.Binding, request.EvidenceDirectory, request.DryRun, request.ExpectedToken,
                execution.Export, execution.Import, execution.VerifyBinding, meta, execution.Compile == null ? null : (Action)Compile); }
            finally { execution.Meta.Clear(); foreach (var pair in meta) execution.Meta[pair.Key] = Plain(pair.Value); }
        }
        private static object? Plain(JsonNode? node)
        {
            if (node is JsonObject obj) return obj.ToDictionary(p => p.Key, p => Plain(p.Value));
            if (node is JsonArray array) return array.Select(Plain).ToArray();
            if (node is not JsonValue) return null;
            return JsonSerializer.Deserialize<object>(node.ToJsonString());
        }
    }
}
