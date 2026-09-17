using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ReadUnifiedGraphicSelection(string softwarePath, string expectedProject, string screenPath,
            string itemNamesJson, string cursor = "", int pageSize = 20, int budgetMs = 5000)
            => RunHmiStepTool("ReadUnifiedGraphicSelection", meta =>
            {
                meta["readOnly"] = true; meta["softwarePath"] = softwarePath; meta["screenPath"] = screenPath;
                var names = UnifiedGraphicSelection.Names(itemNamesJson);
                var trace = new HmiReadTrace();
                JsonObject? fault = null;
                void Error(Exception ex)
                {
                    if (!HmiReadSafety.ConnectionUnavailable(ex)) return;
                    fault = new JsonObject { ["softwarePath"] = softwarePath, ["screenPath"] = screenPath, ["error"] = ex.ToString(), ["timestamp"] = DateTimeOffset.UtcNow.ToString("o") };
                    trace.AddTo(fault); RecordHmiReadFault(fault);
                }
                var scope = new JsonObject { ["screenPath"] = screenPath, ["itemNames"] = JsonNode.Parse(itemNamesJson) };
                var page = MigrationPage("ReadUnifiedGraphicSelection", softwarePath, expectedProject, scope, cursor, pageSize, budgetMs,
                    hmi => GuardGraphicSelection(UnifiedGraphicSelection.Capture(hmi, screenPath, names, trace), Error), Error, names.Length + 1);
                foreach (var entry in page.Meta!) meta[entry.Key] = entry.Value?.DeepClone();
                // A resumed iterator holds its original trace; its callback records
                // the session fault, which is also reflected on the current response.
                if (_hmiReadFault != null)
                {
                    if (fault != null) foreach (var entry in fault) meta[entry.Key] = entry.Value?.DeepClone();
                    meta["lastFailure"] = _hmiReadFault.DeepClone(); meta["connectionUnavailable"] = true;
                    meta["remoteInspectionStopped"] = true; meta["requiresExplicitRebind"] = true;
                }
                meta["expectedObjectCount"] = names.Length;
                meta["selectionKind"] = "caller-defined exact-name selection";
                meta["nativeGroupVerified"] = false;
                if (string.IsNullOrEmpty(cursor)) trace.AddTo(meta);
                return "Read-only selection evidence. Follow nextCursor and retain all page Meta objects for CompareUnifiedGraphicSelections. Native editor groups remain unverified; no coordinate writes performed.";
            });
        private static IEnumerable<JsonObject> GuardGraphicSelection(IEnumerable<JsonObject> source, Action<Exception> error)
        {
            // This iterator is managed adapter state, not an Openness enumerator.
            using var iterator = source.GetEnumerator();
            while (true)
            {
                bool more;
                try { more = iterator.MoveNext(); }
                catch (Exception ex) { error(ex); throw; }
                if (!more) yield break;
                yield return iterator.Current;
            }
        }
        public ResponseMessage CompareUnifiedGraphicSelections(string beforePagesJson, string afterPagesJson)
        {
            try { return new ResponseMessage { Message = "Offline comparison of raw geometry; no TIA call or coordinate write.", Meta = UnifiedGraphicSelection.Compare(beforePagesJson, afterPagesJson) }; }
            catch (Exception ex)
            {
                return new ResponseMessage { Message = "Comparison refused; supply complete compatible selection pages.", Meta = new JsonObject {
                    ["success"] = false, ["operationSuccess"] = false, ["apiCallSuccess"] = false, ["dataComplete"] = false,
                    ["readOnly"] = true, ["offline"] = true, ["failureCount"] = 1, ["failures"] = new JsonArray(MigrationRead.Failure("comparison", "InvalidSnapshot", ex)) } };
            }
        }
    }
}
