using TiaMcp.Adapters.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Versioning;

namespace TiaMcp.Adapters
{
    internal static class PlcCompilePolicy
    {
        internal static string[] RequireScope(string release, string scope, string scopePath, string unitName)
        {
            var parts = Contracts.PlcCompileScope.Validate(scope, scopePath, unitName);
            if ((scope == "unit" || unitName != "") && TiaVersionCatalog.Get(release).MajorVersion < OpennessApi.PlcUnits.FirstMajor)
                throw ScopeUnavailable("Software unit compile scopes require V16-V21.");
            return parts;
        }

        internal static Contracts.AdapterPreconditionException ScopeUnavailable(string message)
            => new Contracts.AdapterPreconditionException(message, Contracts.PlcCompileScope.UnavailableCondition, null);

        internal static void RequirePasswordCapability(string release,string password)
        {
            var version = TiaVersionCatalog.Get(release);
            if (PlcCompileScope.PasswordRefusal(release, password, version.DisplayName) is string reason)
                throw new NotSupportedException(reason) { Data = { ["foundationKnownNoMutation"] = true } };
        }
        internal static bool ScopeMismatch(string[] roots, string device, string item, Func<string> readSoftwareName)
        {
            // The first two levels can contain localized categories alongside the PLC identity.
            return roots.Length != 0 && !roots.Any(root => root == device || root == item)
                && !roots.Contains(readSoftwareName(), StringComparer.Ordinal);
        }

        // Block labels include a type and number, e.g. "Main (OB1)". A group can have the same bare name.
        internal static bool BlockLabel(string? path, string name) => name.Length > 0 && path != null
            && path.StartsWith(name + " (", StringComparison.Ordinal) && path.EndsWith(")", StringComparison.Ordinal)
            && char.IsLetter(path[name.Length + 2]) && char.IsDigit(path[path.Length - 2]);

        internal static void Classify(PlcCompileResult result,IEnumerable<PlcDiagnostic> diagnostics, bool blockScope = false)
        {
            var raw=new List<string>(); var errors=new List<string>(); var warnings=new List<string>(); var info=new List<string>();
            var otherErrors = new List<string>();
            foreach(var item in diagnostics)
            {
                raw.Add(item.Formatted);
                var description=item.Description.Trim();
                // API audit 2026-10-19 F8: State is the CompilerResultState enum name (Error, Information, Success, Warning), never
                // localized text. Summary rows are still recognised by the English/German "Compiling finished" prefixes and by the
                // empty-description parent rule; where TIA places them in the tree is on the VM checklist. Success and the native
                // ErrorCount/WarningCount always come from the root result, not from these lists.
                if(new[]{"Compiling finished","Compilation finished","Kompilierung beendet"}
                        .Any(s=>description.StartsWith(s,StringComparison.OrdinalIgnoreCase)))
                    continue;
                bool error=string.Equals(item.State,"Error",StringComparison.Ordinal);
                bool warning=string.Equals(item.State,"Warning",StringComparison.Ordinal);
                if((error || warning) && item.HasChildren && string.IsNullOrWhiteSpace(item.Description)) continue;
                var target = error && blockScope && !item.InRequestedBlock ? otherErrors : error ? errors : warning ? warnings : info;
                if((error || warning || description.Length!=0) && !target.Contains(item.Formatted)) target.Add(item.Formatted);
            }
            if (result is PlcScopedCompileResult scoped) scoped.OtherErrors = otherErrors.ToArray();
            result.Messages=raw.ToArray(); result.Errors=errors.ToArray(); result.Warnings=warnings.ToArray(); result.Info=info.ToArray();
        }
    }
}
