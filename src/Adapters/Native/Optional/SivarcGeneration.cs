using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Optional;

internal static class SivarcGeneration
{
    // Generate takes a device name. A Unified connection names the CPU item that owns the software container.
    internal static (string Device, string Partner) Plc(SoftwareContainer? container, string softwarePath)
    {
        if (container == null) throw new PlcSoftwareException("NotFound", "Exact PLC software not found: " + softwarePath);
        var cpu = container.Parent;
        string device = OptionalNativeOperations.OwningDeviceName(cpu, value => (value as IEngineeringInstance)?.Parent,
            value => value is Device owner ? owner.Name : null);
        return (device, (cpu as DeviceItem)?.Name ?? "");
    }

    internal static void CheckConnections(IReadOnlyList<Device> targets, string[] partners, HmiObject meta)
    {
        var unverified = new List<string>();
        for (int i = 0; i < targets.Count; i++)
        {
            var device = targets[i];
            var connected = new List<string>();
            bool hasHmi = false;
            bool complete = true;
            try
            {
                foreach (var root in device.DeviceItems)
                foreach (var (item, _) in PlcFoundationEngine.HardwareTraverseDeviceItems(root, root.Name))
                {
                    var software = ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>()?.Software;
                    if (software is Siemens.Engineering.Hmi.HmiTarget classic)
                    {
                        hasHmi = true;
                        // Classic exposes connection names only. An empty collection can still prove absence.
                        if (classic.Connections.Count > 0) complete = false;
                    }
#if HMI_UNIFIED
                    else if (software is Siemens.Engineering.HmiUnified.HmiSoftware unified)
                    {
                        hasHmi = true;
                        connected.AddRange(unified.Connections.Select(connection => connection.Partner));
                    }
#endif
                    else if (software != null && software is not Siemens.Engineering.SW.PlcSoftware)
                    {
                        // Earlier SDKs can return panel software whose connection partners they cannot expose.
                        complete = false;
                    }
                }
            }
            catch (Exception error) when ((error is EngineeringException or InvalidOperationException or NotSupportedException)
                && !HmiReadPolicy.ConnectionUnavailable(error))
            {
                complete = false;
            }
            if (complete)
                OptionalNativeOperations.RequireGenerationConnections(device.Name, partners, connected, hasHmi,
                    i == 0 ? "hmiDeviceName" : "additionalHmiDeviceNames");
            else unverified.Add(device.Name);
        }
        meta["connectionPartnersVerified"] = unverified.Count == 0;
        if (unverified.Count > 0) meta["unverifiedHmiDeviceNames"] = new HmiArray(unverified.ToArray());
    }
}
