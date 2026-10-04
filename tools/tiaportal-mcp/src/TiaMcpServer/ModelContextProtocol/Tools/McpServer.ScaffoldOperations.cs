using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        // Shared spec operations after the caller has opened or created its project.
        private static void ApplyScaffoldPlcElements(JsonNode root, string plcName, ResponseScaffold resp)
        {
            void Step(string name, string status, string? detail = null)
                => resp.Steps.Add(new ScaffoldStep { Step = name, Status = status, Detail = detail });
            JsonArray Arr(string key) => root[key] as JsonArray ?? new JsonArray();
            void BuildList(string key, string kind)
            {
                foreach (var item in Arr(key))
                {
                    try { PlcBuildAndImport(plcName, kind, item!.ToJsonString(), "", "", "", false, false); Step(kind, "ok"); }
                    catch (Exception ex) { Step(kind, "failed", ex.Message); resp.Ok = false; }
                }
            }
            BuildList("udt", "udt");
            BuildList("globalDb", "globaldb");
            BuildList("tagTable", "tagtable");

            foreach (var item in Arr("sclSourceFiles"))
            {
                string p; try { p = item?.GetValue<string>() ?? ""; } catch { p = ""; }
                if (string.IsNullOrWhiteSpace(p)) continue;
                var srcName = Path.GetFileName(p);
                try { ImportPlcExternalSource(plcName, "", p); GenerateBlocksFromExternalSource(plcName, srcName); Step("scl", "ok", srcName); }
                catch (Exception ex) { Step("scl", "failed", $"{srcName}: {ex.Message}"); resp.Ok = false; }
            }
        }

        private static void CompileScaffoldPlc(string plcName, ResponseScaffold resp)
        {
            void Step(string name, string status, string? detail = null)
                => resp.Steps.Add(new ScaffoldStep { Step = name, Status = status, Detail = detail });
            try
            {
                var c = CompileAndDiagnosePlc(plcName);
                resp.CompileState = c.State; resp.CompileErrorCount = c.ErrorCount; resp.CompileWarningCount = c.WarningCount;
                bool clean = (c.ErrorCount ?? 0) == 0;
                Step("compile", clean ? "ok" : "failed", $"state={c.State} errors={c.ErrorCount} warnings={c.WarningCount}");
                if (!clean) resp.Ok = false;
            }
            catch (Exception ex) { Step("compile", "failed", ex.Message); resp.Ok = false; }
        }

        private static void ApplyScaffoldHmi(JsonNode root, string plcName, string hmiName,
            string hmiSoftwarePathSpec, string connectionName, ResponseScaffold resp)
        {
            void Step(string name, string status, string? detail = null)
                => resp.Steps.Add(new ScaffoldStep { Step = name, Status = status, Detail = detail });
            JsonArray Arr(string key) => root[key] as JsonArray ?? new JsonArray();
            string IS(JsonNode? n, string key, string def = "") { try { return n?[key]?.GetValue<string>() ?? def; } catch { return def; } }
            uint IU(JsonNode? n, string key) { try { return (uint)(n?[key]?.GetValue<int>() ?? 0); } catch { return 0; } }
            string hmiPath = "";
            var candidates = new List<string> { hmiSoftwarePathSpec, "HMI_RT_1", hmiName + ".HMI_RT_1", hmiName + "_RT_1", hmiName };
            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                try { GetHmiProgramInfo(c); hmiPath = c; break; } catch { /* swallow(probe-optional): a missing HMI candidate permits the next exact candidate */ }
            }

            if (string.IsNullOrWhiteSpace(hmiPath))
            {
                Step("hmiResolve", "failed", $"could not resolve HMI software path (tried: {string.Join(", ", candidates.Where(x => !string.IsNullOrWhiteSpace(x)))})");
                resp.Ok = false;
            }
            else
            {
                Step("hmiResolve", "ok", hmiPath);
                try { EnsureUnifiedHmiConnection(hmiPath, connectionName, plcName); Step("hmiConnection", "ok"); }
                catch (Exception ex) { Step("hmiConnection", "failed", ex.Message); resp.Ok = false; }

                foreach (var item in Arr("hmiScreens"))
                {
                    var screenName = IS(item, "screenName");
                    if (string.IsNullOrWhiteSpace(screenName)) continue;
                    try
                    {
                        EnsureUnifiedHmiScreen(hmiPath, screenName, IU(item, "width"), IU(item, "height"));
                        var design = item?["designJson"];
                        if (design != null) ApplyUnifiedHmiScreenDesignJson(hmiPath, screenName, design.ToJsonString(), true);
                        Step("hmiScreen", "ok", screenName);
                    }
                    catch (Exception ex) { Step("hmiScreen", "failed", $"{screenName}: {ex.Message}"); resp.Ok = false; }
                }

                foreach (var item in Arr("hmiTags"))
                {
                    var tagName = IS(item, "tagName");
                    if (string.IsNullOrWhiteSpace(tagName)) continue;
                    var tagTable = IS(item, "tagTableName", "Default tag table");
                    var dt = IS(item, "hmiDataType", "Bool");
                    var plcTag = IS(item, "plcTag");
                    var address = IS(item, "address");
                    try { EnsureUnifiedHmiTag(hmiPath, tagTable, tagName, dt, plcName, plcTag, connectionName, address, true); Step("hmiTag", "ok", tagName); }
                    catch (Exception ex) { Step("hmiTag", "failed", $"{tagName}: {ex.Message}"); resp.Ok = false; }
                }
            }
        }
    }
}
