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

        // Local OS inventory only; no probe, DNS lookup or socket is opened.
        internal static JsonObject Inspect(string target)
        {
            var adapters = new JsonArray();
            bool matches = false;
            string? inventoryWarning = null;
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var addresses = new JsonArray();
                    foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = address.Address.ToString(), mask = address.IPv4Mask.ToString();
                        bool shares = SharesSubnet(ip, mask, target) && adapter.OperationalStatus == OperationalStatus.Up;
                        matches |= shares;
                        addresses.Add(new JsonObject { ["address"] = ip, ["subnetMask"] = mask, ["sharesTargetSubnet"] = shares });
                    }
                    adapters.Add(new JsonObject { ["name"] = adapter.Name, ["description"] = adapter.Description,
                        ["status"] = adapter.OperationalStatus.ToString(), ["ipv4"] = addresses });
                }
            }
            catch (NetworkInformationException) /* swallow(env-probe): incomplete local inventory is an explicit warning, never reachability evidence. */
            {
                inventoryWarning = "Local adapter inventory is incomplete.";
            }
            return new JsonObject { ["targetIpAddress"] = target, ["adapters"] = adapters, ["sharesLocalSubnet"] = matches,
                ["warning"] = inventoryWarning ?? (matches ? null : "No active local IPv4 adapter shares the target subnet. Check the PG/PC route. "
                    + "Routed networks and PLCSIM Softbus may still work; this is a warning, not a reachability test.") };
        }
    }
}
