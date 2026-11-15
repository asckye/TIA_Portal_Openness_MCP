using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public object? HardwareGetDeviceItemNetworkInfo(string deviceItemPath)
        {
            if (HardwareProjectMissing())
                return new Dictionary<string, object?> { ["failureCode"] = "PROJECT_NOT_BOUND", ["message"] = "No project open." };
            var di = HardwareResolveItem(deviceItemPath);
            if (di == null) return null;

            // Heuristic: filter attribute names that likely contain network addressing / interface identity.
            var keys = new[]
            {
                "ip", "ipv4", "subnet", "mask", "gateway", "mac", "pn", "profinet", "device", "station", "interface", "name", "address"
            };

            var list = new List<HardwareNetworkAttribute>();
            foreach (var info in di.GetAttributeInfos())
            {
                var n = info.Name ?? "";
                var lower = n.ToLowerInvariant();
                if (!keys.Any(k => lower.Contains(k))) continue;

                object vObj;
                try { vObj = di.GetAttribute(info.Name); }
                catch (Exception error) when (!TiaMcpServer.ModelContextProtocol.PortalFailureClassifier.IsPortalProcessLost(error)) /* swallow(probe-optional): retain the named unreadable attribute as incomplete evidence */
                {
                    list.Add(new HardwareNetworkAttribute { Name = info.Name });
                    continue;
                }

                var v = vObj?.ToString();
                if (string.IsNullOrWhiteSpace(v)) continue;

                list.Add(new HardwareNetworkAttribute
                {
                    Name = info.Name,
                    Value = v,
                    DataType = HardwareAttributeTypes(info) is { Length: > 0 } types ? types : null,
                    IsWritable = HardwareAttributeWritable(info)
                });
            }

            return list;
        }
    }
}
