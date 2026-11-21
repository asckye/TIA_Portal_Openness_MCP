using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private IDisposable HardwareEditAccess() => AcquireEngineeringEditAccess != null ? AcquireEngineeringEditAccess()
            : HasEngineeringTransaction ? new EngineeringAmbientAccess() : Portal().ExclusiveAccess(ExclusiveAccessText());
        private void HardwareWriteAdmission()
        {
            Check();
            if (EngineeringProjectMissing?.Invoke() == true || project == null)
                throw new AdapterPreconditionException("No project is bound.", "project-binding", null);
            if (HasEngineeringTransaction)
                throw new AdapterPreconditionException("CAx import cannot run inside an engineering transaction.", "exclusive-access", null);
        }
        private static IEngineeringServiceProvider HardwareServiceProvider(object target)
            => target as IEngineeringServiceProvider ?? throw StepFailureStatus.UnsupportedByTarget("Selected object is not a service provider.");
        private static bool HardwareDisposedOnly(Exception error) => HmiReadPolicy.DisposedObjectOnly(error);
        private static void HardwarePage(object[] rows, int offset, int limit, Dictionary<string, object?> meta)
        {
            var page = rows.Skip(offset).Take(limit).ToArray(); meta["records"] = page;
            foreach (var pair in HardwareServicesPolicy.PageMeta(rows.Length, offset, limit, page.Length)) meta[pair.Key] = pair.Value;
        }
        internal static bool HardwarePageComplete(object[] rows, int offset, object[] page)
            => offset == 0 && page.Length == rows.Length && !HardwareHasFailures(page);
        private static bool HardwareHasFailures(object? value)
        {
            if (value is IDictionary<string, object?> fields)
                return fields.Any(pair => pair.Key == "failures" && pair.Value is System.Collections.IEnumerable failures
                    && failures.Cast<object>().Any() || HardwareHasFailures(pair.Value));
            return value is System.Collections.IEnumerable items && value is not string && items.Cast<object>().Any(HardwareHasFailures);
        }
        private static DeviceItem HardwareRequireDeviceItem(HardwareObject owner, string parameter)
            => owner as DeviceItem ?? throw HardwareServicesPolicy.Argument(parameter, "must address a DeviceItem (nonempty path).");

        private static T HardwareRequireHardwareService<T>(HardwareObject owner, string parameter) where T : class, IEngineeringService
            => HardwareServiceProvider(owner).GetService<T>()
                ?? throw StepFailureStatus.UnsupportedByTarget(HardwareServicesPolicy.DeclaredParameter(parameter) + " does not expose " + typeof(T).Name
                    + " (service is null for this hardware object).");

        internal static void JournalTransferAreaCreate(Func<object> freshAreas, string name)
            => Native.Engineering.EngineeringWriteJournal.Creating(() => HardwareGroupOperations.Items(freshAreas()).SingleOrDefault(area =>
                string.Equals((string)HardwareGroupOperations.Get(area, "Name"), name, StringComparison.Ordinal)), name);

        internal static bool VerifyHardwareDelete(Func<bool> stillPresent, string description, Dictionary<string, object?> meta)
        {
            bool FreshLookup()
            {
                try { return stillPresent(); }
                catch (Exception ex) when (HardwareDisposedOnly(ex)) { return stillPresent(); }
            }
            if (!ReadbackStep.TryRead(meta, "postDelete", FreshLookup, out var remaining)) return false;
            if (remaining) throw new InvalidOperationException("Delete returned but " + description + " is still present on the refreshed composition.");
            meta["verifiedAbsent"] = true;
            return true;
        }

        private static object? HardwareFindOnFresh(Func<object> composition, string name, Dictionary<string, object?> meta, string phase)
            => HardwareReadOnFresh(() => HardwareGroupOperations.Find(composition(), name), meta, phase, null);

        private static T HardwareReadOnFresh<T>(Func<T> read, Dictionary<string, object?> meta, string phase, T disposedValue)
        {
            try { return read(); }
            catch (Exception ex) when (HardwareDisposedOnly(ex))
            {
                meta[phase + "Enumeration"] = "EngineeringObjectDisposedException on the refreshed composition (TIA released a proxy): "
                    + NativeFailureText.Compose(ex.GetBaseException().Message, null, Array.Empty<string>());
                return disposedValue;
            }
        }

        private static int? HardwareCountOnFresh(Func<object> composition, Dictionary<string, object?> meta, string key)
        {
            try { int n = HardwareGroupOperations.Items(composition()).Count(); meta[key] = n; return n; }
            catch (Exception ex) when (HardwareDisposedOnly(ex)) { meta[key] = null; meta[key + "Error"] = ex.GetBaseException().Message; return null; }
        }

        private static Channel HardwareExactChannel(DeviceItem item, string channelType, string channelIoType, int channelNumber)
            => item.Channels
                .Find((ChannelType)Enum.Parse(typeof(ChannelType), channelType), (ChannelIoType)Enum.Parse(typeof(ChannelIoType), channelIoType), channelNumber)
               ?? throw new HardwareAddressingException("NotFound", "Channel not found: " + channelType + "/" + channelIoType + "/" + channelNumber);

        private static object? HardwareLinkedTagRows(Channel channel, Dictionary<string, object?> row)
        {
#if !PLC_HARDWARE_LINKED_TAGS
            row["linkedTagsNote"] = "PlcTagProvider.GetLinkedTags exists in the V21 PublicAPI only.";
            return null;
#else
            PlcTagProvider? provider = channel.GetService<PlcTagProvider>();
            if (provider == null) { row["linkedTagsNote"] = "PlcTagProvider unavailable on this channel."; return null; }
            IList<PlcTag> tags = provider.GetLinkedTags();
            return new List<object
                ?>(tags
                    .Select(t => (object)new Dictionary<string, object?> {
                        ["name"] = t.Name, ["dataTypeName"] = t.DataTypeName, ["logicalAddress"] = t.LogicalAddress, ["tagTable"] = (t.Parent as PlcTagTable)?
                            .Name
                    })
                    .ToArray());
#endif
        }
    }
}
