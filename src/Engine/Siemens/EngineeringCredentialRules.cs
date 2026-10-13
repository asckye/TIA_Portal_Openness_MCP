using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class EngineeringCredentialRules
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter)
            => TiaMcp.Adapters.Contracts.EngineeringCredentialRules.RequireOneOf(value, allowed, parameter);
        // ---- credentials for OpenProject / GoOnline; R/H targets ---------------------------------------------------------------
        internal static readonly string[] UmacUserTypes = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.UmacUserTypes;
        internal static readonly string[] OnlineUserTypes = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.OnlineUserTypes;
        internal static readonly string[] RhTargets = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.RhTargets;
        internal static void ValidateUmacCredentials(string userName, string password, string userType)
            => TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ValidateUmacCredentials(userName, password, userType);
        internal static void ValidateOnlineCredentials(string userName, string password, string userType)
            => TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ValidateOnlineCredentials(userName, password, userType);
        // TIA V21 / PLCSIM Advanced via Softbus, 2026-09-21 (docs/reference/real-machine-ledger.md): first contact with an S7-1500 FW >= 2.9 raises
        // TlsVerificationConfiguration on ConnectionConfiguration.OnlineLegitimation ("The device is not trusted. Please check the
        // certificate." when nobody answers). The official answer is CurrentSelection = Trusted; the decision is the caller's
        // (trustDeviceCertificate) and the selection TIA reported before it is kept for the record.
        internal static readonly string[] TlsSelections = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.TlsSelections;
        internal static string? TlsSelectionToApply(bool trustDeviceCertificate, string currentSelection)
            => TiaMcp.Adapters.Contracts.EngineeringCredentialRules.TlsSelectionToApply(trustDeviceCertificate, currentSelection);
        internal static string ValidateRhTarget(string rhTarget)
            => TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ValidateRhTarget(rhTarget);
    }
}
