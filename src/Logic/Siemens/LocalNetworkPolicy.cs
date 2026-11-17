using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class LocalNetworkPolicy
    {
        internal static bool SharesSubnet(string localAddress, string subnetMask, string targetAddress)
        {
            if (!IPAddress.TryParse(localAddress, out var local) || !IPAddress.TryParse(subnetMask, out var mask)
                || !IPAddress.TryParse(targetAddress, out var target) || local.AddressFamily != AddressFamily.InterNetwork
                || mask.AddressFamily != AddressFamily.InterNetwork || target.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] left = local.GetAddressBytes(), right = target.GetAddressBytes(), bits = mask.GetAddressBytes();
            for (int i = 0; i < left.Length; i++)
                if ((left[i] & bits[i]) != (right[i] & bits[i])) return false;
            return true;
        }

        internal sealed class Address
        {
            internal string Name = "", Description = "", Ip = "", Mask = "";
            internal NetworkInterfaceType Type;
            internal OperationalStatus Status;
        }

        // Local OS inventory only; no probe, DNS lookup or socket is opened.
        internal static JsonObject Inspect(string target)
        {
            var addresses = new System.Collections.Generic.List<Address>();
            bool complete = true;
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                            addresses.Add(new Address { Name = adapter.Name, Description = adapter.Description, Type = adapter.NetworkInterfaceType,
                                Status = adapter.OperationalStatus, Ip = address.Address.ToString(), Mask = address.IPv4Mask.ToString() });
                }
            }
            catch (NetworkInformationException) /* swallow(env-probe): incomplete inventory cannot establish a simulator-only subnet. */
            {
                complete = false;
            }
            return Report(target, addresses, complete);
        }

        internal static JsonObject Report(string target, System.Collections.Generic.IEnumerable<Address> addresses, bool complete = true)
        {
            var adapters = new JsonArray();
            bool matches = false, simulator = false, other = false;
            foreach (var address in addresses)
            {
                if (!SharesSubnet(address.Ip, address.Mask, target)) continue;
                bool simulated = address.Description.IndexOf("PLCSIM Virtual Ethernet Adapter", StringComparison.OrdinalIgnoreCase) >= 0;
                other |= !simulated;
                if (address.Status != OperationalStatus.Up) continue;
                matches = true;
                simulator |= simulated && SimulatorMask(address.Mask);
                bool privateAdapter = address.Type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                    || address.Description.IndexOf("VPN", StringComparison.OrdinalIgnoreCase) >= 0
                    || address.Name.IndexOf("VPN", StringComparison.OrdinalIgnoreCase) >= 0
                    || address.Description.IndexOf("TAP-Windows", StringComparison.OrdinalIgnoreCase) >= 0
                    || address.Description.IndexOf("WireGuard", StringComparison.OrdinalIgnoreCase) >= 0
                    || address.Description.IndexOf("Wintun", StringComparison.OrdinalIgnoreCase) >= 0;
                if (privateAdapter) continue;
                adapters.Add(new JsonObject { ["name"] = address.Name, ["description"] = address.Description, ["status"] = "Up",
                    ["ipv4"] = new JsonArray(new JsonObject { ["address"] = address.Ip, ["subnetMask"] = address.Mask,
                        ["sharesTargetSubnet"] = true }) });
            }
            return new JsonObject { ["targetIpAddress"] = target, ["adapters"] = adapters, ["sharesLocalSubnet"] = matches,
                ["noNonPlcsimSubnetOverlap"] = complete && !other,
                ["simulationOnlySubnet"] = complete && simulator && !other,
                ["warning"] = !complete ? "Local adapter inventory is incomplete."
                    : matches ? null : "No active local IPv4 adapter shares the target subnet. Check the PG/PC route. "
                        + "Routed networks and PLCSIM Softbus may still work; this is a warning, not a reachability test." };
        }

        private static bool SimulatorMask(string text)
        {
            if (!IPAddress.TryParse(text, out var mask) || mask.AddressFamily != AddressFamily.InterNetwork) return false;
            int prefix = 0;
            bool zero = false;
            foreach (byte part in mask.GetAddressBytes())
                for (int bit = 7; bit >= 0; bit--)
                    if ((part & (1 << bit)) == 0) zero = true;
                    else if (zero) return false;
                    else prefix++;
            return prefix >= 8;
        }
    }
}
