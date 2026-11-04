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

        private static JsonObject BuildHmiActionRecipe(JsonObject normalizedAction, JsonObject sourceAction)
        {
            var actionKind = sourceAction["ActionKind"]?.ToString() ?? normalizedAction["actionKind"]?.ToString() ?? "";
            var eventName = normalizedAction["event"]?.ToString() ?? "";
            var script = normalizedAction["script"]?.ToString() ?? "";
            var referencedTags = (normalizedAction["referencedTags"] as JsonArray ?? new JsonArray())
                .Select(x => x?.ToString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (string.IsNullOrWhiteSpace(actionKind))
            {
                actionKind = InferActionKindFromScript(script);
            }

            var recipeKind = NormalizeActionKind(actionKind);
            var targetScreen = sourceAction["TargetScreen"]?.ToString() ?? "";
            var targetPopup = sourceAction["TargetPopup"]?.ToString() ?? "";
            var explicitNavigationScript = !string.IsNullOrWhiteSpace(script);
            if ((recipeKind == "open-popup" || recipeKind == "goto-screen") && !explicitNavigationScript)
            {
                recipeKind = "project-binding-placeholder";
            }
            var writesPlcOrHmiTag = recipeKind is "set-bit" or "reset-bit" or "toggle-bit" or "set-value" or "confirm-write";
            var requiresConfirm = recipeKind == "confirm-write" || sourceAction["RequiresConfirm"]?.GetValue<bool?>() == true;
            var highRisk = recipeKind == "confirm-write" || recipeKind == "set-value";
            var status = "ready-for-generation";
            var warnings = new JsonArray();

            if (string.IsNullOrWhiteSpace(eventName))
            {
                status = "needs-template-fix";
                warnings.Add("Event is empty.");
            }

            if (writesPlcOrHmiTag && referencedTags.Length == 0)
            {
                status = "needs-template-fix";
                warnings.Add("Write action has no TargetTag/TargetTags/script tag reference.");
            }

            if (highRisk && !requiresConfirm)
            {
                status = "needs-confirmation-policy";
                warnings.Add("High-risk value write must require operator confirmation and range validation.");
            }

            return new JsonObject
            {
                ["recipeKind"] = recipeKind,
                ["actionKind"] = actionKind,
                ["event"] = eventName,
                ["targetTags"] = new JsonArray(referencedTags.Select(x => JsonValue.Create(x)).ToArray()),
                ["targetScreen"] = targetScreen,
                ["targetPopup"] = targetPopup,
                ["writesTag"] = writesPlcOrHmiTag,
                ["requiresOperatorConfirm"] = requiresConfirm,
                ["safetyLevel"] = recipeKind == "project-binding-placeholder" ? "structure-placeholder" : highRisk ? "high" : writesPlcOrHmiTag ? "command"
                    : "navigation",
                ["status"] = status,
                ["applyAsScript"] = recipeKind != "project-binding-placeholder",
                ["verificationRequired"] = new JsonArray(BuildActionVerificationSteps(recipeKind, referencedTags).Select(x => JsonValue.Create(x)).ToArray()),
                ["warnings"] = warnings
            };
        }

        private static JsonObject BuildActionRecipeSummary(JsonArray actions, HashSet<string> requiredTagNames, HashSet<string> templateObjectNames)
        {
            var recipes = new JsonArray();
            var ready = 0;
            var confirm = 0;
            var highRisk = 0;
            var missingRequiredTags = new JsonArray();
            var duplicateActions = new JsonArray();
            var missingTargets = new JsonArray();
            var seenActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var effectiveRecipes = new JsonArray();

            foreach (var actionNode in actions.OfType<JsonObject>())
            {
                var recipe = actionNode["recipe"] as JsonObject ?? new JsonObject();
                var row = recipe.DeepClone().AsObject();
                row["item"] = actionNode["item"]?.ToString() ?? "";
                row["event"] = actionNode["event"]?.ToString() ?? "";
                var actionKey = string.Join("|",
                    row["item"]?.ToString() ?? "",
                    row["event"]?.ToString() ?? "",
                    row["recipeKind"]?.ToString() ?? "",
                    string.Join(",", (row["targetTags"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "")),
                    row["targetScreen"]?.ToString() ?? "",
                    row["targetPopup"]?.ToString() ?? "");
                if (!seenActions.Add(actionKey))
                {
                    duplicateActions.Add(new JsonObject
                    {
                        ["item"] = row["item"]?.ToString() ?? "",
                        ["event"] = row["event"]?.ToString() ?? "",
                        ["recipeKind"] = row["recipeKind"]?.ToString() ?? ""
                    });
                }
                else
                {
                    effectiveRecipes.Add(row.DeepClone());
                }

                if (string.Equals(recipe["status"]?.ToString(), "ready-for-generation", StringComparison.OrdinalIgnoreCase))
                    ready++;
                if (recipe["requiresOperatorConfirm"]?.GetValue<bool>() == true)
                    confirm++;
                if (string.Equals(recipe["safetyLevel"]?.ToString(), "high", StringComparison.OrdinalIgnoreCase))
                    highRisk++;

                foreach (var tagNode in recipe["targetTags"] as JsonArray ?? new JsonArray())
                {
                    var tag = tagNode?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(tag) && !requiredTagNames.Contains(tag))
                    {
                        missingRequiredTags.Add(new JsonObject
                        {
                            ["item"] = row["item"]?.ToString() ?? "",
                            ["event"] = row["event"]?.ToString() ?? "",
                            ["tag"] = tag
                        });
                    }
                }

                var targetPopup = row["targetPopup"]?.ToString() ?? "";
                var targetScreen = row["targetScreen"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(targetPopup) && !templateObjectNames.Contains(targetPopup))
                {
                    missingTargets.Add(new JsonObject
                    {
                        ["item"] = row["item"]?.ToString() ?? "",
                        ["event"] = row["event"]?.ToString() ?? "",
                        ["targetType"] = "popup",
                        ["target"] = targetPopup
                    });
                }
                if (!string.IsNullOrWhiteSpace(targetScreen) && !templateObjectNames.Contains(targetScreen))
                {
                    missingTargets.Add(new JsonObject
                    {
                        ["item"] = row["item"]?.ToString() ?? "",
                        ["event"] = row["event"]?.ToString() ?? "",
                        ["targetType"] = "screen",
                        ["target"] = targetScreen
                    });
                }

                recipes.Add(row);
            }

            return new JsonObject
            {
                ["actionCount"] = actions.Count,
                ["readyForGeneration"] = ready,
                ["effectiveActionCount"] = effectiveRecipes.Count,
                ["requiresOperatorConfirm"] = confirm,
                ["highRiskWrites"] = highRisk,
                ["missingRequiredTags"] = missingRequiredTags,
                ["duplicateActions"] = duplicateActions,
                ["missingTargets"] = missingTargets,
                ["effectiveRecipes"] = effectiveRecipes,
                ["recipes"] = recipes
            };
        }

        private static HashSet<string> BuildTemplateObjectNameSet(JsonObject template, JsonArray items)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string value)
            {
                if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
            }

            Add(template["Screen"]?["Name"]?.ToString() ?? "");
            foreach (var item in items.OfType<JsonObject>())
            {
                Add(item["Name"]?.ToString() ?? "");
            }

            foreach (var component in template["Components"] as JsonArray ?? new JsonArray())
            {
                if (component is JsonObject obj)
                {
                    Add(obj["Name"]?.ToString() ?? "");
                }
            }

            return names;
        }

        private static string InferActionKindFromScript(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return "";
            if (Regex.IsMatch(script, @"SetBitInTag", RegexOptions.IgnoreCase)) return "SetBitInTag";
            if (Regex.IsMatch(script, @"ResetBitInTag", RegexOptions.IgnoreCase)) return "ResetBitInTag";
            if (Regex.IsMatch(script, @"ToggleBitInTag", RegexOptions.IgnoreCase)) return "ToggleBitInTag";
            if (Regex.IsMatch(script, @"SetTagValue|Write", RegexOptions.IgnoreCase)) return "SetValue";
            if (Regex.IsMatch(script, @"OpenPopup", RegexOptions.IgnoreCase)) return "OpenPopup";
            if (Regex.IsMatch(script, @"ChangeScreen|SetScreen", RegexOptions.IgnoreCase)) return "GotoScreen";
            return "Script";
        }

        private static string NormalizeActionKind(string actionKind)
        {
            if (string.IsNullOrWhiteSpace(actionKind)) return "unknown";
            var value = actionKind.Trim();
            if (value.Equals("SetBitInTag", StringComparison.OrdinalIgnoreCase)) return "set-bit";
            if (value.Equals("ResetBitInTag", StringComparison.OrdinalIgnoreCase)) return "reset-bit";
            if (value.Equals("ToggleBitInTag", StringComparison.OrdinalIgnoreCase)) return "toggle-bit";
            if (value.Equals("SetValue", StringComparison.OrdinalIgnoreCase) || value.Equals("SetTagValue", StringComparison.OrdinalIgnoreCase))
                return "set-value";
            if (value.Equals("ConfirmWrite", StringComparison.OrdinalIgnoreCase)) return "confirm-write";
            if (value.Equals("OpenPopup", StringComparison.OrdinalIgnoreCase)) return "open-popup";
            if (value.Equals("GotoScreen", StringComparison.OrdinalIgnoreCase) || value.Equals("ChangeScreen", StringComparison.OrdinalIgnoreCase) || value
                    .Equals("NavigateScreen", StringComparison.OrdinalIgnoreCase))
                return "goto-screen";
            return "script";
        }

        private static IEnumerable<string> BuildActionVerificationSteps(string recipeKind, string[] targetTags)
        {
            yield return "Read back HMI item and event name after template application.";
            if (targetTags.Length > 0)
            {
                yield return "Verify every referenced HMI tag exists before binding event script.";
                yield return "Verify every mapped PLC tag or DB member exists before template application.";
            }

            if (recipeKind is "set-bit" or "reset-bit" or "toggle-bit")
            {
                yield return "Run script syntax check and read back ScriptCode.";
            }
            else if (recipeKind is "set-value" or "confirm-write")
            {
                yield return "Require range validation, operator confirmation, script syntax check, and readback.";
            }
            else if (recipeKind is "open-popup" or "goto-screen")
            {
                yield return "Verify target popup/screen exists or is created in the same template transaction.";
            }
            else if (recipeKind == "project-binding-placeholder")
            {
                yield return "Treat this as a structural placeholder; do not generate ScriptCode until the target project provides a verified navigation or " +
                    "popup API.";
            }
        }

        private static void CollectDynamizations(JsonObject node, string itemName, JsonArray output)
        {
            foreach (var kv in node)
            {
                if (kv.Value is JsonObject child)
                {
                    if (kv.Key.Equals("Dynamizations", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var dyn in child)
                        {
                            output.Add(new JsonObject
                            {
                                ["item"] = itemName,
                                ["property"] = dyn.Key,
                                ["tag"] = dyn.Value?["Tag"]?.ToString() ?? "",
                                ["readOnly"] = dyn.Value?["ReadOnly"]?.ToString() ?? ""
                            });
                        }
                    }
                    else
                    {
                        CollectDynamizations(child, itemName, output);
                    }
                }
                else if (kv.Value is JsonArray array)
                {
                    foreach (var item in array.OfType<JsonObject>())
                    {
                        CollectDynamizations(item, itemName, output);
                    }
                }
            }
        }

        private static IEnumerable<string> ExtractRuntimeTags(string script)
        {
            var matches = Regex.Matches(script, @"Tags\.SysFct\.\w+\(\s*""([^""]+)""", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    yield return match.Groups[1].Value;
                }
            }
        }

        private static IEnumerable<string> ExtractActionTags(JsonObject action)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in ExtractRuntimeTags(action["Script"]?.ToString() ?? ""))
            {
                if (seen.Add(tag)) yield return tag;
            }

            var targetTag = action["TargetTag"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(targetTag) && seen.Add(targetTag))
            {
                yield return targetTag;
            }

            if (action["TargetTags"] is JsonArray targetTags)
            {
                foreach (var node in targetTags)
                {
                    var tag = node?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag))
                    {
                        yield return tag;
                    }
                }
            }
        }

        private static int CountOccurrences(string text, string pattern)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += pattern.Length;
            }
            return count;
        }
    }
}
