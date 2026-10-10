using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;

#if TIA_SHARED_ADAPTER_PATHS
using Native = TiaMcp.Adapters.WatchTechnologyPrimitives;
#else
using Native = TiaMcpServer.Siemens.LocalWatchTechnology.WatchTechnologyPrimitives;
#endif

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class TechnologyObjectsService
    {
        private readonly IEngineeringSession _session;

        public TechnologyObjectsService(IEngineeringSession session) => _session = session;

        #region software - TechnologyObjects

        // ── Technology Objects (TO) ──────────────────────────────────────────

        // Native observation: root-only enumeration omitted TOs in TechnologicalInstanceDBUserGroup folders.
        // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
        // Typed walk: TechnologicalInstanceDBGroup.TechnologicalObjects + Groups (recursive); folder = "" for the root.
        private static List<(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB To, string Folder)> EnumerateTechnologyObjectsRecursive(PlcSoftware plc)
        {
            var list = new List<(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB, string)>();
            void Walk(global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup group, string folder)
            {
                foreach (var to in EngineeringGroupOperations.Items(Native.Objects(group)).Cast<global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB>())
                    list.Add((to, folder));
                foreach (var sub in EngineeringGroupOperations.Items(Native.Groups(group)).Cast<global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBUserGroup>())
                    Walk(sub, folder.Length == 0 ? Native.Name(sub) : folder + "/" + Native.Name(sub));
            }
            Walk(Native.TechnologyGroup(plc), "");
            return list;
        }

        // Exact name, or folder path + name ("MCP_TO/MCP_PID"); a bare name that exists in several folders is ambiguous.
        private static global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDB? FindTechnologyObjectRecursive(PlcSoftware plc, string toName, out string? error)
        {
            error = null;
            var all = EnumerateTechnologyObjectsRecursive(plc);
            var text = (toName ?? "").Trim().Replace('\\', '/').Trim('/');
            var slash = text.LastIndexOf('/');
            var folder = slash < 0 ? null : text.Substring(0, slash);
            var name = slash < 0 ? text : text.Substring(slash + 1);
            var hits = all.Where(x => string.Equals(Native.Name(x.To), name, StringComparison.OrdinalIgnoreCase)
                && (folder == null || string.Equals(x.Folder, folder, StringComparison.OrdinalIgnoreCase))).ToList();
            if (hits.Count == 1) return hits[0].To;
            if (hits.Count > 1) { error = $"'{name}' exists in {hits.Count} folders ({string.Join(", ", hits.Select(h => h.Folder.Length == 0 ? "(root)" : h.Folder))}); give folder/name."; return null; }
            error = $"Technology object '{toName}' not found. Available: {string.Join(", ", all.Select(x => x.Folder.Length == 0 ? Native.Name(x.To) : x.Folder + "/" + Native.Name(x.To)).Take(30))}";
            return null;
        }

        private static object? ResolveTechnologyObjectCollection(PlcSoftware plc)
        {
            var group = TryGetPropertyValue(plc,
                "TechnologicalObjectGroup", "TechnologyObjectGroup",
                "TechnologicalObjects", "TechnologyObjects");
            if (group == null) return null;

            // If we landed on a group container, drill into the collection
            var col = TryGetPropertyValue(group,
                "TechnologicalObjects", "TechnologyObjects", "Instances", "Objects");
            return col ?? group; // group itself might already be enumerable
        }

        public List<JsonObject> GetTechnologyObjects(string softwarePath)
        {
            var result = new List<JsonObject>();
            // 未连接工程、路径不存在或枚举失败必须报错；空列表只表示已解析的 PLC 确实没有 TO。
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "ListTechnologyObjects: no project is open. Call ConnectPortal + OpenProject "
                    + "(or AttachOpenProject) first.");
            }

            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"ListTechnologyObjects: PLC software not found at '{softwarePath}'." + _session.AvailablePlcPathsSuffix());
            }

            try
            {
                foreach (var (item, folder) in EnumerateTechnologyObjectsRecursive(plc))
                {
                    var obj = new JsonObject();
                    foreach (var prop in new[] { "Name", "OfSystemLibElement", "OfSystemLibVersion" })
                    {
                        var val = TryGetPropertyValue(item, prop);
                        if (val != null) obj[prop] = JsonValue.Create(val.ToString());
                    }
                    // Try to get a "type" hint from class name as fallback
                    if (!obj.ContainsKey("OfSystemLibElement"))
                        obj["TypeHint"] = JsonValue.Create(item.GetType().Name);
                    obj["Folder"] = JsonValue.Create(folder);
                    result.Add(obj);
                }
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "ListTechnologyObjects failed for {SoftwarePath}", softwarePath);
                // 枚举失败时不得返回看似完整的部分列表。
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"ListTechnologyObjects failed halfway through '{softwarePath}': {ex.Message}. "
                    + "The list would have been INCOMPLETE, so it is not returned.", null, ex);
            }
            return result;
        }

        public ResponseMessage ExportTechnologyObject(string softwarePath, string toName, string exportPath, bool dryRun = false)
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open.", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "PROJECT_NOT_BOUND"), ("mayHaveChanged", false)) };
            var plc = _session.GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix(), Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };

            try
            {
                var to = FindTechnologyObjectRecursive(plc, toName, out var lookupError);
                if (to == null)
                    return new ResponseMessage { Message = $"Technology object '{toName}' not found in '{softwarePath}': {lookupError}", Meta = ResponseMeta.Unstamped(false, ("v4Rejection", "NOT_FOUND"), ("mayHaveChanged", false)) };

                TiaOpenness.Shared.NativeExportPolicy.RequireApply(to.IsConsistent ? "planned" : "inconsistent");
                exportPath = TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath);
                if (to.IsKnowHowProtected) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Know-how protected technology objects are outside this export scope.", "export-plan", false);
                if (dryRun) return new ResponseMessage { Message = "Technology-object export preview completed.",
                    Meta = ResponseMeta.Unstamped(true, ("exportPath", exportPath), ("toName", toName), ("executed", false)) };
                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                _session.TryExportEngineeringObject(to, exportPath, out var err);
                if (err != null)
                    return new ResponseMessage { Message = $"Export error: {err}" };

                return new ResponseMessage
                {
                    Message = $"Technology object '{toName}' exported to '{exportPath}'.",
                    Meta = ResponseMeta.Unstamped(true, ("exportPath", exportPath), ("toName", toName))
                };
            }
            catch (Exception ex)
            {
                if (ex is TiaMcp.Adapters.Contracts.AdapterPreconditionException) throw;
                _session.Logger?.LogError(ex, "ExportTechnologyObject failed");
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        #endregion

#if TIA_SHARED_ADAPTER_PATHS
        private TiaMcp.Adapters.PlcServices.WatchTechnologySurface? _watchTechnology;
        private bool IsProjectNull()
        {
            // Keep the engine's binding check before borrowing its current handle.
            if (_session.IsProjectNull()) return true;
            return (_watchTechnology ?? (_watchTechnology =
                TiaMcp.Adapters.PlcServices.Over(() => _session.CurrentProject!).WatchTechnology)).CurrentProject == null;
        }
#else
        private bool IsProjectNull() => _session.IsProjectNull();
#endif
    }
}
