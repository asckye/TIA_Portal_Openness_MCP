using TiaMcp.Adapters.Contracts;
namespace TiaMcpServer.Siemens;
internal static class PlcProtectionLogic
{
    internal static readonly string[] AccessLevels = PlcProtectionRules.AccessLevels;
    internal static readonly string[] Actions = PlcProtectionRules.Actions;
    internal static readonly string[] LevelActions = PlcProtectionRules.LevelActions;
    internal static readonly string[] PasswordActions = PlcProtectionRules.PasswordActions;
    internal static readonly string[] MasterSecretActions = PlcProtectionRules.MasterSecretActions;
    internal static string NormalizeAction(string? action) => PlcProtectionRules.NormalizeAction(action);
    internal static string RequireAccessLevel(string? accessLevel) => PlcProtectionRules.RequireAccessLevel(accessLevel);
    internal static (string Action, string AccessLevel) Validate(string? action, string? accessLevel, string? password, string? newPassword) => PlcProtectionRules.Validate(action, accessLevel, password, newPassword);
    internal static int Strictness(string level) => PlcProtectionRules.Strictness(level);
    internal static string? PasswordLevelWarning(string currentLevel, string passwordLevel) => PlcProtectionRules.PasswordLevelWarning(currentLevel, passwordLevel);
    internal static string[] ExpectedMasterSecretStates(string action, bool withPassword) => PlcProtectionRules.ExpectedMasterSecretStates(action, withPassword);
    internal static bool NeedsFullAccessPassword(string level) => PlcProtectionRules.NeedsFullAccessPassword(level);
}
