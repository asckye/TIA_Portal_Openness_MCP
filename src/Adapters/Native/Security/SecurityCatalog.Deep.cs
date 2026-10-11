namespace TiaMcp.Adapters.Native.Security;
internal static partial class SecurityCatalog
{
        internal static readonly string[] SyslogScopes = { "project", "plc" };
        internal static readonly string[] SyslogProjectActions = { "read", "create", "update", "delete", "assignModule", "unassignModule" };
        internal static readonly string[] SyslogPlcActions = { "read", "update", "createServer", "deleteServer" };
        internal static readonly string[] SyslogServerProperties = { "Name", "Address", "Port", "Tls", "Comment" };
        internal static readonly string[] SyslogManagerProperties = { "EnableSystemLogging", "TransportProtocol" };
        internal static readonly string[] SyslogTransportProtocols = { "None", "TLSServerAndClientAuthentication", "TLSOnlyServerAuthentication", "UDP" };
        internal static readonly string[] SyslogPlcAttributes = { "SysLogAutoAcceptClient", "SysLogClientCertificateId", "SysLogTrustedCertificateIds" };
        internal static readonly string[] PolicyActions = { "read", "update" };
        internal static readonly string[] PolicyTargets = { "umac", "plc", "legacyPlc" };
        internal static readonly string[] UmacPolicyProperties = { "IncludesLowerCaseAndUpperCaseCharacters", "MinimumLength", "MinimumNumericCharacterLength", "MinimumSpecialCharacterLength", "EnablePasswordAging", "MinimumUserPasswordsBlockedForReuse", "PasswordValidity", "PasswordValidityPrewarningTime" };
        internal static readonly string[] PlcPolicyProperties = { "PasswordPolicyEnabled" };
        internal static readonly string[] LegacyPlcPolicyProperties = { "PasswordPolicyEnabled", "MinimumLength", "MinimumNumericCharacterLength", "MinimumSpecialCharacterLength", "IncludesLowerCaseAndUpperCaseCharacters" };
        internal static readonly string[] UmcKinds = { "user", "group", "server" };
        internal static readonly string[] UmcMemberActions = { "read", "createOffline", "importFromServer", "rename", "activate", "deactivate", "delete", "assignRole", "unassignRole" };
        internal static readonly string[] UmcServerActions = { "read", "checkConsistency", "synchronize" };
        internal static readonly string[] UmcCredentialActions = { "importFromServer", "checkConsistency", "synchronize" };
        internal static readonly string[] TemplateProperties = { "Signature", "SubjectCommonName", "Usage", "ValidFrom", "ValidUntil" };
        internal const string ProjectSyslogCreateRefusal = "scope=project action=create is preview-only: Siemens.Engineering.Security.SyslogServerComposition.Create(name) crashed TIA Portal V21 twice on the reference project (2026-09-18, message-less NonRecoverableException, process gone). Create the server in the TIA UI (project security settings) and manage it here with read / update / assignModule / unassignModule / delete.";
}
