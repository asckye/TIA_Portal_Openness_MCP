using System;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.ExternalSources;
using System.Collections.Generic;
using System.IO;
using TiaMcp.Adapters.Contracts;
#if PLC_UNITS
using Siemens.Engineering.SW.Units;
#if !PLC_SAFETY_UNITS
using PlcUnitBase = Siemens.Engineering.SW.Units.PlcUnit;
#endif
#endif

namespace TiaMcp.Adapters.Native.Plc
{
    // Shared by software-unit tools and block/type/document scopes.
    public static class PlcUnitScope
    {
        public static object? Optional(PlcSoftware plc, string unitName)
        {
            if (unitName == "") return null;
            if (string.IsNullOrWhiteSpace(unitName) || unitName.Trim() != unitName)
                throw new ArgumentException("An exact unitName without surrounding whitespace is required.", nameof(unitName));
#if PLC_UNITS
            var provider = plc.GetService<PlcUnitProvider>()
                ?? throw new AdapterPreconditionException("This PLC does not provide software units.", "unitName", false);
            var unit = provider.UnitGroup.Units.Find(unitName);
            if (unit == null || unit.Name != unitName) throw new AdapterPreconditionException("Exact software unit not found: " + unitName, "unitName", false);
            return unit;
#else
            throw new AdapterPreconditionException("unitName requires V16–V21.", "unitName", false);
#endif
        }
        public static IEnumerable<KeyValuePair<string, object>> Units(PlcSoftware plc)
        {
#if PLC_UNITS
            var provider = plc.GetService<PlcUnitProvider>();
            if (provider != null)
                foreach (var unit in provider.UnitGroup.Units) yield return new KeyValuePair<string, object>(unit.Name, unit);
#else
            yield break;
#endif
        }
        public static PlcTagTableGroup Tags(PlcSoftware plc, object? unit)
        {
#if PLC_UNITS
            return unit == null ? plc.TagTableGroup : ((PlcUnitBase)unit).TagTableGroup;
#else
            return plc.TagTableGroup;
#endif
        }
        public static PlcTagTableGroup TagRoot(PlcSoftware plc, string unitName)
            => Tags(plc, Optional(plc, unitName));
        public static PlcExternalSourceGroup Sources(PlcSoftware plc, object? unit)
        {
#if PLC_UNITS
            return unit == null ? plc.ExternalSourceGroup : ((PlcUnitBase)unit).ExternalSourceGroup;
#else
            return plc.ExternalSourceGroup;
#endif
        }
#if PLC_UNITS
        public static PlcUnitProvider RequireProvider(PlcSoftware plc)
            => plc.GetService<PlcUnitProvider>()
                ?? throw new NotSupportedException("PlcUnitProvider unavailable: this PLC family does not support software units.");
        public static PlcUnitBase? FindSafety(PlcUnitSystemGroup group, string name)
        {
#if PLC_SAFETY_UNITS
            return group.SafetyUnits.Find(name);
#else
            return null;
#endif
        }
        public static PlcUnitBase Exact(PlcUnitSystemGroup group, string kind, string name)
        {
            if (kind == "safety")
            {
#if PLC_SAFETY_UNITS
                var safety = group.SafetyUnits;
                return safety.Find(name)
                    ?? throw new PlcSoftwareException("NotFound", "Exact safety unit not found: " + name + " (available: " + string
                        .Join(", ", PlcGroupOperations.Items(safety).Cast<PlcSafetyUnit>().Select(u => u.Name)) + ").");
#else
                throw new NotSupportedException("PlcSafetyUnit requires V18.");
#endif
            }
            var units = group.Units;
            return units.Find(name)
                ?? throw new PlcSoftwareException("NotFound", "Exact software unit not found: " + name + " (available: " + string
                    .Join(", ", PlcGroupOperations.Items(units).Cast<PlcUnit>().Select(u => u.Name)) + ").");
        }
#endif
        public static object? OptionalLegacy(PlcSoftware plc, string unitName, string unitKind)
        {
            if (string.IsNullOrEmpty(unitName)) return null;
            if (unitKind != "unit" && unitKind != "safety") throw new ArgumentException("unitKind must be unit or safety.");
            var type = typeof(PlcSoftware).Assembly.GetType("Siemens.Engineering.SW.Units.PlcUnitProvider")
                ?? throw new NotSupportedException("PLC units are unavailable in this API.");
            var provider = typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(type).Invoke(plc, null)
                ?? throw new NotSupportedException("PLC unit provider is unavailable on this PLC.");
            var group = PlcGroupOperations.Get(provider, "UnitGroup");
            return PlcGroupOperations.Find(PlcGroupOperations.Get(group, unitKind == "safety" ? "SafetyUnits" : "Units"), unitName)
                ?? throw new PlcSoftwareException("NotFound", "Exact PLC unit not found: " + unitName);
        }
        public static PlcBlockGroup Blocks(PlcSoftware plc, object? unit)
        {
#if PLC_UNITS
            return unit == null ? plc.BlockGroup : ((PlcUnitBase)unit).BlockGroup;
#else
            return plc.BlockGroup;
#endif
        }
        public static PlcTypeGroup Types(PlcSoftware plc, object? unit)
        {
#if PLC_UNITS
            return unit == null ? plc.TypeGroup : ((PlcUnitBase)unit).TypeGroup;
#else
            return plc.TypeGroup;
#endif
        }
    }

    internal static class PlcNativeImportOptions
    {
        internal static void Validate(string release, string kind, string[]? sw, string[]? cultures, string unitName = "")
        {
            var unsupported = PlcImportOptionPolicy.Unsupported(release, kind, sw, cultures, unitName);
            if (unsupported != null) throw new AdapterPreconditionException(unsupported, PlcImportOptionPolicy.UnsupportedCondition, null);
        }
        internal static ImportOptions General(bool overwrite, string[]? cultures)
            => PlcImportOptionPolicy.Combine(overwrite ? ImportOptions.Override : ImportOptions.None, cultures);
        internal static IList<PlcBlock> Blocks(PlcBlockComposition blocks, FileInfo file, ImportOptions options, string[]? sw)
        {
#if PLC_SOURCE_RESULTS
            if (sw != null && sw.Length > 0) return blocks.Import(file, options, Software(sw));
#endif
            return blocks.Import(file, options);
        }
        internal static IList<PlcType> Types(PlcTypeComposition types, FileInfo file, ImportOptions options, string[]? sw)
        {
#if PLC_SOURCE_RESULTS
            if (sw != null && sw.Length > 0) return types.Import(file, options, Software(sw));
#endif
            return types.Import(file, options);
        }
#if PLC_SOURCE_RESULTS
        private static SWImportOptions Software(string[] sw)
            => PlcImportOptionPolicy.Combine(SWImportOptions.None, sw);
#endif
    }
}
