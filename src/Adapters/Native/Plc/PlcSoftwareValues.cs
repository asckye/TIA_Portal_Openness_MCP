using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;

namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcSoftwareValues
    {
        internal static object? Value(object? value) => value;
        internal static string StringArray(IEnumerable<object?> values)
            => "[" + string.Join(",", values.Select(value => "\"" + (value?.ToString() ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"")) + "]";
        internal static void RecordNativeResult(Dictionary<string, object?> evidence, object? state, object? messages)
        {
            evidence["nativeResultReturned"] = true; evidence["nativeState"] = state?.ToString();
            if (state is Enum) evidence["nativeStateType"] = state.GetType().FullName;
            evidence["mayHaveChanged"] = true; evidence["logFilePath"] = null; evidence["nativeMessages"] = messages;
        }
    }

    internal static class PlcProtectionPolicy
    {
        internal static bool ValidateProtectionRequest(string action, string password, bool confirmProtectionChange, bool dryRun)
        {
            if (action != "read" && action != "protect" && action != "unprotect") throw new ArgumentException("action must be read/protect/unprotect.");
            if (action == "read") return false;
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("A nonempty password is required to protect or unprotect.");
            if (password.Length > 256) throw new ArgumentException("Password exceeds 256 characters.");
            if (dryRun) return false;
            if (!confirmProtectionChange) throw new ArgumentException("Real protect/unprotect requires confirmProtectionChange=true besides dryRun=false.");
            return true;
        }
        internal static SecureString ToSecureString(string password)
        {
            var secure = new SecureString(); foreach (var c in password) secure.AppendChar(c); secure.MakeReadOnly(); return secure;
        }
        internal static bool ContainsInvalidPasswordCharacter(string password, IEnumerable<char> invalid)
        { var set = new HashSet<char>(invalid); return set.Count > 0 && password.Any(set.Contains); }
    }
}
