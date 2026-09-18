using System;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Safety tool family (Siemens.Engineering.Safety): request gating, propertiesJson splitting across the four
    // native surfaces (CLR scalars / AssignmentOfBlockNumbers / dynamic attributes / SafetySystemVersion),
    // GlobalSettings parsing, printout argument checks and signature rows.
    internal static class SafetyLogicTests
    {
        internal static void Run(Action<bool, string> check)
        {
            bool Fails<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }

            // Request gating.
            check(!SafetyLogic.ValidateRequest("read", "", "", false, false), "safety: read is never a write even without preview");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidateRequest("compile", "", "", true, false)), "safety: unknown action refused (F-compile is not in the PublicAPI)");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidateRequest("createRuntimeGroup", "", "", false, true)), "safety: runtime-group action without runtimeGroup refused even in preview");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidateRequest("updateSettings", "F-runtime group 1", "", false, true)), "safety: runtimeGroup on a non-runtime-group action refused");
            check(!SafetyLogic.ValidateRequest("updateSettings", "", "", false, true), "safety: preview of settings update is not a write");
            check(SafetyLogic.ValidateRequest("updateSettings", "", "", false, false), "safety: real settings update is a write without confirmation (sentinel)");
            check(SafetyLogic.ValidateRequest("createRuntimeGroup", "RTG2", "", false, false), "safety: real createRuntimeGroup needs no confirmation (preview + Offline + login gate it)");
            foreach (var action in new[] { "deleteRuntimeGroup", "generateGlobalFIOStatusBlock" })
                check(Fails<ArgumentException>(() => SafetyLogic.ValidateRequest(action, "RTG1", "", false, false)), "safety: real " + action + " without confirmSafetyChange refused");
            foreach (var action in new[] { "cleanSystemGeneratedObjects", "generateBaseId" })
                check(Fails<ArgumentException>(() => SafetyLogic.ValidateRequest(action, "", "", false, false)), "safety: real " + action + " without confirmSafetyChange refused");
            check(!SafetyLogic.ValidateRequest("generateBaseId", "", "", false, true), "safety: generateBaseId preview needs no confirmation");
            check(SafetyLogic.ValidateRequest("deleteRuntimeGroup", "RTG1", "", true, false), "safety: confirmed real delete is a write (sentinel)");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidateRequest("login", "", "", false, true)), "safety: login without password refused even in preview");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidateRequest("login", "", new string('x', 257), false, true)), "safety: oversized password refused");
            check(!SafetyLogic.ValidateRequest("login", "", "pw", false, true), "safety: login preview is not a write");
            check(SafetyLogic.ValidateRequest("login", "", "pw", false, false), "safety: real login needs no confirmSafetyChange (sentinel)");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidateRequest("logoff", "", "pw", false, true)), "safety: password on logoff refused (it would be silently ignored otherwise)");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidateRequest("read", "", "pw", false, false)), "safety: password on read refused");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidateRequest("setPassword", "", "pw", false, false)), "safety: real setPassword without confirmSafetyChange refused");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidateRequest("revokePassword", "", "pw", false, false)), "safety: real revokePassword without confirmSafetyChange refused");
            check(SafetyLogic.ValidateRequest("revokePassword", "", "pw", true, false), "safety: confirmed revokePassword is a write (sentinel)");

            // updateSettings splitting.
            var split = SafetyLogic.SplitSettingsChanges("{\"ActivationOfFChangeHistory\":true,\"AssignmentOfBlockNumbers\":{\"ManagementMode\":\"FixedRange\",\"FromFB\":30000,\"ToFB\":30999},\"FromDB\":40000,\"AssignmentOfBlockNumbers.ToDB\":40999,\"EnableFCommunicationIdTag\":false,\"SafetySystemVersion\":\"V2.4\"}");
            check(split.Scalars.Count == 1 && split.Scalars.ContainsKey("ActivationOfFChangeHistory"), "settings: CLR scalar stays a scalar");
            check(split.BlockNumbers.Count == 5 && split.BlockNumbers["ManagementMode"]!.GetValue<string>() == "FixedRange" && split.BlockNumbers["ToDB"]!.GetValue<int>() == 40999, "settings: nested object, dotted key and bare block-number name all land in AssignmentOfBlockNumbers");
            check(split.Attributes.Count == 1 && split.Attributes["EnableFCommunicationIdTag"]!.GetValue<bool>() == false, "settings: documented attribute is routed to SetAttribute");
            check(split.SafetySystemVersion == "V2.4" && split.Count == 8, "settings: SafetySystemVersion string captured and counted");
            check(SafetyLogic.SplitSettingsChanges("{\"SafetySystemVersion\":{\"Value\":\"V2.2\"}}").SafetySystemVersion == "V2.2", "settings: SafetySystemVersion accepts the {Value} shape of the read output");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitSettingsChanges("{\"SafetySystemVersion\":\"\"}")), "settings: empty SafetySystemVersion refused");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitSettingsChanges("{\"AssignmentOfBlockNumbers\":{\"FromXB\":1}}")), "settings: unknown block-number property refused");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitSettingsChanges("{\"ManagementMode\":\"Manual\"}")), "settings: ManagementMode outside the enum refused");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitSettingsChanges("{\"FromFB\":0}")), "settings: block number 0 refused");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitSettingsChanges("{\"ToFC\":65536}")), "settings: block number above 65535 refused");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitSettingsChanges("{\"EnableConsistentUploadFromFCpu\":\"yes\"}")), "settings: non-boolean attribute refused");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitSettingsChanges("{}")), "settings: empty change set refused");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitSettingsChanges("[1]")), "settings: non-object JSON refused");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitSettingsChanges("{\"Name\":\"x\"}")), "settings: Name is not a Safety setting");

            // updateRuntimeGroup splitting.
            var (scalars, attributes) = SafetyLogic.SplitRuntimeGroupChanges("{\"WarnCycleTime\":90000,\"FOBPriority\":12,\"FOBCycleTime\":100000}");
            check(scalars.Count == 1 && attributes.Count == 2 && attributes["FOBPriority"]!.GetValue<int>() == 12, "runtime group: FOB* attributes separated from CLR scalars");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitRuntimeGroupChanges("{\"Name\":\"RTG9\"}")), "runtime group: rename refused");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitRuntimeGroupChanges("{\"FOBNumber\":\"123\"}")), "runtime group: non-integer FOB attribute refused");
            check(Fails<ArgumentException>(() => SafetyLogic.SplitRuntimeGroupChanges("{}")), "runtime group: empty change set refused");

            // GlobalSettings parsing.
            var global = SafetyLogic.ParseGlobalSettingChanges("{\"SafetyModificationsPossible\":false,\"UsernameForFChangeHistory\":\"acceptance-bot\"}");
            check(global.Count == 2 && (bool)global[0].Value == false && (string)global[1].Value == "acceptance-bot", "global: booleans and user name parsed in order");
            check((string)SafetyLogic.ParseGlobalSettingChanges("{\"UsernameForFChangeHistory\":null}")[0].Value == "", "global: null user name becomes the empty reset value");
            check(Fails<ArgumentException>(() => SafetyLogic.ParseGlobalSettingChanges("{\"UsernameForFChangeHistory\":\"" + new string('u', 257) + "\"}")), "global: user name over 256 characters refused instead of being cut by TIA");
            check(Fails<ArgumentException>(() => SafetyLogic.ParseGlobalSettingChanges("{\"GenerationOfDefaultFailsafeProgram\":\"true\"}")), "global: string for a boolean setting refused");
            check(Fails<ArgumentException>(() => SafetyLogic.ParseGlobalSettingChanges("{\"SafetyModeCanBeDisabled\":true}")), "global: PLC-level setting refused at portal level");
            check(Fails<ArgumentException>(() => SafetyLogic.ParseGlobalSettingChanges("{}")), "global: empty change set refused");

            // Printout arguments.
            var printout = SafetyLogic.ValidatePrintoutRequest(@"C:\out\safety.pdf", "MicrosoftPrintToPdf", "All", "");
            check(printout.Layout == SafetyLogic.DefaultDocumentLayout && printout.Extension == ".pdf", "printout: default layout and pdf extension accepted");
            check(SafetyLogic.ValidatePrintoutRequest(@"C:\out\safety.oxps", "MicrosoftXpsDocumentWriter", "Compact", "DocuInfo_Simple_A4_Portrait_Small_Footer").Layout == "DocuInfo_Simple_A4_Portrait_Small_Footer", "printout: XPS writer with .oxps and explicit layout accepted");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidatePrintoutRequest(@"C:\out\safety.xps", "MicrosoftPrintToPdf", "All", "")), "printout: pdf printer with .xps extension refused");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidatePrintoutRequest(@"C:\out\safety.pdf", "MicrosoftXpsDocumentWriter", "All", "")), "printout: XPS writer with .pdf extension refused");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidatePrintoutRequest(@"out\safety.pdf", "MicrosoftPrintToPdf", "All", "")), "printout: relative path refused");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidatePrintoutRequest(@"C:\out\safety.pdf", "AdobePdf", "All", "")), "printout: printer outside the enum refused");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidatePrintoutRequest(@"C:\out\safety.pdf", "MicrosoftPrintToPdf", "Full", "")), "printout: option outside the enum refused");
            check(Fails<ArgumentException>(() => SafetyLogic.ValidatePrintoutRequest(@"C:\out\safety.pdf", "MicrosoftPrintToPdf", "All", "Layout\"x")), "printout: layout with a quote refused");

            // Signature rows and block paths.
            var row = SafetyLogic.SignatureRow("BlockOfflineSignature", 0xDEADBEEFUL);
            check(row["hex"]!.GetValue<string>() == "0xDEADBEEF" && row["valid"]!.GetValue<bool>(), "signature: hex rendering and validity");
            var zero = SafetyLogic.SignatureRow("CollectiveOfflineSignature", 0);
            check(zero["hex"] == null && !zero["valid"]!.GetValue<bool>(), "signature: 0 is reported as no valid signature, not as a value");
            check(SafetyLogic.JoinBlockPath(new[] { "Safety", "Drivers" }, "F_Main") == "Safety/Drivers/F_Main" && SafetyLogic.JoinBlockPath(Array.Empty<string>(), "Main_Safety_RTG1") == "Main_Safety_RTG1", "block path: groups joined with '/' and root blocks bare");
            check(SafetyLogic.Actions.Distinct().Count() == 12 && SafetyLogic.ConfirmedActions.All(SafetyLogic.Actions.Contains), "actions: 12 distinct actions and every confirmed action is an action");
        }
    }
}
