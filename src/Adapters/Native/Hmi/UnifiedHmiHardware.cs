using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private string _hmiTokenGeneration = Guid.NewGuid().ToString("N");
        public Func<string, Dictionary<string, object?>, string>? HmiContentToken { get; set; }
        public Func<string, DeviceItem?>? ResolveUnifiedHmiDeviceItem { get; set; }
        internal DeviceItem? UnifiedHmiDeviceItem(string path)
            => ResolveUnifiedHmiDeviceItem != null ? ResolveUnifiedHmiDeviceItem(path) : HardwareLegacyItem(path);

        internal string UnifiedHmiToken(string scope, Dictionary<string, object?> content)
        {
            if (HmiContentToken != null) return HmiContentToken(scope, content);
            return DefaultUnifiedHmiToken(scope, content);
        }
        private string DefaultUnifiedHmiToken(string scope, Dictionary<string, object?> content)
            => HmiContentTokenPolicy.Compute(_hmiTokenGeneration, scope, content, EngineeringPlcSession.SerializeEvidence);

        internal void FillUnifiedHmiPartnerNetworkInfo(DeviceItem root, UnifiedHmiPlcPartnerInfo info)
        {
            var plcNode = FindNetworkNodes(root).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            if (plcNode?.Node == null) return;
            info.NodeName = TryGetName(plcNode.Node)
                ?? TryGetPropertyValue(plcNode.Node, "Name")?.ToString()
                ?? plcNode.Item.Name
                ?? string.Empty;
            info.InitialAddress = TryGetPropertyValue(plcNode.Node, "Address")?.ToString()
                ?? TryGetPropertyValue(plcNode.Node, "IpAddress")?.ToString()
                ?? TryGetPropertyValue(plcNode.Node, "IPAddress")?.ToString()
                ?? TryGetEngineeringAttribute(plcNode.Node, "Address")?.ToString()
                ?? TryGetEngineeringAttribute(plcNode.Node, "IpAddress")?.ToString()
                ?? string.Empty;
        }
    }
}
