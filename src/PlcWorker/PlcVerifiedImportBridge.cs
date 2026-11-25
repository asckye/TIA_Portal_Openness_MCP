using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcp.PlcWorker
{
    internal static class PlcVerifiedImportBridge
    {
        internal static PlcCompilerEvidence Compiler(object result, bool diagnose)
            => Compiler(result, diagnose, false);

        internal static PlcCompilerEvidence Compiler(object result, bool diagnose, bool verifiedImport)
        {
            var type = result.GetType();
            var messages = type.GetProperty("Messages")!.GetValue(result);
            var collected = CompilerDiagnostics.CollectCompilerMessages(messages, preserveInterruption: verifiedImport);
            if (verifiedImport)
            {
                // The boundary already read the returned state and counts before requesting optional messages.
                return new PlcCompilerEvidence { Meta = new Dictionary<string, object?> {
                    ["nodes"] = Plain(collected.Nodes), ["declaredTotals"] = Plain(collected.DeclaredTotals),
                    ["truncated"] = collected.Truncated, ["collectFailures"] = collected.CollectFailures } };
            }
            string state = type.GetProperty("State")!.GetValue(result)!.ToString()!;
            int errors = (int)type.GetProperty("ErrorCount")!.GetValue(result)!;
            int warnings = (int)type.GetProperty("WarningCount")!.GetValue(result)!;
            var summary = collected.Summary(state, errors, warnings);
            var info = new List<string>(collected.Info);
            if (diagnose) foreach (var failure in collected.CollectFailures) info
                .Add("State=Information; Description=[diagnostic collection incomplete] " + failure);
            return new PlcCompilerEvidence {
                State = summary["effectiveState"]!.ToString(), ErrorCount = collected.HasError && errors == 0 ? (int?)null : errors,
                WarningCount = collected.HasWarning && warnings == 0 ? (int?)null : warnings, Errors = collected.Errors, Warnings = collected.Warnings, Info =
                    info,
                RawMessages = collected.Raw, Meta = (Dictionary<string, object?>)Plain(summary)! };
        }
        internal static PlcVerifiedCandidate Validate(PlcSoftwareRequest request)
        {
            var xml = PlcDocumentEditing.Read(request.FilePath);
            return new PlcVerifiedCandidate { Xml = xml, Name = PlcVerifiedImport.CandidateName(xml) };
        }
        internal static string Execute(PlcVerifiedExecution execution)
        {
            var request = execution.Request;
            var meta = JsonSerializer.SerializeToNode(execution.Meta)!.AsObject();
            var initial = new Dictionary<string, object?>(execution.Meta);
            void Compile() {
                try { execution.Compile!(); }
                finally
                {
                    foreach (var pair in execution.Meta)
                        if (!initial.TryGetValue(pair.Key, out var before) || !Equals(before, pair.Value))
                            meta[pair.Key] = JsonSerializer.SerializeToNode(pair.Value);
                }
            }
            try { return PlcVerifiedImport.Execute(execution.CandidateXml, execution.Binding, request.EvidenceDirectory, request.DryRun, request.ExpectedToken,
                execution.Export, execution.Import, execution.VerifyBinding, meta, execution.Compile == null ? null : (Action)Compile,
                execution.ExportType, execution.ExportNone, execution.GroupNamesBeforeImport, execution.ReadRefusalGroupNames); }
            finally { execution.Meta.Clear(); foreach (var pair in meta) execution.Meta[pair.Key] = Plain(pair.Value); }
        }
        private static object? Plain(JsonNode? node)
            => PlcVerifiedImport.PlainValue(node);
    }
}
