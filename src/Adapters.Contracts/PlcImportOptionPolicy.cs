using System;
using System.Linq;

namespace TiaMcp.Adapters.Contracts
{
    // Availability follows the exact PublicAPI overloads, not just enum membership.
    public static class PlcImportOptionPolicy
    {
        public const string UnsupportedCondition = "unsupported-plc-import-options";
        public static T Combine<T>(T initial, string[]? options) where T : struct, Enum
        {
            ulong bits = Convert.ToUInt64(initial, System.Globalization.CultureInfo.InvariantCulture);
            foreach (var option in options ?? Array.Empty<string>())
                bits |= Convert.ToUInt64(Enum.Parse(typeof(T), option, false), System.Globalization.CultureInfo.InvariantCulture);
            return (T)Enum.ToObject(typeof(T), bits);
        }

        public static string? Unsupported(string release, string kind, string[]? swImportOptions, string[]? importOptions, string unitName = "")
        {
            var sw = swImportOptions ?? Array.Empty<string>();
            var cultures = importOptions ?? Array.Empty<string>();
            bool units = new[] { "16", "17", "18", "19", "20", "21" }.Contains(release, StringComparer.Ordinal);
            if (unitName != "" && !units) return "unitName requires V16–V21.";
            if (sw.Distinct(StringComparer.Ordinal).Count() != sw.Length || cultures.Distinct(StringComparer.Ordinal).Count() != cultures.Length)
                return "Import options must be unique explicit opt-ins.";
            if (sw.Length > 0 && (release == "14sp1" || kind == "TagTable" || kind == "documents"))
                return "SWImportOptions require the V15.1–V21 block/type SimaticML import overload; tag tables and documents have no such overload.";
            foreach (var option in sw)
                if (option != "IgnoreStructuralChanges" && option != "IgnoreMissingReferencedObjects" && !(option == "IgnoreUnitAttributes" && units))
                    return "Unsupported SWImportOptions member for release " + release + ": " + option;
            if (cultures.Length > 0 && (!new[] { "19", "20", "21" }.Contains(release, StringComparer.Ordinal) || kind == "documents"))
                return "Culture ImportOptions require V19–V21 SimaticML import.";
            if (cultures.Any(option => option != "SkipInactiveCultures" && option != "ActivateInactiveCultures") || cultures.Length > 1)
                return "Choose at most one culture ImportOptions member: SkipInactiveCultures or ActivateInactiveCultures.";
            return null;
        }
    }
}
