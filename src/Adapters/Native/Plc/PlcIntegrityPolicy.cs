using System;
using System.Linq;

namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcIntegrityPolicy
    {
        internal static readonly string[] UnitKinds = { "unit", "safety" };
        internal static readonly string[] UnitListKinds = { "all", "unit", "safety" };
        internal static void RequireOneOf(string value, string[] allowed, string parameter)
        {
            if (!allowed.Contains(value, StringComparer.Ordinal)) throw new ArgumentException(parameter + " must be one of: " + string.Join("/", allowed) + " (case-sensitive).");
        }
        private static void RequireName(string value, string parameter, int max = 256)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        // ---- fingerprints (FingerprintProvider on blocks and UDTs) ------------------------------------------------------------------
        internal static readonly string[] FingerprintObjectKinds = { "block", "type" };
        internal static readonly string[] FingerprintIds = { "Code", "Comments", "Interface", "LibraryType", "Texts", "Alarms", "Supervisions", "TechnologyObject", "Events", "TextualInterface", "Properties", "ProgramCode" };
        internal static void ValidateFingerprintRequest(string objectKind, string objectPath, string unitName, string unitKind)
        {
            RequireOneOf(objectKind, FingerprintObjectKinds, "objectKind");
            RequireName(objectPath, "objectPath", 1024);
            if (!string.IsNullOrEmpty(unitName)) RequireOneOf(unitKind, UnitKinds, "unitKind");
        }

        // ---- block write protection (PlcBlockWriteProtectionProvider, V21) ---------------------------------------------------------
        internal static readonly string[] WriteProtectionActions = { "read", "define", "protect", "unprotect", "change", "remove" };
        internal static bool ValidateWriteProtectionRequest(string action, string password, string newPassword, bool confirmProtectionChange, bool dryRun)
        {
            RequireOneOf(action, WriteProtectionActions, "action");
            if (action == "read")
            {
                if (!string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(newPassword)) throw new ArgumentException("read takes no password.");
                return false;
            }
            if (string.IsNullOrEmpty(password)) throw new ArgumentException(action + " requires the current password (define: the password to set).");
            if (action == "change") { if (string.IsNullOrEmpty(newPassword)) throw new ArgumentException("change requires newPassword; use remove to drop the password entirely."); }
            else if (!string.IsNullOrEmpty(newPassword)) throw new ArgumentException("newPassword applies to change only.");
            if (dryRun) return false;
            if (!confirmProtectionChange) throw new ArgumentException("Real write-protection changes require confirmProtectionChange=true besides dryRun=false.");
            return true;
        }
        // State machine of the official workflow: Define sets the password, Protect / Unprotect toggle protection with it,
        // Change(pwd, newPwd) rotates it and Change(pwd, null) removes password and protection.
        internal static string? WriteProtectionStateRefusal(string action, bool isDefined, bool isProtected)
        {
            switch (action)
            {
                case "define": return isDefined ? "A write-protection password is already defined; use change or remove." : null;
                case "protect": return !isDefined ? "No write-protection password is defined; define first." : isProtected ? "Block is already write protected." : null;
                case "unprotect": return !isDefined ? "No write-protection password is defined." : !isProtected ? "Block is not write protected." : null;
                case "change": case "remove": return !isDefined ? "No write-protection password is defined." : null;
                default: return null;
            }
        }

    }
}
