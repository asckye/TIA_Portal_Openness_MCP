using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class HmiTemplateReferenceAnalyzer
    {
        public static JsonObject Analyze(string templateDirectory, string referenceProjectPath, string referenceGlobalLibraryPath)
        {
            var root = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["templateDirectory"] = templateDirectory,
                ["referenceProjectPath"] = referenceProjectPath,
                ["referenceGlobalLibraryPath"] = referenceGlobalLibraryPath,
                ["safetyPolicy"] = new JsonObject
                {
                    ["mode"] = "Offline template/reference analysis only.",
                    ["tia"] = "TIA Portal is not connected or opened by this analyzer.",
                    ["write"] = "No project, global library, HMI screen, watch table, or delivery package content is modified.",
                    ["binding"] = "A HMI tag binding is only considered ready when the PLC-side tag/member can be verified later in TIA."
                }
            };

            var templates = AnalyzeTemplates(templateDirectory);
            var referenceHints = AnalyzeReferenceHints(referenceProjectPath, referenceGlobalLibraryPath);
            root["templates"] = templates;
            root["referenceHints"] = referenceHints;
            root["readiness"] = BuildReadiness(templates, referenceHints);
            root["recommendations"] = BuildRecommendations(templates, referenceHints);
            root["ok"] = Directory.Exists(templateDirectory) && templates.Count > 0;
            return root;
        }

        public static string BuildMarkdown(JsonObject root, string jsonPath)
        {
            var md = new StringBuilder();
            md.AppendLine("# HMI Template Reference Analysis");
            md.AppendLine();
            md.AppendLine("Generated: " + root["timestamp"]);
            md.AppendLine("JSON: " + jsonPath);
            md.AppendLine();
            md.AppendLine("## Safety");
            md.AppendLine("- Offline analysis only; no TIA connection, no project write, no global-library import.");
            md.AppendLine("- HMI binding remains unverified until the PLC-side tag or DB member is read back from TIA.");
            md.AppendLine("- Delivery package is not modified by this report.");
            md.AppendLine();

            md.AppendLine("## Template Summary");
            if (root["templates"] is JsonArray templates)
            {
                foreach (var node in templates)
                {
                    var t = node as JsonObject;
                    md
                        .AppendLine("- " + t?["templateName"] + ": tags=" + t?["requiredTagCount"] + ", items=" + t?["itemCount"] + ", dynamizations=" + t
                            ?["dynamizationCount"] + ", actions=" + t?["actionCount"]);
                }
            }
            md.AppendLine();

            md.AppendLine("## Binding And Event Readiness");
            if (root["readiness"] is JsonArray readiness)
            {
                foreach (var node in readiness)
                {
                    var item = node as JsonObject;
                    md.AppendLine("- " + item?["templateName"] + ": " + item?["status"] + " - " + item?["detail"]);
                }
            }
            md.AppendLine();

            md.AppendLine("## HMI Action Recipes");
            if (root["templates"] is JsonArray actionTemplates)
            {
                foreach (var node in actionTemplates)
                {
                    if (node is not JsonObject t) continue;
                    var summary = t["actionRecipeSummary"] as JsonObject;
                    md
                        .AppendLine("- " + t["templateName"] + ": actions=" + summary?["actionCount"] + ", effective=" + summary?["effectiveActionCount"]
                            + ", ready=" + summary?["readyForGeneration"] + ", needsConfirm=" + summary?["requiresOperatorConfirm"] + ", highRisk=" + summary
                            ?["highRiskWrites"] + ", duplicates=" + ((summary?["duplicateActions"] as JsonArray)?.Count ?? 0) + ", missingTargets="
                            + ((summary?["missingTargets"] as JsonArray)?.Count ?? 0));
                    if (summary?["effectiveRecipes"] is JsonArray recipes)
                    {
                        foreach (var recipeNode in recipes.Take(8))
                        {
                            if (recipeNode is not JsonObject recipe) continue;
                            md
                                .AppendLine("  - " + recipe["item"] + "." + recipe["event"] + ": " + recipe["recipeKind"] + ", safety=" + recipe["safetyLevel"]
                                    + ", status=" + recipe["status"]);
                        }
                    }
                }
            }
            md.AppendLine();

            md.AppendLine("## Reference Signals");
            var hints = root["referenceHints"] as JsonObject;
            md.AppendLine("- HMI runtime exists: " + hints?["hmiRuntimeExists"]);
            md.AppendLine("- Screen RDF files: " + hints?["screenRdfCount"]);
            md.AppendLine("- Faceplate RDF files: " + hints?["faceplateRdfCount"]);
            md.AppendLine("- Global library exists: " + hints?["globalLibraryExists"]);
            md.AppendLine("- Global library `.al*` files: " + hints?["globalLibraryFileCount"]);
            md.AppendLine();

            md.AppendLine("## Recommendations");
            if (root["recommendations"] is JsonArray recs)
            {
                foreach (var rec in recs)
                {
                    md.AppendLine("- " + rec);
                }
            }

            return md.ToString();
        }

        private static JsonArray AnalyzeTemplates(string templateDirectory)
        {
            var result = new JsonArray();
            if (!Directory.Exists(templateDirectory)) return result;

            foreach (var file in Directory.EnumerateFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var json = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject;
                    if (json == null) continue;

                    var requiredTags = json["RequiredTags"] as JsonArray ?? new JsonArray();
                    var items = json["Items"] as JsonArray ?? new JsonArray();
                    var dyn = new JsonArray();
                    var actions = new JsonArray();

                    foreach (var itemNode in items)
                    {
                        if (itemNode is not JsonObject item) continue;
                        CollectDynamizations(item, item["Name"]?.ToString() ?? "", dyn);
                        if (item["Actions"] is JsonArray actionArray)
                        {
                            foreach (var actionNode in actionArray)
                            {
                                if (actionNode is JsonObject action)
                                {
                                    var itemName = item["Name"]?.ToString() ?? "";
                                    var eventName = action["Event"]?.ToString() ?? "";
                                    var script = action["Script"]?.ToString() ?? "";
                                    var actionJson = new JsonObject
                                    {
                                        ["item"] = itemName,
                                        ["event"] = eventName,
                                        ["script"] = script,
                                        ["referencedTags"] = new JsonArray(ExtractRuntimeTags(script).Select(x => JsonValue.Create(x)).ToArray())
                                    };
                                    actionJson["recipe"] = BuildHmiActionRecipe(actionJson, action);
                                    actions.Add(actionJson);
                                }
                            }
                        }
                    }

                    if (json["Events"] is JsonArray eventArray)
                    {
                        foreach (var eventNode in eventArray)
                        {
                            if (eventNode is not JsonObject action) continue;
                            var actionJson = new JsonObject
                            {
                                ["item"] = action["Item"]?.ToString() ?? "",
                                ["event"] = action["Event"]?.ToString() ?? "",
                                ["actionKind"] = action["ActionKind"]?.ToString() ?? "",
                                ["targetTag"] = action["TargetTag"]?.ToString() ?? "",
                                ["targetScreen"] = action["TargetScreen"]?.ToString() ?? "",
                                ["targetPopup"] = action["TargetPopup"]?.ToString() ?? "",
                                ["script"] = action["Script"]?.ToString() ?? "",
                                ["referencedTags"] = new JsonArray(ExtractActionTags(action).Select(x => JsonValue.Create(x)).ToArray())
                            };
                            actionJson["recipe"] = BuildHmiActionRecipe(actionJson, action);
                            actions.Add(actionJson);
                        }
                    }

                    var tagNames = requiredTags
                        .OfType<JsonObject>()
                        .Select(x => x["Name"]?.ToString() ?? "")
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var templateObjectNames = BuildTemplateObjectNameSet(json, items);
                    var actionTags = actions
                        .OfType<JsonObject>()
                        .SelectMany(x => (x["referencedTags"] as JsonArray ?? new JsonArray()).Select(y => y?.ToString() ?? ""))
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var missingActionTags = actionTags.Where(x => !tagNames.Contains(x)).ToArray();
                    var actionRecipeSummary = BuildActionRecipeSummary(actions, tagNames, templateObjectNames);

                    result.Add(new JsonObject
                    {
                        ["file"] = file,
                        ["templateName"] = json["TemplateName"]?.ToString() ?? Path.GetFileNameWithoutExtension(file),
                        ["format"] = json["Format"]?.ToString() ?? "",
                        ["purpose"] = json["Purpose"]?.ToString() ?? "",
                        ["screenName"] = json["Screen"]?["Name"]?.ToString() ?? "",
                        ["requiredTagCount"] = requiredTags.Count,
                        ["itemCount"] = items.Count,
                        ["dynamizationCount"] = dyn.Count,
                        ["actionCount"] = actions.Count,
                        ["requiredTags"] = CloneArray(requiredTags),
                        ["dynamizations"] = dyn,
                        ["actions"] = actions,
                        ["actionRecipeSummary"] = actionRecipeSummary,
                        ["missingRequiredTagsForActions"] = new JsonArray(missingActionTags.Select(x => JsonValue.Create(x)).ToArray()),
                        ["bindingPolicy"] =
                            "RequiredTags.Name must exist as HMI tag, RequiredTags.PlcTag must exist as PLC tag or DB member before template application is " +
                            "treated as verified."
                    });
                }
                catch (Exception ex)
                {
                    result.Add(new JsonObject
                    {
                        ["file"] = file,
                        ["error"] = ex.Message,
                        ["ok"] = false
                    });
                }
            }

            return result;
        }

        private static JsonObject AnalyzeReferenceHints(string referenceProjectPath, string referenceGlobalLibraryPath)
        {
            var runtimeRoot = Directory.Exists(referenceProjectPath)
                ? Directory.EnumerateDirectories(referenceProjectPath, "currentConfiguration", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            var screenDir = runtimeRoot == null ? "" : Path.Combine(runtimeRoot, "screens");
            var faceplateDir = runtimeRoot == null ? "" : Path.Combine(runtimeRoot, "faceplates");
            var libraryRoot = string.IsNullOrWhiteSpace(referenceGlobalLibraryPath)
                ? ""
                : Directory.Exists(referenceGlobalLibraryPath)
                ? referenceGlobalLibraryPath
                : Path.GetDirectoryName(referenceGlobalLibraryPath) ?? referenceGlobalLibraryPath;

            var sampleStrings = new JsonArray();
            if (!string.IsNullOrWhiteSpace(runtimeRoot) && Directory.Exists(runtimeRoot))
            {
                foreach (var file in Directory.EnumerateFiles(runtimeRoot, "*.rdf", SearchOption.AllDirectories).Take(80))
                {
                    try
                    {
                        var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                        foreach (var pattern in new[] { "Faceplate", "Screen", "Button", "IO", "Tag", "Alarm", "Trend", "Recipe" })
                        {
                            var count = CountOccurrences(text, pattern);
                            if (count > 0 && sampleStrings.Count < 80)
                            {
                                sampleStrings.Add(new JsonObject
                                {
                                    ["file"] = file,
                                    ["pattern"] = pattern,
                                    ["count"] = count
                                });
                            }
                        }
                    }
                    catch /* swallow(probe-optional): RDF 扫描仅提供离线参考线索，单个样本不可读时继续扫描 */
                    {
                        // RDF内容可能是二进制或局部编码，离线学习只记录可读线索。
                    }
                }
            }

            return new JsonObject
            {
                ["hmiRuntimeExists"] = runtimeRoot != null,
                ["runtimeRoot"] = runtimeRoot ?? "",
                ["screenRdfCount"] = Directory.Exists(screenDir) ? Directory.EnumerateFiles(screenDir, "*.rdf").Count() : 0,
                ["faceplateRdfCount"] = Directory.Exists(faceplateDir) ? Directory.EnumerateFiles(faceplateDir, "*.rdf").Count() : 0,
                ["globalLibraryExists"] = !string.IsNullOrWhiteSpace(libraryRoot) && (Directory.Exists(libraryRoot) || File.Exists(referenceGlobalLibraryPath)),
                ["globalLibraryFileCount"] = Directory.Exists(libraryRoot) ? Directory.EnumerateFiles(libraryRoot, "*.al*", SearchOption.TopDirectoryOnly)
                    .Count() : 0,
                ["sampleRuntimeStringHints"] = sampleStrings
            };
        }

        private static JsonArray BuildReadiness(JsonArray templates, JsonObject referenceHints)
        {
            var list = new JsonArray();
            foreach (var node in templates)
            {
                if (node is not JsonObject t) continue;
                var missingActionTags = t["missingRequiredTagsForActions"] as JsonArray ?? new JsonArray();
                var requiredTagCount = t["requiredTagCount"]?.GetValue<int>() ?? 0;
                var actionCount = t["actionCount"]?.GetValue<int>() ?? 0;
                var dynCount = t["dynamizationCount"]?.GetValue<int>() ?? 0;
                var status = missingActionTags.Count == 0 && requiredTagCount > 0 ? "ready-for-tia-validation" : "needs-template-fix";
                var detail = missingActionTags.Count == 0
                    ? $"Template has {requiredTagCount} required tags, {dynCount} dynamic bindings, and {actionCount} event actions. Next validation must verify PLC-side tags in TIA before applying."
                    : "Action scripts reference tags not listed in RequiredTags: " + string.Join(", ", missingActionTags.Select(x => x?.ToString()));
                list.Add(new JsonObject
                {
                    ["templateName"] = t["templateName"]?.ToString() ?? "",
                    ["status"] = status,
                    ["detail"] = detail
                });
            }

            if (referenceHints["hmiRuntimeExists"]?.GetValue<bool>() != true)
            {
                list.Add(new JsonObject
                {
                    ["templateName"] = "reference-project",
                    ["status"] = "blocked",
                    ["detail"] = "Reference HMI runtime currentConfiguration was not found."
                });
            }

            return list;
        }

        private static JsonArray BuildRecommendations(JsonArray templates, JsonObject referenceHints)
        {
            var list = new JsonArray
            {
                "HMI模板应用前必须先从TIA读回PLC变量表/DB成员，逐项匹配RequiredTags.PlcTag；未匹配则拒绝绑定。",
                "按钮、输入框、状态文本应分开验证：按钮检查事件脚本引用的HMI变量，IOField检查ProcessValue绑定，状态文本检查Visible/颜色等动态化。",
                "参考项目runtime RDF只用于学习画面规模、faceplate数量和控件关键词，不直接复制为模板，避免不可维护和版本耦合。",
                "全局库对象级复用下一步走ProbeGlobalLibrary只读读回，再在临时项目验证导入MasterCopy/Type，不直接对真实项目导入。"
            };

            if ((referenceHints["screenRdfCount"]?.GetValue<int>() ?? 0) > 0)
            {
                list.Add("参考项目包含大量screen RDF，建议把常见页面拆成总览、设备单元、PID面板、报警/趋势、维护诊断五类模板。");
            }

            if ((referenceHints["faceplateRdfCount"]?.GetValue<int>() ?? 0) > 0)
            {
                list.Add("参考项目包含faceplate RDF，HMI模板体系应优先补充faceplate级别的参数接口和实例化规则。");
            }

            if (templates.Count > 0)
            {
                list.Add("当前JSON模板已有RequiredTags/Dynamizations/Actions骨架，可以作为商业版模板契约；下一步应增加版本号、控件能力矩阵、PLC同步预检报告。");
            }

            return list;
        }

        private static JsonArray CloneArray(JsonArray array)
        {
            return JsonNode.Parse(array.ToJsonString(new JsonSerializerOptions { WriteIndented = false })) as JsonArray ?? new JsonArray();
        }
    }
}
