using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class EngineeringCredentialRules
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => ArgumentRules.RequireOneOf(value, allowed, parameter);
        // ---- credentials for OpenProject / GoOnline; R/H targets ---------------------------------------------------------------
        internal static readonly string[] UmacUserTypes = { "Project", "Global" };
        internal static readonly string[] OnlineUserTypes = { "None", "AnonymousUser", "GlobalUser", "ProjectUser", "SingleSignOnUser", "PasswordOnly" };
        internal static readonly string[] RhTargets = { "", "primary", "backup" };
        internal static void ValidateUmacCredentials(string userName, string password, string userType)
        {
            bool any = !string.IsNullOrEmpty(userName) || !string.IsNullOrEmpty(password);
            if (!any) { if (!string.IsNullOrEmpty(userType)) throw new ArgumentException("umacUserType applies together with umacUserName + umacPassword."); return; }
            if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password)) throw new ArgumentException("umacUserName and umacPassword must be given together (protected project credentials; the password is converted to SecureString and never echoed).");
            RequireOneOf(string.IsNullOrEmpty(userType) ? "Project" : userType, UmacUserTypes, "umacUserType");
        }
        internal static void ValidateOnlineCredentials(string userName, string password, string userType)
        {
            if (!string.IsNullOrEmpty(userName) && string.IsNullOrEmpty(password)) throw new ArgumentException("userName needs a password (online authentication for UMAC-protected PLCs).");
            if (!string.IsNullOrEmpty(userType)) { RequireOneOf(userType, OnlineUserTypes, "userType"); if (string.IsNullOrEmpty(userName) && userType != "None" && userType != "AnonymousUser" && userType != "PasswordOnly") throw new ArgumentException("userType " + userType + " needs a userName."); }
        }
        // TIA V21 / PLCSIM Advanced via Softbus, 2026-09-21 (docs/reference/real-machine-ledger.md): first contact with an S7-1500 FW >= 2.9 raises
        // TlsVerificationConfiguration on ConnectionConfiguration.OnlineLegitimation ("The device is not trusted. Please check the
        // certificate." when nobody answers). The official answer is CurrentSelection = Trusted; the decision is the caller's
        // (trustDeviceCertificate) and the selection TIA reported before it is kept for the record.
        internal static readonly string[] TlsSelections = { "NonVerified", "Trusted", "NonTrusted" };
        internal static string? TlsSelectionToApply(bool trustDeviceCertificate, string currentSelection)
        {
            if (!trustDeviceCertificate) return null;
            return string.Equals(currentSelection, "Trusted", StringComparison.Ordinal) ? null : "Trusted";
        }
        internal static string ValidateRhTarget(string rhTarget)
        {
            var value = rhTarget ?? "";
            if (!RhTargets.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("rhTarget must be empty (standard CPU), primary or backup (R/H systems via RHDownloadProvider / RHOnlineProvider).");
            return value;
        }
    }
}
