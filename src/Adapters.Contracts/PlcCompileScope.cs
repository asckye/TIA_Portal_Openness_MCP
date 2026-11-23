using System;
using System.Linq;

namespace TiaMcp.Adapters.Contracts
{
    public static class PlcCompileScope
    {
        public const string UnavailableCondition = "compile-scope-unavailable";
        public static string? PasswordRefusal(string release, string password) =>
            password.Length == 0 || OpennessApi.SafetyPasswordCompile.Includes(release) ? null
                : $"Password-based compilation requires the V17 or later SafetyAdministration login API. Release {release} does not provide it.";

        public static string[] Validate(string scope, string scopePath, string unitName)
        {
            if (scope != "software" && scope != "block" && scope != "group" && scope != "unit")
                throw new ArgumentException("scope must be software, block, group or unit.", nameof(scope));
            if (scope == "software")
            {
                if (scopePath != "" || unitName != "") throw new ArgumentException("Software scope takes no scopePath or unitName.", nameof(scopePath));
                return Array.Empty<string>();
            }
            var parts = (scopePath ?? "").Split('/').Select(ObjectPathEncoding.DecodeSegment).ToArray();
            if ((scopePath ?? "").Contains("\\") || parts.Length > 64 || parts.Any(p => string.IsNullOrWhiteSpace(p) || p == "." || p == ".."))
                throw new ArgumentException("Use a relative exact scopePath with 1-64 nonempty segments; encode separators in object names.",
                        nameof(scopePath));
            if (scope == "unit" && (parts.Length != 1 || unitName != ""))
                throw new ArgumentException("Unit scope needs one exact unit name in scopePath and an empty unitName.", nameof(scopePath));
            if (unitName != "" && (string.IsNullOrWhiteSpace(unitName) || unitName.Trim() != unitName))
                throw new ArgumentException("An exact unitName without surrounding whitespace is required.", nameof(unitName));
            return parts;
        }
    }
}
