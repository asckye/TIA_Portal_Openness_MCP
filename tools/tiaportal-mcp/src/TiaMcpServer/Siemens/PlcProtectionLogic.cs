using System;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    // 2.7.53 (real machine, 项目1 / PLCSIM Advanced): the first hardware download to the F-CPU 1515F-2 PN V2.9 was refused by TIA V21's
    // hardware compile - "the password for full access must be set" (access level NoAccess without password), "Password for confidential
    // PLC configuration data is not configured" and "The PLC communication certificate cannot be configured without the password" - and
    // no tool could change either setting. Official pages: "Access level setting" (PlcAccessLevelProvider: PlcProtectionAccessLevel
    // attribute, SetPassword(level, SecureString) / ResetPassword(level)) and "Managing PLC Master Secret in PLCs"
    // (PlcMasterSecretConfigurator: Protect / Unprotect / ChangePassword / ProtectAllPlcConfiguration[WithPassword] /
    // UnprotectAllPlcConfiguration / Reset, MasterSecretConfiguration state). Pure gating here; the Openness calls are in Portal.PlcProtection.cs.
    internal static class PlcProtectionLogic
    {
        // PlcProtectionAccessLevel names the Openness user may set (None is "for enum initialization" only).
        internal static readonly string[] AccessLevels = { "FullAccess", "ReadAccess", "HMIAccess", "NoAccess", "FullAccessIncludingFailsafe" };
        internal static readonly string[] Actions =
        {
            "read", "setAccessLevel", "setAccessPassword", "resetAccessPassword",
            "protectMasterSecret", "changeMasterSecret", "unprotectMasterSecret", "resetMasterSecret",
            "protectAllConfiguration", "unprotectAllConfiguration"
        };
        // Actions that need accessLevel / password / newPassword. unprotectMasterSecret takes the password only when one is configured.
        internal static readonly string[] LevelActions = { "setAccessLevel", "setAccessPassword", "resetAccessPassword" };
        internal static readonly string[] PasswordActions = { "setAccessPassword", "protectMasterSecret", "changeMasterSecret" };
        internal static readonly string[] MasterSecretActions = { "protectMasterSecret", "changeMasterSecret", "unprotectMasterSecret", "resetMasterSecret", "protectAllConfiguration", "unprotectAllConfiguration" };

        internal static string NormalizeAction(string? action)
        {
            var a = (action ?? "").Trim();
            var hit = Actions.FirstOrDefault(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
            if (hit == null) throw new ArgumentException("action must be one of: " + string.Join(", ", Actions) + ".");
            return hit;
        }

        internal static string RequireAccessLevel(string? accessLevel)
        {
            var value = (accessLevel ?? "").Trim();
            var hit = AccessLevels.FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (hit == null) throw new ArgumentException("accessLevel must be one of: " + string.Join(", ", AccessLevels) + " (TIA UI: Full access (no protection) / Read access / HMI access / No access / Full access incl. fail-safe - F-CPUs only).");
            return hit;
        }

        // Validates the parameter set of one action; returns the normalized action and access level (empty when not needed).
        internal static (string Action, string AccessLevel) Validate(string? action, string? accessLevel, string? password, string? newPassword)
        {
            var act = NormalizeAction(action);
            var level = LevelActions.Contains(act) ? RequireAccessLevel(accessLevel) : "";
            if (PasswordActions.Contains(act) && string.IsNullOrEmpty(password))
                throw new ArgumentException(act + " needs password (converted to SecureString, never echoed; TIA rejects an empty password).");
            if (act == "changeMasterSecret" && string.IsNullOrEmpty(newPassword))
                throw new ArgumentException("changeMasterSecret needs newPassword besides password (the current master secret).");
            if (act == "setAccessPassword" && level == "FullAccessIncludingFailsafe")
                throw new ArgumentException("No password exists for FullAccessIncludingFailsafe (it is the unprotected level of an F-CPU); passwords belong to FullAccess / ReadAccess / HMIAccess.");
            if (act == "setAccessPassword" && level == "NoAccess")
                throw new ArgumentException("No password exists for NoAccess (complete protection has no login of its own); set the password for FullAccess / ReadAccess / HMIAccess.");
            return (act, level);
        }

        // Official rule: passwords can only be set / reset for levels LESS strict than the selected access level (HMIAccess selected ->
        // FullAccess and ReadAccess passwords only). Strictness order: FullAccessIncludingFailsafe < FullAccess < ReadAccess < HMIAccess < NoAccess.
        internal static int Strictness(string level) => level switch
        {
            "FullAccessIncludingFailsafe" => 0, "FullAccess" => 1, "ReadAccess" => 2, "HMIAccess" => 3, "NoAccess" => 4, _ => -1
        };

        internal static string? PasswordLevelWarning(string currentLevel, string passwordLevel)
        {
            var current = Strictness(currentLevel); var target = Strictness(passwordLevel);
            if (current < 0 || target < 0) return null;
            return target >= current
                ? "TIA only accepts passwords for levels less strict than the selected access level (" + currentLevel + "); '" + passwordLevel + "' is expected to be refused with 'Cannot set password for the access level'."
                : null;
        }

        // MasterSecretConfiguration values TIA should report after each master-secret action (official enum: None / WithPassword /
        // WithoutPassword / WithPasswordAllDataProtection).
        internal static string[] ExpectedMasterSecretStates(string action, bool withPassword) => action switch
        {
            "protectMasterSecret" => new[] { "WithPassword", "WithPasswordAllDataProtection" },
            "protectAllConfiguration" => withPassword ? new[] { "WithPasswordAllDataProtection" } : new[] { "WithPasswordAllDataProtection", "WithoutPassword", "WithPassword" },
            "unprotectMasterSecret" => new[] { "None" },
            "unprotectAllConfiguration" => new[] { "WithPassword", "WithoutPassword", "None" },
            "resetMasterSecret" => new[] { "None", "WithoutPassword" },
            _ => Array.Empty<string>()
        };

        // The hardware-compile rule seen on the real machine: any level above FullAccess / FullAccessIncludingFailsafe needs the FullAccess password.
        internal static bool NeedsFullAccessPassword(string level) => Strictness(level) >= 2;
    }
}
