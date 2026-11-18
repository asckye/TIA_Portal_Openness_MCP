using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private sealed partial class OrganisationSession
        {
            public T? ResolvePlcService<T>(string path, PlcSoftware plc, bool required = false) where T : class, IEngineeringService
            {
                var service = plc.GetService<T>();
                if (service != null) return service;
                var selected = engine.ReadSelection(path);
                var items = required ? new List<Siemens.Engineering.HW.DeviceItem>() : null;
                var cpu = selected.Context as Siemens.Engineering.HW.DeviceItem;
                for (var item = cpu; item != null;
                    item = item.Parent as Siemens.Engineering.HW.DeviceItem)
                {
                    items?.Add(item);
                    service = ((IEngineeringServiceProvider)item).GetService<T>();
                    if (service != null) return service;
                }
                service = (selected.DeviceContext as IEngineeringServiceProvider)?.GetService<T>();
                if (service != null || items == null) return service;
                var searched = new List<string> { "PlcSoftware " + selected.ExactPath };
                searched.AddRange(items.Select(owner => "DeviceItem " + owner.Name));
                if (selected.DeviceContext is IEngineeringServiceProvider) searched.Add("Device " + selected.Device);
                string? orderNumber = cpu == null ? null : TryGetEngineeringAttribute(cpu, "OrderNumber") as string;
                string? firmware = cpu == null ? null : TryGetEngineeringAttribute(cpu, "FirmwareVersion")?.ToString();
                throw PlcServiceLookup.Missing(typeof(T).Name, selected.ExactPath, cpu?.TypeIdentifier, searched, orderNumber, firmware);
            }
        }
    }
}
