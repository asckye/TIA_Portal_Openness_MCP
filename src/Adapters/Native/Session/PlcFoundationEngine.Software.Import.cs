using System;
using System.Linq;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
#if PLC_UNITS
using Siemens.Engineering.SW.Units;
#endif
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal static void RequireScopedImportAvailable(XDocument document, string prefix, bool overwrite, PlcSoftware plc,
            string unitName, object? targetUnit, Func<object?, string, bool> exists)
        {
            PlcFoundationPolicy.RequireImportAvailable(document, prefix, overwrite, name => exists(targetUnit, name));
#if PLC_UNITS
#if PLC_SAFETY_UNITS
            static bool HasNamespacePreset(object? unit) => unit is PlcUnitBase scoped && !string.IsNullOrEmpty(scoped.NamespacePreset);
            if (HasNamespacePreset(targetUnit)) return;
#endif
            var names = document.Root!.Elements().Where(e => e.Name.LocalName.StartsWith(prefix, StringComparison.Ordinal))
                .SelectMany(e => e.Elements().Where(a => a.Name.LocalName == "AttributeList"))
                .SelectMany(a => a.Elements().Where(n => n.Name.LocalName == "Name")).Select(n => n.Value).ToArray();
            if (names.Length == 0) return;
            void CheckOther(object? unit, string scope)
            {
#if PLC_SAFETY_UNITS
                if (HasNamespacePreset(unit)) return;
#endif
                foreach (string name in names)
                    if (exists(unit, name))
                        throw new AdapterPreconditionException("Object '" + name + "' already exists in " + scope
                            + ". Overwrite applies only within the target scope.", "unitName", false);
            }
            if (targetUnit != null) CheckOther(null, "the PLC root");
            var provider = plc.GetService<PlcUnitProvider>();
            if (provider == null) return;
            foreach (var unit in provider.UnitGroup.Units)
                if (unit.Name != unitName) CheckOther(unit, "software unit '" + unit.Name + "'");
#if PLC_SAFETY_UNITS
            foreach (var unit in provider.UnitGroup.SafetyUnits) CheckOther(unit, "safety unit '" + unit.Name + "'");
#endif
#endif
        }

        internal static string? SingleNewImportName(XDocument document, bool overwrite, params string[] supported)
            => overwrite ? null : SimaticMlImportWindow.SingleImportName(document, supported);

        internal static T ImportWithReadback<T>(Func<T> import, string? newName, bool defaultOptions, Func<string, bool> exists)
        {
            try { return import(); }
            catch (EngineeringException native)
            {
                native.Data["phase"] = "import";
                native.Data["nativeMessage"] = NativeFailureText.Compose(native.Message, null, Array.Empty<string>());
                try
                {
                    var data = native.MessageData;
                    string reason = NativeFailureText.Compose(data.Text, data.DetailText,
                        native.DetailMessageData.SelectMany(detail => new[] { detail.Text, detail.DetailText }));
                    if (reason.Length > 0) native.Data["nativeMessage"] = reason;
                }
                catch (Exception) { /* swallow(probe-optional): retain the original Siemens exception message */ }
                if (defaultOptions && NativeRefusalProbe.ProvesAbsent(native, newName, exists))
                {
                    native.Data["foundationKnownNoMutation"] = true;
                    native.Data["verifiedAbsent"] = true;
                }
                throw;
            }
        }
    }
}
