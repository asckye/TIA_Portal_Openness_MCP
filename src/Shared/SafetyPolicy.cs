using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaOpenness.Shared;

internal static class SafetyPolicy
{
        internal static readonly string[] Actions =
        {
            "read", "createRuntimeGroup", "deleteRuntimeGroup", "updateRuntimeGroup", "updateSettings",
            "generateGlobalFIOStatusBlock", "cleanSystemGeneratedObjects", "generateBaseId",
            "login", "logoff", "setPassword", "revokePassword"
        };
        // Native effects that cannot be verified by a readback of this tool and are not undone by it.
        internal static readonly string[] ConfirmedActions =
            { "deleteRuntimeGroup", "generateGlobalFIOStatusBlock", "cleanSystemGeneratedObjects", "generateBaseId", "setPassword", "revokePassword" };
        internal static readonly string[] RuntimeGroupActions = { "createRuntimeGroup", "deleteRuntimeGroup", "updateRuntimeGroup", "generateGlobalFIOStatusBlock" };
        internal static readonly string[] PasswordActions = { "login", "setPassword", "revokePassword" };
        // Dynamic attributes documented for RuntimeGroup ("F-OB Number/Cycle Time/Phase shift/Priority") and
        // SafetySettings ("Enable of consistent upload", "Enable F-Communication ID tag"); not CLR properties.
        internal static readonly string[] RuntimeGroupAttributes = { "FOBNumber", "FOBCycleTime", "FOBPhaseShift", "FOBPriority" };
        internal static readonly string[] SettingsAttributes = { "EnableConsistentUploadFromFCpu", "EnableFCommunicationIdTag" };
        internal static readonly string[] BlockNumberProperties = { "ManagementMode", "FromFB", "ToFB", "FromFC", "ToFC", "FromDB", "ToDB" };
        internal static readonly string[] GlobalSettingNames =
            { "SafetyModificationsPossible", "GenerationOfDefaultFailsafeProgram", "ManagementOfFailsafeInSoftwareUnitsEnvironment", "UsernameForFChangeHistory" };
        internal static readonly string[] Printers = { "MicrosoftPrintToPdf", "MicrosoftXpsDocumentWriter" };
        internal static readonly string[] PrintoutOptions = { "All", "Compact" };
        internal const string DefaultDocumentLayout = "DocuInfo_ISO_A4_Portrait";

        // Returns true when the request performs a real native call (not read, not preview).
        internal static bool ValidateRequest(string action, string runtimeGroup, string password, bool confirmSafetyChange, bool dryRun)
        {
            if (!Actions.Contains(action)) throw new ArgumentException("action must be one of: " + string.Join("/", Actions) + ".");
            if (RuntimeGroupActions.Contains(action) && string.IsNullOrWhiteSpace(runtimeGroup)) throw new ArgumentException("Exact runtimeGroup required for " + action + ".");
            if (!RuntimeGroupActions.Contains(action) && !string.IsNullOrEmpty(runtimeGroup)) throw new ArgumentException("runtimeGroup is only used by runtime-group actions.");
            if (PasswordActions.Contains(action))
            {
                if (string.IsNullOrEmpty(password)) throw new ArgumentException(action + " requires a nonempty password; it is passed to TIA only and never stored or logged.");
                if (password.Length > 256) throw new ArgumentException("Password exceeds 256 characters.");
            }
            else if (!string.IsNullOrEmpty(password)) throw new ArgumentException("password is only used by login/setPassword/revokePassword.");
            if (action == "read" || dryRun) return false;
            if (ConfirmedActions.Contains(action) && !confirmSafetyChange)
                throw new ArgumentException("Real " + action + " requires confirmSafetyChange=true besides dryRun=false.");
            return true;
        }

        internal sealed class PrintoutRequest { public string Printer = ""; public string Option = ""; public string Layout = ""; public string Extension = ""; }

        // ExportSafetyPrintout: printer/option are official enum names; the file extension must match the printer driver.
        internal static PrintoutRequest ValidatePrintoutRequest(string filePath, string printer, string option, string documentLayout)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath)) throw new ArgumentException("Absolute output filePath required (native Print overwrites; this tool refuses existing files).");
            if (!Printers.Contains(printer)) throw new ArgumentException("printer must be MicrosoftPrintToPdf or MicrosoftXpsDocumentWriter.");
            if (!PrintoutOptions.Contains(option)) throw new ArgumentException("option must be All or Compact.");
            var layout = string.IsNullOrWhiteSpace(documentLayout) ? DefaultDocumentLayout : documentLayout.Trim();
            if (layout.Length > 128 || layout.Any(c => char.IsControl(c) || c == '"')) throw new ArgumentException("documentLayout must be a plain layout name such as DocuInfo_ISO_A4_Portrait.");
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            bool ok = printer == "MicrosoftPrintToPdf" ? extension == ".pdf" : extension == ".xps" || extension == ".oxps";
            if (!ok) throw new ArgumentException(printer + " writes " + (printer == "MicrosoftPrintToPdf" ? ".pdf" : ".xps/.oxps") + "; filePath extension is '" + extension + "'.");
            return new PrintoutRequest { Printer = printer, Option = option, Layout = layout, Extension = extension };
        }

        // Relative block path segments joined with '/', root group omitted.
        internal static string JoinBlockPath(IEnumerable<string> groupNames, string blockName)
        {
            var parts = groupNames.ToList(); parts.Add(blockName);
            return string.Join("/", parts);
        }

}
