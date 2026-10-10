using System;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
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
#if PLC_UNITS
        public static PlcUnitProvider RequireProvider(PlcSoftware plc)
            => plc.GetService<PlcUnitProvider>() ?? throw new NotSupportedException("PlcUnitProvider unavailable: this PLC family does not support software units.");
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
                return safety.Find(name) ?? throw new PlcSoftwareException("NotFound", "Exact safety unit not found: " + name + " (available: " + string.Join(", ", PlcGroupOperations.Items(safety).Cast<PlcSafetyUnit>().Select(u => u.Name)) + ").");
#else
                throw new NotSupportedException("PlcSafetyUnit requires V18.");
#endif
            }
            var units = group.Units;
            return units.Find(name) ?? throw new PlcSoftwareException("NotFound", "Exact software unit not found: " + name + " (available: " + string.Join(", ", PlcGroupOperations.Items(units).Cast<PlcUnit>().Select(u => u.Name)) + ").");
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
}
