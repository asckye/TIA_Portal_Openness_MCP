using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.Siemens;
namespace TiaMcp.Engine.Tests
{
    // 下载/上载提示应答策略：UserManagementDownload 曾被当成复选框处理而实际是选择型（V20/V21 均如此），
    // 这些用例保证每种提示形态都按真实形态应答、破坏性提示默认不动、密码绝不落入记录。
    internal static class DownloadPromptPolicyTests
    {
        internal static void Run(Action<bool,string> check)
        {
            bool Fails(Action action) { try { action(); return false; } catch { return true; } }
            var userMgmt = new[] { "DownloadAllUserManagementDataResetToProject", "KeepOnlineUserManagementData", "UpdateUserManagementDataButKeepOnlinePassword" };
            var none = Array.Empty<string>();

            var policy = new DownloadPromptPolicy();
            var a = policy.Decide("UserManagementDownload", userMgmt, false, false);
            check(a.Kind == DownloadPromptPolicy.AnswerKind.Selection && a.Value == "KeepOnlineUserManagementData", "UserManagementDownload is answered as a selection with the keep default");
            policy.UserManagementMode = "resetToProject";
            check(policy.Decide("UserManagementDownload", userMgmt, false, false).Value == "DownloadAllUserManagementDataResetToProject", "userManagementMode resetToProject maps to the native enum name");
            policy.UserManagementMode = "updateKeepPassword";
            check(policy.Decide("UserManagementDownload", userMgmt, false, false).Value == "UpdateUserManagementDataButKeepOnlinePassword", "userManagementMode updateKeepPassword maps to the native enum name");
            check(Fails(() => DownloadPromptPolicy.UserManagementSelection("bogus")), "unknown userManagementMode is rejected");

            var p2 = new DownloadPromptPolicy();
            check(p2.Decide("AlarmTextLibrariesDownload", new[] { "ConsistentDownload", "NoAction" }, false, false).Value == "ConsistentDownload", "AlarmTextLibrariesDownload (selection type, formerly mishandled as checkbox) defaults to ConsistentDownload");
            check(p2.Decide("StopModules", new[] { "NoAction", "StopAll" }, false, false).Value == "StopAll", "StopModules honours stopBeforeDownload=true");
            p2.StopBeforeDownload = false;
            check(p2.Decide("StopModules", new[] { "NoAction", "StopAll" }, false, false).Value == "NoAction", "StopModules honours stopBeforeDownload=false");
            check(p2.Decide("DataBlockReinitializationOrKeepActualValues", new[] { "KeepActualValues", "StopPlcAndReinitialize" }, false, false).Value == "KeepActualValues", "V21 DB reinitialization prompt keeps actual values by default");
            p2.KeepActualValues = false;
            var noStop = p2.Decide("DataBlockReinitializationOrKeepActualValues", new[] { "KeepActualValues", "NoAction", "StopPlcAndReinitialize" }, false, false);
            check(noStop.Value == "NoAction" && noStop.Source == "conservative" && noStop.Note!.Contains("stopBeforeDownload=true"), "V21 DB reinitialization with keepActualValues=false but stopBeforeDownload=false answers NoAction and names stopBeforeDownload");
            p2.StopBeforeDownload = true;
            check(p2.Decide("DataBlockReinitializationOrKeepActualValues", new[] { "KeepActualValues", "StopPlcAndReinitialize" }, false, false).Value == "StopPlcAndReinitialize", "keepActualValues=false reinitializes");

            foreach (var destructive in new[] { "ResetModule", "InitializeMemory", "OverwriteOnMemoryCard", "OverwriteSystemData", "SwitchBackupToPrimary" })
            {
                var d = new DownloadPromptPolicy().Decide(destructive, new[] { "NoAction", "DeleteAll" }, false, false);
                check(d.Kind == DownloadPromptPolicy.AnswerKind.Selection && d.Value == "NoAction" && d.Source == "conservative", destructive + " defaults to NoAction with a conservative note");
            }
            check(new DownloadPromptPolicy().Decide("ProtectionLevelChanged", new[] { "ContinueDownloading", "NoChange" }, false, false).Value == "NoChange", "ProtectionLevelChanged keeps the protection level by default");
            check(new DownloadPromptPolicy().Decide("TargetForSoftware", new[] { "CPU", "PlcSimulationAdvanced" }, false, false).Value == "CPU", "TargetForSoftware defaults to the real CPU");
            check(new DownloadPromptPolicy { AllowSafetyProgramDownload = true }.Decide("SafetyProgram", new[] { "ConsistentDownload" }, false, false).Value == "ConsistentDownload", "Safety.Download.Configurations.SafetyProgram is answered with its only selection, ConsistentDownload, when allowSafetyProgramDownload=true");

            var explicitPolicy = new DownloadPromptPolicy();
            foreach (var kv in DownloadPromptPolicy.ParseExplicitAnswers("{\"ResetModule\":\"deleteall\",\"OverwriteHmiData\":true}")) explicitPolicy.Explicit[kv.Key] = kv.Value;
            var e1 = explicitPolicy.Decide("ResetModule", new[] { "NoAction", "DeleteAll" }, false, false);
            check(e1.Source == "explicit" && e1.Value == "DeleteAll", "explicit answer overrides the conservative default and is normalised to the native enum casing");
            var e2 = explicitPolicy.Decide("OverwriteHmiData", none, true, false);
            check(e2.Kind == DownloadPromptPolicy.AnswerKind.Checked && e2.Value == "true", "explicit boolean answers checkbox prompts");
            var fallback = explicitPolicy.Decide("StopModules", new[] { "NoAction", "StopAll" }, false, false);
            check(fallback.Source == "builtin" && fallback.Value == "StopAll", "sentinel: a prompt absent from the explicit map still uses its built-in default");
            var bad = new DownloadPromptPolicy(); bad.Explicit["ResetModule"] = "Explode";
            check(Fails(() => bad.Decide("ResetModule", new[] { "NoAction", "DeleteAll" }, false, false)), "explicit value outside the enum is rejected");
            check(Fails(() => DownloadPromptPolicy.ParseExplicitAnswers("[1,2]")), "promptAnswersJson must be an object");
            check(Fails(() => DownloadPromptPolicy.ParseExplicitAnswers("{\"Reset Module\":\"x\"}")), "prompt names must be identifiers");
            check(DownloadPromptPolicy.ParseExplicitAnswers("{}").Count == 0 && DownloadPromptPolicy.ParseExplicitAnswers("").Count == 0, "empty answers parse to nothing");
            var pwdExplicit = new DownloadPromptPolicy(); pwdExplicit.Explicit["PlcMasterSecretPassword"] = "secret";
            check(Fails(() => pwdExplicit.Decide("PlcMasterSecretPassword", none, false, true)), "passwords are refused inside promptAnswersJson");

            var noSecret = new DownloadPromptPolicy();
            var u = noSecret.Decide("ModuleReadAccessPassword", none, false, true);
            check(u.Kind == DownloadPromptPolicy.AnswerKind.Unanswered, "password prompt without a password stays unanswered");
            var withSecret = new DownloadPromptPolicy { ModuleAccessPassword = "s3cret", BlockBindingPassword = "b", MasterSecretPassword = "m" };
            foreach (var t in new[] { "ModuleReadAccessPassword", "ModuleWriteAccessPassword", "PasswordReadAccess", "BlockBindingPassword", "PlcMasterSecretPassword" })
            {
                var ans = withSecret.Decide(t, none, false, true);
                check(ans.Kind == DownloadPromptPolicy.AnswerKind.Password && ans.Value == null, t + " is answered with a password that never appears in the answer");
            }
            withSecret.Record("PlcMasterSecretPassword", "Enter master secret", withSecret.Decide("PlcMasterSecretPassword", none, false, true));
            var summary = withSecret.Summary().ToJsonString();
            check(!summary.Contains("s3cret") && !summary.Contains("\"m\"") && summary.Contains("Password"), "recorded answers never contain the secret");

            var unknown = new DownloadPromptPolicy().Decide("SelectiveDeleteDownload", new[] { "AcceptAll", "DeleteAll", "DeleteSelected" }, false, false);
            check(unknown.Kind == DownloadPromptPolicy.AnswerKind.Unanswered && unknown.Note != null && unknown.Note.Contains("AcceptAll"), "prompts without a safe default stay unanswered and list the allowed values");
            var chk = new DownloadPromptPolicy().Decide("UpgradeTargetDevice", none, true, false);
            check(chk.Kind == DownloadPromptPolicy.AnswerKind.Unanswered, "firmware upgrade checkbox is never answered implicitly");
            check(new DownloadPromptPolicy().Decide("DeleteWebApplication", none, true, false).Value == "false", "web application deletion checkbox defaults to false");
            var rec = new DownloadPromptPolicy();
            rec.Record("ResetModule", "Reset memory?", rec.Decide("ResetModule", new[] { "NoAction", "DeleteAll" }, false, false));
            rec.Record("SelectiveDeleteDownload", "Delete objects?", rec.Decide("SelectiveDeleteDownload", new[] { "AcceptAll" }, false, false));
            check(rec.Answered.Count == 1 && rec.Unanswered.Count == 1 && rec.UnansweredSummary().Contains("SelectiveDeleteDownload") && rec.Summary()["promptsUnanswered"]!.AsArray().Count == 1, "summary separates answered and unanswered prompts with the TIA message");

            DataBlockReinitialization(check);
            OptInPrompts(check, Fails);
            RequestPolicies(check, Fails);
            ApprovalPlans(check);
        }

        // V17-V21 DataBlockReinitializationSelections has exactly NoAction and StopPlcAndReinitialize (SDK XML of every release that
        // has the prompt); the former KeepActualValues/Reinitialize names never matched, so the prompt was always unanswered.
        private static void DataBlockReinitialization(Action<bool, string> check)
        {
            var values = new[] { "NoAction", "StopPlcAndReinitialize" };
            foreach (var release in new[] { "17", "18", "19", "20", "21" })
            {
                var keep = new DownloadPromptPolicy().Decide("DataBlockReinitialization", values, false, false);
                check(keep.Kind == DownloadPromptPolicy.AnswerKind.Selection && keep.Value == "NoAction" && keep.Note!.Contains("aborts"), release + " DataBlockReinitialization: keepActualValues=true answers NoAction and says TIA aborts");
                var reset = new DownloadPromptPolicy { KeepActualValues = false }.Decide("DataBlockReinitialization", values, false, false);
                check(reset.Kind == DownloadPromptPolicy.AnswerKind.Selection && reset.Value == "StopPlcAndReinitialize", release + " DataBlockReinitialization: keepActualValues=false with stopBeforeDownload=true answers StopPlcAndReinitialize");
                var noStop = new DownloadPromptPolicy { KeepActualValues = false, StopBeforeDownload = false }.Decide("DataBlockReinitialization", values, false, false);
                check(noStop.Value == "NoAction" && noStop.Source == "conservative" && noStop.Note!.Contains("STOP"), release + " DataBlockReinitialization: reinitialization without STOP is refused with NoAction");
            }
            var stopped = new DownloadPromptPolicy { KeepActualValues = false, StopBeforeDownload = false };
            stopped.Record("DataBlockReinitializationOrKeepActualValues", "Reinitialize the data blocks?", stopped.Decide("DataBlockReinitializationOrKeepActualValues", new[] { "KeepActualValues", "NoAction", "StopPlcAndReinitialize" }, false, false));
            check(stopped.ReinitializationSummary().Contains("DataBlockReinitializationOrKeepActualValues answered NoAction") && stopped.ReinitializationSummary().Contains("stopBeforeDownload=true"),
                "the result names the reinitialization prompt that stops the download and the parameter that allows it");
            var kept = new DownloadPromptPolicy();
            kept.Record("DataBlockReinitializationOrKeepActualValues", null, kept.Decide("DataBlockReinitializationOrKeepActualValues", new[] { "KeepActualValues", "NoAction", "StopPlcAndReinitialize" }, false, false));
            check(kept.ReinitializationSummary().Length == 0, "keeping actual values on V21 adds no reinitialization stop to the result");
            var legacyNames = new DownloadPromptPolicy { KeepActualValues = false }.Decide("DataBlockReinitialization", new[] { "KeepActualValues", "Reinitialize" }, false, false);
            check(legacyNames.Kind == DownloadPromptPolicy.AnswerKind.Unanswered, "a DataBlockReinitialization enum without the documented names stays unanswered instead of guessing");
        }

        private static void OptInPrompts(Action<bool, string> check, Func<Action, bool> fails)
        {
            var prompts = new (string Type, string[] Values, string Allow, string? Refuse, string Parameter)[]
            {
                ("SafetyProgram", new[] { "ConsistentDownload" }, "ConsistentDownload", null, "allowSafetyProgramDownload"),
                ("DifferentTargetConfiguration", new[] { "AcceptAll", "NoAction" }, "AcceptAll", "NoAction", "allowHardwareDifferences"),
                ("ActiveTestCanBeAborted", new[] { "AcceptAll", "NoAction" }, "AcceptAll", "NoAction", "allowActiveTestCancel"),
                ("ActiveTestCanPreventDownload", new[] { "AcceptAll", "NoAction" }, "AcceptAll", "NoAction", "allowActiveTestCancel"),
                ("ExpandDownload", new[] { "Download", "NoAction" }, "Download", "NoAction", "allowExpandedDownload"),
                ("LoadIdentificationData", new[] { "LoadData", "LoadNothing" }, "LoadData", "LoadNothing", "allowIdentificationDataLoad"),
            };
            foreach (var prompt in prompts)
            {
                var refused = new DownloadPromptPolicy().Decide(prompt.Type, prompt.Values, false, false);
                check(refused.Kind == DownloadPromptPolicy.AnswerKind.Refused && refused.Value == prompt.Refuse && refused.AllowWith == prompt.Parameter,
                    prompt.Type + " is refused by default, with its documented no-selection and the parameter that allows it");
                var policy = new DownloadPromptPolicy();
                switch (prompt.Parameter)
                {
                    case "allowSafetyProgramDownload": policy.AllowSafetyProgramDownload = true; break;
                    case "allowHardwareDifferences": policy.AllowHardwareDifferences = true; break;
                    case "allowActiveTestCancel": policy.AllowActiveTestCancel = true; break;
                    case "allowExpandedDownload": policy.AllowExpandedDownload = true; break;
                    default: policy.AllowIdentificationDataLoad = true; break;
                }
                var allowed = policy.Decide(prompt.Type, prompt.Values, false, false);
                check(allowed.Kind == DownloadPromptPolicy.AnswerKind.Selection && allowed.Value == prompt.Allow && allowed.Source == "allowed", prompt.Type + " is answered " + prompt.Allow + " only when " + prompt.Parameter + "=true");
                var other = new DownloadPromptPolicy { AllowSafetyProgramDownload = prompt.Parameter != "allowSafetyProgramDownload", AllowHardwareDifferences = prompt.Parameter != "allowHardwareDifferences",
                    AllowActiveTestCancel = prompt.Parameter != "allowActiveTestCancel", AllowExpandedDownload = prompt.Parameter != "allowExpandedDownload", AllowIdentificationDataLoad = prompt.Parameter != "allowIdentificationDataLoad" };
                check(other.Decide(prompt.Type, prompt.Values, false, false).Kind == DownloadPromptPolicy.AnswerKind.Refused, prompt.Type + " is not allowed by the other opt-in parameters");
                var smuggled = new DownloadPromptPolicy(); smuggled.Explicit[prompt.Type] = prompt.Allow;
                check(fails(() => smuggled.Decide(prompt.Type, prompt.Values, false, false)), prompt.Type + " cannot be answered through promptAnswers");
            }
            var recorder = new DownloadPromptPolicy();
            recorder.Record("ExpandDownload", "Expand the download?", recorder.Decide("ExpandDownload", new[] { "Download", "NoAction" }, false, false));
            check(recorder.Refused.Count == 1 && recorder.Answered.Count == 0 && recorder.Unanswered.Count == 0, "a refused prompt is recorded only as refused");
            check(recorder.RefusedSummary().Contains("ExpandDownload") && recorder.RefusedSummary().Contains("allowExpandedDownload=true"), "the result names the refused prompt and the parameter that allows it");
            var refusedRow = recorder.Summary()["promptsRefused"]!.AsArray()[0]!;
            check((string?)refusedRow["prompt"] == "ExpandDownload" && (string?)refusedRow["allowWith"] == "allowExpandedDownload" && (string?)refusedRow["message"] == "Expand the download?", "meta.promptsRefused keeps the TIA prompt text and the allowing parameter");
            check(DownloadPromptPolicy.NonTlsLoginRefusal(false, false) != null && DownloadPromptPolicy.NonTlsLoginRefusal(false, false)!.Contains("allowNonTlsLogin"), "a password is not sent when IsSecureCommunication=false and the legacy channel is not allowed");
            check(DownloadPromptPolicy.NonTlsLoginRefusal(false, true) == null && DownloadPromptPolicy.NonTlsLoginRefusal(true, false) == null && DownloadPromptPolicy.NonTlsLoginRefusal(null, false) == null,
                "a password is sent over TLS, when the legacy channel is allowed, or where the release cannot report IsSecureCommunication");
        }

        private static void RequestPolicies(Action<bool, string> check, Func<Action, bool> fails)
        {
            var none = new Dictionary<string, string>();
            var download = DownloadPromptPolicy.ForRequest(new TiaMcp.Adapters.Contracts.OnlineRequest { Operation = "DownloadPlc", KeepActualValues = false, StopBeforeDownload = false,
                Password = "p", AllowExpandedDownload = true, AllowNonTlsLogin = true, UserManagementMode = "resetToProject" }, none);
            check(!download.KeepActualValues && !download.StopBeforeDownload && download.StartAfterDownload && download.ModuleAccessPassword == "p" && download.AllowExpandedDownload
                && !download.AllowSafetyProgramDownload && download.AllowNonTlsLogin && download.UserManagementMode == "resetToProject", "DownloadPlc maps every request option into the prompt policy");
            var folder = DownloadPromptPolicy.ForRequest(new TiaMcp.Adapters.Contracts.OnlineRequest { Operation = "DownloadPlcToFolder", TargetForSoftware = "PlcSimulationAdvanced", OverwriteOnMemoryCard = true,
                AllowSafetyProgramDownload = true, AllowHardwareDifferences = true, Password = "p" }, none);
            check(folder.Explicit["TargetForSoftware"] == "PlcSimulationAdvanced" && folder.Explicit["OverwriteOnMemoryCard"] == "Load" && !folder.StopBeforeDownload && !folder.StartAfterDownload
                && folder.AllowSafetyProgramDownload && !folder.AllowHardwareDifferences && folder.ModuleAccessPassword == null, "the folder download answers its own prompts and never the device-only opt-ins");
            var upload = DownloadPromptPolicy.ForRequest(new TiaMcp.Adapters.Contracts.OnlineRequest { Operation = "UploadStationFromPlc", Password = "p", KeepActualValues = false, AllowSafetyProgramDownload = true }, none);
            check(upload.KeepActualValues && upload.ModuleAccessPassword == "p" && !upload.AllowSafetyProgramDownload && upload.UserManagementMode == "keep", "uploads keep the defaults and only use the CPU password");
            check(fails(() => DownloadPromptPolicy.ForRequest(new TiaMcp.Adapters.Contracts.OnlineRequest { Operation = "DownloadPlc" }, new Dictionary<string, string> { ["safetyprogram"] = "ConsistentDownload" })),
                "promptAnswers for an opt-in prompt is refused before the download, case-insensitively");
            check(fails(() => DownloadPromptPolicy.ForRequest(new TiaMcp.Adapters.Contracts.OnlineRequest { Operation = "DownloadPlc", UserManagementMode = "update" }, none)), "an unknown userManagementMode is refused before the download");
        }

        private static void ApprovalPlans(Action<bool, string> check)
        {
            var plan = DownloadPromptPolicy.ApprovalPlan("DownloadPlc", new System.Text.Json.Nodes.JsonObject())!;
            var answers = plan["answers"]!.AsObject();
            check((string?)answers["StopModules"]!["answer"] == "StopAll" && (string?)answers["StartModules"]!["answer"] == "StartModule" && (string?)answers["DataBlockReinitialization"]!["answer"] == "NoAction"
                && (string?)answers["ConsistentBlocksDownload"]!["answer"] == "ConsistentDownload" && answers["AllBlocksDownload"]!["answer"] == null && (string?)answers["UserManagementDownload"]!["answer"] == "KeepOnlineUserManagementData",
                "the DownloadPlc approval plan shows the default answers");
            foreach (var type in DownloadPromptPolicy.OptInPrompts)
                check((string?)answers[type]!["source"] == "refused" && (string?)answers[type]!["allowWith"] == DownloadPromptPolicy.OptInParameter(type), "the approval plan shows " + type + " as refused with its parameter");
            check(plan["loginPrompts"]!.AsArray().Any(l => (string?)l!["prompt"] == "ModuleReadAccessPassword" && !(bool)l["answered"]!), "the approval plan shows a password prompt without a password as unanswered");
            var custom = DownloadPromptPolicy.ApprovalPlan("DownloadPlc", (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
                "{\"keepActualValues\":\"false\",\"stopBeforeDownload\":true,\"allowSafetyProgramDownload\":true,\"password\":\"fixture-secret\",\"promptAnswers\":{\"ResetModule\":\"DeleteAll\"}}")!)!;
            var customAnswers = custom["answers"]!.AsObject();
            check((string?)customAnswers["DataBlockReinitialization"]!["answer"] == "StopPlcAndReinitialize" && (string?)customAnswers["SafetyProgram"]!["answer"] == "ConsistentDownload"
                && (string?)customAnswers["ResetModule"]!["answer"] == "DeleteAll" && (string?)customAnswers["ResetModule"]!["source"] == "explicit"
                && custom["loginPrompts"]!.AsArray().Any(l => (string?)l!["prompt"] == "ModuleReadAccessPassword" && (bool)l["answered"]!),
                "the approval plan follows the request arguments, CallTool's string booleans and explicit promptAnswers");
            check(!custom.ToJsonString().Contains("fixture-secret"), "the approval plan never contains a password");
            var smuggled = DownloadPromptPolicy.ApprovalPlan("DownloadPlc", (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("{\"promptAnswers\":{\"ExpandDownload\":\"Download\"}}")!)!;
            check(((string?)smuggled["invalid"] ?? "").Contains("allowExpandedDownload"), "the approval plan reports a promptAnswers entry for an opt-in prompt as invalid");
            var folder = DownloadPromptPolicy.ApprovalPlan("DownloadPlcToFolder", (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("{\"targetForSoftware\":\"PlcSimulationAdvanced\",\"overwriteOnMemoryCard\":true}")!)!["answers"]!.AsObject();
            check((string?)folder["TargetForSoftware"]!["answer"] == "PlcSimulationAdvanced" && (string?)folder["OverwriteOnMemoryCard"]!["answer"] == "Load" && (string?)folder["StopModules"]!["answer"] == "NoAction",
                "the folder download plan shows its own TargetForSoftware and OverwriteOnMemoryCard answers");
            var upload = DownloadPromptPolicy.ApprovalPlan("UploadStationFromPlc", new System.Text.Json.Nodes.JsonObject())!["answers"]!.AsObject();
            check((string?)upload["UploadMissingProducts"]!["answer"] == "TryUpload" && !upload.ContainsKey("SafetyProgram"), "the station upload plan shows only upload prompts");
            check(DownloadPromptPolicy.ApprovalPlan("CompilePlcSoftware", new System.Text.Json.Nodes.JsonObject()) == null, "tools without download prompts carry no plan");
        }
    }
}
