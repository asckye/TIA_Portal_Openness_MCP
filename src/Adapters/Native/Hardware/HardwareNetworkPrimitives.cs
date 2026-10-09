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
            : Portal().ExclusiveAccess("MCP: verifying a precise HMI operation");
        private static IEngineeringServiceProvider HardwareServiceProvider(object target)
            => target as IEngineeringServiceProvider ?? throw new NotSupportedException("Selected object is not a service provider.");
        private static bool HardwareDisposedOnly(Exception error) => error.GetBaseException().GetType().Name == "EngineeringObjectDisposedException";
        private static void HardwarePage(object[] rows, int offset, int limit, Dictionary<string, object?> meta)
        {
            var page = rows.Skip(offset).Take(limit).ToArray(); meta["records"] = page;
            foreach (var pair in HardwareServicesPolicy.PageMeta(rows.Length, offset, limit, page.Length)) meta[pair.Key] = pair.Value;
        }
        private static DeviceItem HardwareRequireDeviceItem(HardwareObject owner, string parameter)
            => owner as DeviceItem ?? throw new ArgumentException(parameter + " must address a DeviceItem (non-empty itemPathJson).");

        private static T HardwareRequireHardwareService<T>(HardwareObject owner, string parameter) where T : class, IEngineeringService
            => HardwareServiceProvider(owner).GetService<T>() ?? throw new NotSupportedException(parameter + " does not expose " + typeof(T).Name + " (service is null for this hardware object).");

        private static object? HardwareFindOnFresh(Func<object> composition, string name, Dictionary<string, object?> meta, string phase)
        {
            try { return HardwareGroupOperations.Find(composition(), name); }
            catch (Exception ex) when (HardwareDisposedOnly(ex))
            {
                meta[phase + "Enumeration"] = "EngineeringObjectDisposedException on the refreshed composition (TIA released a proxy): " + ex.GetBaseException().Message;
                return null;
            }
        }

        private static int? HardwareCountOnFresh(Func<object> composition, Dictionary<string, object?> meta, string key)
        {
            try { int n = HardwareGroupOperations.Items(composition()).Count(); meta[key] = n; return n; }
            catch (Exception ex) when (HardwareDisposedOnly(ex)) { meta[key] = null; meta[key + "Error"] = ex.GetBaseException().Message; return null; }
        }

        private static Channel HardwareExactChannel(DeviceItem item, string channelType, string channelIoType, int channelNumber)
            => item.Channels.Find((ChannelType)Enum.Parse(typeof(ChannelType), channelType), (ChannelIoType)Enum.Parse(typeof(ChannelIoType), channelIoType), channelNumber)
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
            return new List<object?>(tags.Select(t => (object)new Dictionary<string, object?> { ["name"] = t.Name, ["dataTypeName"] = t.DataTypeName, ["logicalAddress"] = t.LogicalAddress, ["tagTable"] = (t.Parent as PlcTagTable)?.Name }).ToArray());
#endif
        }
    }
}
