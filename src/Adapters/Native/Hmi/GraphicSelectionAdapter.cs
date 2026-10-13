using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class GraphicSelectionAdapter
    {
        private readonly IUnifiedSession _session;
        private readonly MigrationReadAdapter _migrationRead;

        public GraphicSelectionAdapter(IUnifiedSession session, MigrationReadAdapter migrationRead)
        {
            _session = session;
            _migrationRead = migrationRead;
        }

        public HmiMessage ReadUnifiedGraphicSelection(string softwarePath, string expectedProject, string screenPath,
            string itemNamesJson, string cursor = "", int pageSize = 20, int budgetMs = 5000)
            => _session.RunHmiStepTool("ReadUnifiedGraphicSelection", meta =>
            {
                meta["readOnly"] = true; meta["softwarePath"] = softwarePath; meta["screenPath"] = screenPath;
                var names = UnifiedGraphicSelection.Names(itemNamesJson);
                var trace = new HmiReadTrace();
                HmiObject? fault = null;
                void Error(Exception ex)
                {
                    if (!HmiReadSafety.ConnectionUnavailable(ex)) return;
                    fault = new HmiObject { ["softwarePath"] = softwarePath, ["screenPath"] = screenPath, ["error"] = ex.ToString(), ["timestamp"] = DateTimeOffset.UtcNow.ToString("o") };
                    trace.AddTo(fault); _session.RecordHmiReadFault(fault);
                }
                var scope = new HmiObject { ["screenPath"] = screenPath, ["itemNames"] = UnifiedRequestValues.Parse(itemNamesJson) };
                var page = _migrationRead.MigrationPage("ReadUnifiedGraphicSelection", softwarePath, expectedProject, scope, cursor, pageSize, budgetMs,
                    hmi => GuardGraphicSelection(UnifiedGraphicSelection.Capture(hmi, screenPath, names, trace), Error), Error, names.Length + 1);
                foreach (var entry in page.Meta!) meta[entry.Key] = entry.Value?.DeepClone();
                // A resumed iterator holds its original trace; its callback records
                // the session fault, which is also reflected on the current response.
                if (_session.HmiReadFault != null)
                {
                    if (fault != null) foreach (var entry in fault) meta[entry.Key] = entry.Value?.DeepClone();
                    meta["lastFailure"] = _session.HmiReadFault.DeepClone(); meta["connectionUnavailable"] = true;
                    meta["remoteInspectionStopped"] = true; meta["requiresExplicitRebind"] = true;
                }
                meta["expectedObjectCount"] = names.Length;
                meta["selectionKind"] = "caller-defined exact-name selection";
                meta["nativeGroupVerified"] = false;
                if (string.IsNullOrEmpty(cursor)) trace.AddTo(meta);
                return "Read-only selection evidence. Follow nextCursor and retain all page Meta objects for CompareUnifiedGraphicSelections. Native editor groups remain unverified; no coordinate writes performed.";
            });
        private static IEnumerable<HmiObject> GuardGraphicSelection(IEnumerable<HmiObject> source, Action<Exception> error)
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
    }
}
