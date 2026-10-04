using ModelContextProtocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    // PatchProject — apply a spec to an EXISTING project (upsert), used by `tia patch`.
    // Mirrors ScaffoldProject's element handling but OPENS an existing project instead of
    // creating one and does not add hardware. Reuses the same engine statics, so behaviour
    // stays consistent with `tia gen`. Kept in a partial file so the McpServer god-file is
    // not touched.
    public static partial class McpServer
    {
        public static ResponseScaffold PatchProject(string spec, bool dryRun = false, bool noOverwrite = false)
        {
            var resp = new ResponseScaffold { Ok = true };
            void Step(string name, string status, string? detail = null)
                => resp.Steps.Add(new ScaffoldStep { Step = name, Status = status, Detail = detail });

            JsonNode root;
            try { root = JsonNode.Parse(spec) ?? throw new Exception("spec parsed to null"); }
            catch (Exception ex) { throw new McpException($"PatchProject: invalid spec JSON: {ex.Message}", McpErrorCode.InvalidParams); }

            string S(string key, string def = "") { try { return root[key]?.GetValue<string>() ?? def; } catch /* swallow(parse-fallback): a non-string spec field uses its existing default */ { return def; } }
            bool B(string key, bool def) { try { return root[key] is JsonNode n ? n.GetValue<bool>() : def; } catch /* swallow(parse-fallback): a non-boolean spec field uses its existing default */ { return def; } }
            JsonArray Arr(string key) => root[key] as JsonArray ?? new JsonArray();
            string IS(JsonNode? n, string key, string def = "") { try { return n?[key]?.GetValue<string>() ?? def; } catch /* swallow(parse-fallback): a non-string item field uses its existing default */ { return def; } }

            var projectPath = S("projectPath");
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new McpException("PatchProject: 'projectPath' is required (the .apXX to open)", McpErrorCode.InvalidParams);
            // Openness resolves relative paths against the exe dir; resolve against CWD so a
            // relative projectPath in the spec opens the project the user actually means.
            projectPath = Path.GetFullPath(projectPath);
            var plcName = S("plcName", "PLC_1");
            var ladOption = noOverwrite ? "None" : "Override";
            resp.ProjectName = Path.GetFileNameWithoutExtension(projectPath);
            resp.DirectoryPath = projectPath;

            // ---- dryRun: offline validation only ----
            if (dryRun)
            {
                bool projExists = File.Exists(projectPath);
                Step("projectPath", projExists ? "ok" : "failed", (projExists ? "exists: " : "MISSING: ") + projectPath);
                if (!projExists) resp.Ok = false;
                foreach (var pair in new[] { ("udt", "udt"), ("globalDb", "globaldb"), ("tagTable", "tagtable") })
                    foreach (var item in Arr(pair.Item1))
                    {
                        try { EngineServices.Get<PlcBuildTools>().PlcBuildAndImport(plcName, pair.Item2, item!.ToJsonString(), "", "", "", false, true); Step(pair.Item2, "ok", "dryRun: XML built"); }
                        catch (Exception ex) { Step(pair.Item2, "failed", ex.Message); resp.Ok = false; }
                    }
                foreach (var item in Arr("sclSourceFiles"))
                {
                    string p; try { p = item?.GetValue<string>() ?? ""; } catch /* swallow(parse-fallback): a non-string source path is treated as empty and skipped */ { p = ""; }
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    bool ex = File.Exists(p);
                    Step("scl", ex ? "ok" : "failed", (ex ? "exists: " : "MISSING: ") + p); if (!ex) resp.Ok = false;
                }
                foreach (var item in Arr("ladDocs"))
                {
                    var importPath = IS(item, "importPath"); var name = IS(item, "name");
                    bool ex = !string.IsNullOrWhiteSpace(importPath) && !string.IsNullOrWhiteSpace(name) && File.Exists(Path.Combine(importPath, name + ".s7dcl"));
                    Step("lad", ex ? "ok" : "failed", ex ? $"{name} (.s7dcl found)" : $"MISSING .s7dcl under {importPath} for {name}"); if (!ex) resp.Ok = false;
                }
                foreach (var item in Arr("hmiScreens"))
                {
                    var screenName = IS(item, "screenName");
                    bool ok = !string.IsNullOrWhiteSpace(screenName) && item?["designJson"] != null;
                    Step("hmiScreen", ok ? "ok" : "failed", ok ? screenName : "missing screenName/designJson"); if (!ok) resp.Ok = false;
                }
                var okN = resp.Steps.Count(s => s.Status == "ok");
                var failN = resp.Steps.Count(s => s.Status == "failed");
                resp.Message = $"PatchProject dryRun '{resp.ProjectName}': {okN} ok, {failN} failed (offline validation, nothing changed).";
                resp.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = resp.Ok, ["dryRun"] = true };
                return resp;
            }

            // ---- critical: connect + open existing project ----
            try
            {
                if (!EngineServices.Get<Siemens.Portal>().IsConnected()) { EngineServices.Get<SessionTools>().Connect(); Step("connect", "ok"); }
                else Step("connect", "skipped", "already connected");
            }
            catch (Exception ex) { Step("connect", "failed", ex.Message); resp.Ok = false; throw new McpException($"PatchProject aborted at connect: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            try { EngineServices.Get<ProjectSessionTools>().OpenProject(projectPath); Step("openProject", "ok", projectPath); }
            catch (Exception ex) { Step("openProject", "failed", ex.Message); resp.Ok = false; throw new McpException($"PatchProject aborted at openProject: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }

            // ---- PLC elements (per-item collect; re-import = upsert) ----
            ApplyScaffoldPlcElements(root, plcName, resp);

            foreach (var item in Arr("ladDocs"))
            {
                var importPath = IS(item, "importPath");
                var name = IS(item, "name");
                if (string.IsNullOrWhiteSpace(importPath) || string.IsNullOrWhiteSpace(name)) { Step("lad", "skipped", "missing importPath/name"); continue; }
                try { EngineServices.Get<DocumentsTools>().ImportFromDocuments(plcName, "", importPath, name, ladOption); Step("lad", "ok", name + (noOverwrite ? " (no-overwrite)" : "")); }
                catch (Exception ex) { Step("lad", "failed", $"{name}: {ex.Message}"); resp.Ok = false; }
            }

            // ---- compile ----
            if (B("compile", true)) CompileScaffoldPlc(plcName, resp);

            // ---- HMI connection / screens / tags (Ensure* are idempotent upserts) ----
            var hmiName = S("hmiName");
            if (!string.IsNullOrWhiteSpace(hmiName))
            {
                var connectionName = S("connectionName", "HMI_Connection_1");
                var hmiSoftwarePathSpec = S("hmiSoftwarePath");
                ApplyScaffoldHmi(root, plcName, hmiName, hmiSoftwarePathSpec, connectionName, resp);
            }

            // ---- save ----
            if (B("save", true))
            {
                try { EngineServices.Get<ProjectSessionTools>().SaveProject(); Step("save", "ok"); }
                catch (Exception ex) { Step("save", "failed", ex.Message); resp.Ok = false; }
            }

            var okCount = resp.Steps.Count(s => s.Status == "ok");
            var failCount = resp.Steps.Count(s => s.Status == "failed");
            resp.Message = $"PatchProject '{resp.ProjectName}': {okCount} ok, {failCount} failed; compile state={resp.CompileState ?? "(skipped)"} errors={resp.CompileErrorCount}.";
            resp.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = resp.Ok };
            return resp;
        }
    }
}
