using System;
using System.Collections.Generic;
using TiaMcpServer.Siemens;
namespace TiaMcpServer.Tests
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
            check(p2.Decide("DataBlockReinitializationOrKeepActualValues", new[] { "KeepActualValues", "StopPlcAndReinitialize" }, false, false).Value == "StopPlcAndReinitialize", "keepActualValues=false reinitializes");

            foreach (var destructive in new[] { "ResetModule", "InitializeMemory", "OverwriteOnMemoryCard", "OverwriteSystemData", "SwitchBackupToPrimary" })
            {
                var d = new DownloadPromptPolicy().Decide(destructive, new[] { "NoAction", "DeleteAll" }, false, false);
                check(d.Kind == DownloadPromptPolicy.AnswerKind.Selection && d.Value == "NoAction" && d.Source == "conservative", destructive + " defaults to NoAction with a conservative note");
            }
            check(new DownloadPromptPolicy().Decide("ProtectionLevelChanged", new[] { "ContinueDownloading", "NoChange" }, false, false).Value == "NoChange", "ProtectionLevelChanged keeps the protection level by default");
            check(new DownloadPromptPolicy().Decide("TargetForSoftware", new[] { "CPU", "PlcSimulationAdvanced" }, false, false).Value == "CPU", "TargetForSoftware defaults to the real CPU");

            var explicitPolicy = new DownloadPromptPolicy();
            foreach (var kv in DownloadPromptPolicy.ParseExplicitAnswers("{\"ResetModule\":\"deleteall\",\"OverwriteHmiData\":true}")) explicitPolicy.Explicit[kv.Key] = kv.Value;
            var e1 = explicitPolicy.Decide("ResetModule", new[] { "NoAction", "DeleteAll" }, false, false);
            check(e1.Source == "explicit" && e1.Value == "DeleteAll", "explicit answer overrides the conservative default and is normalised to the native enum casing");
            var e2 = explicitPolicy.Decide("OverwriteHmiData", none, true, false);
            check(e2.Kind == DownloadPromptPolicy.AnswerKind.Checked && e2.Value == "true", "explicit boolean answers checkbox prompts");
            check(Fails(() => explicitPolicy.Decide("StopModules", new[] { "NoAction", "StopAll" }, false, false) == null), "explicit map without the prompt does not throw") == false, "sentinel: a prompt absent from the explicit map still uses defaults");
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
        }
    }
}
