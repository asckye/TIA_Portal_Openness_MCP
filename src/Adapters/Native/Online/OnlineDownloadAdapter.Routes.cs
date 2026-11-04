using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Reflection;
using System.Threading;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using TiaMcpServer.Siemens;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Online;
#if ONLINE_LEGITIMATION
using Siemens.Engineering.Online.Configurations;
#endif
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
#if ONLINE_DOWNLOAD
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Upload;
using Siemens.Engineering.Upload.Configurations;
#endif
namespace TiaMcp.Adapters.Native.Online
{
    // Historical native observations lack a recorded PLCSIM version/date; see docs/reference/real-machine-ledger.md.
    internal sealed partial class OnlineDownloadAdapter
    {
        // ---- PG/PC route selection (issue #14) --------------------------------------------------
        // V21: ConnectionConfiguration.ApplyConfiguration(ConfigurationTargetInterface) returns a
        // bool (whether the online route applied) — it does NOT return the IConfiguration. The
        // ConfigurationTargetInterface itself IS an IConfiguration (verified against the V21
        // PublicAPI), so we hand the target to Download().
        //
        // The route tree is Modes -> PcInterfaces -> TargetInterfaces. Picking the FIRST applicable
        // target was enough on a single-NIC PC, but on a multi-NIC PC (WLAN + VPN + PLCSIM virtual
        // adapter) TIA enumerates the wrong adapter first and ApplyConfiguration "succeeds" on it
        // too — it does not verify reachability. The download then leaves through an adapter that
        // cannot see the CPU and fails with "connection to the target module cannot be established".
        // So: enumerate every route, rank by IP proximity to the CPU, and apply the best one.

        // One flattened Modes -> PcInterfaces -> TargetInterfaces route, with the addresses on both
        // ends so the adapter facing the CPU can be identified (and reported back to the caller).
        private sealed class DownloadRoute
        {
            public object Target = null!;
            public string ModeName = string.Empty;
            public string PcInterfaceName = string.Empty;
            public int PcInterfaceNumber;
            public List<string> PcAddresses = new List<string>();
            public string TargetName = string.Empty;
            public List<string> TargetAddresses = new List<string>();
            public int Score;

            public string Describe() =>
                $"{ModeName} / {PcInterfaceName}"
                + (PcAddresses.Count > 0 ? $" [{string.Join(", ", PcAddresses)}]" : " [no IP]")
                + $" -> {TargetName}"
                + (TargetAddresses.Count > 0 ? $" [{string.Join(", ", TargetAddresses)}]" : string.Empty);
        }

        private sealed class DownloadRouteSelection
        {
            public object? Configuration;   // what to hand to Download(); null = fall back to the raw configuration
            public string Description = "(no route selected — raw connection configuration)";
            public string? Error;           // set when an explicit pgPcInterface/targetIpAddress filter matched nothing
            public List<DownloadRoute> Candidates = new List<DownloadRoute>();
            // the exact ConfigurationAddress when the caller named a target IP. It comes from the target interface,
            // from the PC interface's subnet / gateway (where TIA lists the CPU's configured IP - the target interface itself
            // stays empty until the PG adapter can see the CPU), or it is created on the target interface (official
            // ConfigurationAddressComposition.Create; first download to a PLCSIM Advanced instance / a factory-new CPU).
            public ConfigurationAddress? Address;
            public string AddressSource = "";   // targetInterface | subnet | gateway | created | not-created (preview)
            public object? Target;              // the ConfigurationTargetInterface the address belongs to (for the 5-arg Download overload)
            public bool Applied;                // ConnectionConfiguration.ApplyConfiguration returned true for the selection
        }

        // Typed walk of one PC interface's subnets and gateways for an exact address (historical native observation: the route tree of a
        // PLCSIM Advanced target listed 192.168.0.1 only under PcInterface.Subnets["MCP_PN"].Addresses, never under 1 X1).
        private ConfigurationAddress? FindSubnetOrGatewayAddress(object? pcInterface, string ipAddress, out string source)
        {
            source = "";
            if (pcInterface is not ConfigurationPcInterface typed) return null;
            try
            {
                foreach (ConfigurationSubnet subnet in PlcGroupOperations.Items(typed.Subnets).Cast<ConfigurationSubnet>())
                {
                    foreach (ConfigurationAddress address in PlcGroupOperations.Items(subnet.Addresses).Cast<ConfigurationAddress>())
#if ONLINE_ADDRESSES
                        if (string.Equals(address.Address, ipAddress, StringComparison.OrdinalIgnoreCase)) { source = "subnet " + subnet.Name; return address; }
#else
                        if (string.Equals(OnlineRoutePrimitives.Address(address), ipAddress, StringComparison.OrdinalIgnoreCase)) {
                            source = "subnet " + subnet.Name; return address; }
#endif
                    foreach (ConfigurationGateway gateway in PlcGroupOperations.Items(subnet.Gateways).Cast<ConfigurationGateway>())
                        foreach (ConfigurationAddress address in PlcGroupOperations.Items(gateway.Addresses).Cast<ConfigurationAddress>())
#if ONLINE_ADDRESSES
                            if (string.Equals(address.Address, ipAddress, StringComparison.OrdinalIgnoreCase)) {
                                source = "gateway " + gateway.Name + " of subnet " + subnet.Name; return address; }
#else
                            if (string.Equals(OnlineRoutePrimitives.Address(address), ipAddress, StringComparison.OrdinalIgnoreCase)) {
                                source = "gateway " + gateway.Name + " of subnet " + subnet.Name; return address; }
#endif
                }
            }
            catch /* swallow(native-fallback): an unavailable subnet or gateway address leaves target-interface address creation available */ { }
            return null;
        }

        // Official ConfigurationAddressComposition.Create(address) on a target interface (V20 and V21). Returns null when the
        // composition refuses (the exception text is handed back for the error message).
        private static ConfigurationAddress? TryCreateTargetAddress(object? target, string ipAddress, out string error)
        {
#if ONLINE_TARGET_ADDRESSES
            error = "";
            if (target is not ConfigurationTargetInterface typed) { error = "target interface is not a ConfigurationTargetInterface"; return null; }
            try
            {
                var existing = typed.Addresses.Find(ipAddress);
                if (existing != null) return existing;
                return typed.Addresses.Create(ipAddress);
            }
            catch (Exception ex) { error = ex.Message; return null; }
        
#else
            error = "Target-interface address creation requires V17 or later.";
            return null;
#endif
        }

        private List<DownloadRoute> EnumerateDownloadRoutes(object? connectionConfiguration)
        {
            var routes = new List<DownloadRoute>();
            if (connectionConfiguration == null) return routes;
            foreach (var mode in _session.EnumerateReflectedProperty(connectionConfiguration, "Modes"))
                foreach (var pcInterface in _session.EnumerateReflectedProperty(mode, "PcInterfaces"))
                    foreach (var target in _session.EnumerateReflectedProperty(pcInterface, "TargetInterfaces"))
                    {
                        if (target == null) continue;
                        routes.Add(new DownloadRoute
                        {
                            Target = target,
                            ModeName = _session.ReadReflectedString(mode, "Name"),
                            PcInterfaceName = _session.ReadReflectedString(pcInterface, "Name"),
                            PcInterfaceNumber = ReadReflectedInt(pcInterface, "Number"),
                            PcAddresses = ReadConfigurationAddresses(pcInterface),
                            TargetName = _session.ReadReflectedString(target, "Name"),
                            TargetAddresses = ReadConfigurationAddresses(target)
                        });
                    }
            return routes;
        }

        private static object? ReadReflectedParent(object? owner)
        {
            try { return owner?.GetType().GetProperty("Parent")?.GetValue(owner); }
            catch /* swallow(probe-optional): an unavailable parent leaves route selection on the target interface */ { return null; }
        }

        private static int ReadReflectedInt(object? owner, string propertyName)
        {
            try { return owner?.GetType().GetProperty(propertyName)?.GetValue(owner) is int number ? number : 0; }
            catch /* swallow(probe-optional): an unavailable interface number retains the default route diagnostic */ { return 0; }
        }

        // ConfigurationPcInterface.Addresses = the PG/PC adapter's own IPs.
        // ConfigurationTargetInterface.Addresses = the CPU interface's IPs.
        // Both are ConfigurationAddress compositions whose Address property holds the IP string.
        private List<string> ReadConfigurationAddresses(object? owner)
        {
            var addresses = new List<string>();
            foreach (var address in _session.EnumerateReflectedProperty(owner, "Addresses"))
            {
                var value = _session.ReadReflectedString(address, "Address");
                if (!string.IsNullOrWhiteSpace(value)) addresses.Add(value);
            }
            return addresses;
        }

        // Rough "can these two adapters see each other" test. ConfigurationAddress exposes only the
        // address, never the mask, so /24 is an assumption — it holds for the usual 192.168.x.y /
        // 10.x.y.z engineering subnets, and it is only ever used to RANK candidates, never to reject
        // a download outright.
        private static bool SameIpv4Subnet24(string a, string b)
        {
            var left = a.Split('.');
            var right = b.Split('.');
            if (left.Length != 4 || right.Length != 4) return false;
            return left[0] == right[0] && left[1] == right[1] && left[2] == right[2];
        }

        private static void ScoreDownloadRoutes(List<DownloadRoute> routes, string? preferredTargetIp)
        {
            foreach (var route in routes)
            {
                var score = 0;
                if (!string.IsNullOrWhiteSpace(preferredTargetIp))
                {
                    if (route.TargetAddresses.Any(t => string.Equals(t, preferredTargetIp, StringComparison.OrdinalIgnoreCase)))
                        score += 8;
                    if (route.PcAddresses.Any(p => SameIpv4Subnet24(p, preferredTargetIp!)))
                        score += 4;
                }
                // No explicit target IP: the CPU address on the route itself is the reference point.
                // Prefer the adapter sitting in the same subnet as the CPU it has to reach — that is
                // exactly what separates the PLCSIM virtual adapter from a WLAN/VPN adapter.
                if (route.PcAddresses.Any(p => route.TargetAddresses.Any(t => SameIpv4Subnet24(p, t))))
                    score += 2;
                route.Score = score;
            }
        }

        private static string DescribeRoutes(IEnumerable<DownloadRoute> routes)
            => string.Join(" | ", routes.Select(r => r.Describe()));

        // Returns the IConfiguration to pass to Download() / GoOnline(), or a selection carrying an Error when an explicit filter
        // matched nothing or an explicitly requested route was not applied (ApplyConfiguration returned false or threw).
        // Configuration stays null when no route exists at all; callers refuse then (a raw ConnectionConfiguration is not an
        // IConfiguration). apply=false is the side-effect-free preview: it enumerates and ranks only - no ApplyConfiguration and no
        // ConfigurationAddressComposition.Create - and reports what execution would apply or create.
        private DownloadRouteSelection SelectDownloadRoute(
            object? connectionConfiguration,
            string? pgPcInterface,
            string? targetIpAddress,
            bool apply = true)
        {
            var selection = new DownloadRouteSelection();
            if (connectionConfiguration == null) return selection;
            bool explicitRoute = !string.IsNullOrWhiteSpace(pgPcInterface) || !string.IsNullOrWhiteSpace(targetIpAddress);

            try
            {
                selection.Candidates = EnumerateDownloadRoutes(connectionConfiguration);
                if (selection.Candidates.Count == 0) return selection;

                var pool = selection.Candidates;

                if (!string.IsNullOrWhiteSpace(pgPcInterface))
                {
                    var byName = pool
                        .Where(r => r.PcInterfaceName.IndexOf(pgPcInterface, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                    if (byName.Count == 0)
                    {
                        selection.Error =
                            $"No PG/PC interface matches '{pgPcInterface}'. Available routes: {DescribeRoutes(pool)}";
                        return selection;
                    }
                    pool = byName;
                }

                if (!string.IsNullOrWhiteSpace(targetIpAddress))
                {
                    var byIp = pool
                        .Where(r => r.TargetAddresses.Any(t => string.Equals(t, targetIpAddress, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    if (byIp.Count > 0)
                    {
                        pool = byIp;
                        foreach (var route in pool)
                            foreach (var candidate in _session.EnumerateReflectedProperty(route.Target, "Addresses"))
#if ONLINE_ADDRESSES
                                if (selection.Address == null && candidate is ConfigurationAddress typed && string
                                        .Equals(typed.Address, targetIpAddress, StringComparison.OrdinalIgnoreCase))
#else
                                if (selection.Address == null && candidate is ConfigurationAddress typed && string
                                        .Equals(OnlineRoutePrimitives.Address(typed), targetIpAddress, StringComparison.OrdinalIgnoreCase))
#endif
                                { selection.Address = typed; selection.AddressSource = "targetInterface"; selection.Target = route.Target; }
                    }
                    else
                    {
                        // the CPU's configured IP is usually listed under the PC interface's subnet (or a gateway) while the
                        // target interface carries no address at all; a subnet / gateway ConfigurationAddress is an IConfiguration.
                        var byPcInterface = pool.GroupBy(r => r.PcInterfaceName + "#" + r.PcInterfaceNumber).ToList();
                        foreach (var group in byPcInterface)
                        {
                            var first = group.First();
                            var pcInterface = ReadReflectedParent(first.Target);
                            var found = FindSubnetOrGatewayAddress(pcInterface, targetIpAddress!, out var source);
                            if (found == null) continue;
                            selection.Address = found; selection.AddressSource = source; selection.Target = first.Target;
                            pool = group.ToList();
                            break;
                        }
                        if (selection.Address == null)
                        {
                            // Nothing lists the address: create it on the first target interface of the (filtered) pool - official
                            // ConfigurationAddressComposition.Create. This is the first download to a PLCSIM Advanced instance or a
                            // factory-new CPU whose address TIA has not seen yet.
                            var ordered = pool.OrderBy(r => r.TargetName.IndexOf("X1", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).ToList();
                            if (!apply)
                            {
                                // Preview: the address would be created on the first ordered target interface after approval.
                                selection.AddressSource = "not-created";
                                selection.Target = ordered[0].Target;
                                selection.Configuration = ordered[0].Target;
                                selection.Description = ordered[0].Describe() + " -> address " + targetIpAddress
                                    + " (not in the route tree; execution creates it with ConfigurationAddressComposition.Create; preview: nothing applied " +
                                    "or created)";
                                return selection;
                            }
                            var errors = new List<string>();
                            foreach (var route in ordered)
                            {
                                var created = TryCreateTargetAddress(route.Target, targetIpAddress!, out var error);
                                if (created == null) { errors.Add(route.TargetName + ": " + error); continue; }
                                selection.Address = created; selection.AddressSource = "created"; selection.Target = route.Target;
                                route.TargetAddresses.Add(targetIpAddress!);
                                pool = new List<DownloadRoute> { route };
                                break;
                            }
                            if (selection.Address == null)
                            {
                                selection.Error =
                                    $"No download route reaches target IP '{targetIpAddress}'. The address could not be created on a target interface either ({string.Join("; ", errors)}). Available routes: {DescribeRoutes(pool)}";
                                return selection;
                            }
                        }
                    }
                }

                ScoreDownloadRoutes(pool, targetIpAddress);

                if (!apply)
                {
                    var best = pool.OrderByDescending(r => r.Score).First();
                    selection.Target ??= best.Target;
                    selection.Configuration = selection.Address != null && selection.AddressSource != "targetInterface" ? selection.Address : selection.Target;
#if ONLINE_ADDRESSES
                    string addressText = selection.Address == null ? "" : " -> address " + selection.Address.Address + " (" + selection.AddressSource + ")";
#else
                    string addressText = selection.Address == null ? "" : " -> address " + OnlineRoutePrimitives.Address(selection.Address) + " (" + selection
                        .AddressSource + ")";
#endif
                    selection.Description = best.Describe() + addressText + " (preview: ApplyConfiguration not called)";
                    return selection;
                }

                // A subnet / gateway / created address is applied as such (ConnectionConfiguration.ApplyConfiguration(ConfigurationAddress))
                // and handed to Download / GoOnline as the IConfiguration; the target interface is kept for the 5-arg Download overload.
                // An address listed on the target interface keeps the field-verified path below (apply + hand over the target interface).
                // Such an address is only selected for an explicit target IP, so a route that is not applied is refused.
                if (selection.Address != null && selection.AddressSource != "targetInterface"
                        && connectionConfiguration is ConnectionConfiguration typedConfiguration)
                {
                    bool applied = false;
                    string? applyError = null;
                    try { applied = typedConfiguration.ApplyConfiguration(selection.Address); }
                    catch (Exception ex) { applyError = ex.GetBaseException().Message; }
                    selection.Configuration = selection.Address;
                    selection.Applied = applied;
                    var route = pool.OrderByDescending(r => r.Score).First();
#if ONLINE_ADDRESSES
                    selection.Description = route.Describe() + " -> address " + selection.Address.Address + " (" + selection.AddressSource
                        + (applied ? "" : "; not confirmed by ApplyConfiguration") + ")";
#else
                    selection.Description = route.Describe() + " -> address " + OnlineRoutePrimitives.Address(selection.Address) + " (" + selection
                        .AddressSource + (applied ? "" : "; not confirmed by ApplyConfiguration") + ")";
#endif
                    selection.Error = OnlineTransitionPolicy.RouteApplyRefusal(explicitRoute, applied, applyError, selection.Description);
                    return selection;
                }

                var applyMethod = connectionConfiguration.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ApplyConfiguration"
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType.Name == "ConfigurationTargetInterface");

                // OrderByDescending is a stable sort, so equal scores keep the original enumeration
                // order — i.e. the old first-wins behaviour whenever nothing distinguishes adapters.
                var ranked = pool.OrderByDescending(r => r.Score).ToList();
                var applyErrors = new List<string>();

                foreach (var route in ranked)
                {
                    try
                    {
                        if (applyMethod?.Invoke(connectionConfiguration, new[] { route.Target }) is bool ok && ok)
                        {
                            selection.Configuration = route.Target;
                            selection.Description = route.Describe();
                            selection.Applied = true;
                            return selection;
                        }
                        applyErrors.Add(route.Describe() + ": ApplyConfiguration returned false");
                    }
                    catch (Exception ex)
                    {
                        // Try the next ranked route; the reason is kept for the refusal of an explicit route.
                        var real = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                        applyErrors.Add(route.Describe() + ": " + real.Message);
                    }
                }

                var refusal = OnlineTransitionPolicy.RouteApplyRefusal(explicitRoute, false, string.Join("; ", applyErrors), DescribeRoutes(ranked));
                if (refusal != null)
                {
                    selection.Error = refusal;
                    return selection;
                }
                // Automatic pick only: nothing applied cleanly, so hand back the best-ranked target anyway (it IS an
                // IConfiguration), marked as not confirmed. Mirrors the previous fallback to the first target.
                selection.Configuration = ranked[0].Target;
                selection.Description = ranked[0].Describe() + " (not confirmed by ApplyConfiguration"
                    + (applyErrors.Count > 0 ? ": " + string.Join("; ", applyErrors) : "") + ")";
                return selection;
            }
            catch (Exception ex)
            {
                // The route tree could not be walked; callers refuse instead of handing the raw configuration to the native call.
                selection.Error ??= "Route selection failed: " + ex.GetBaseException().Message;
            }
            return selection;
        }

        // ---- transfer routes and R/H providers --------------------------------------------------------------------------------
#if ONLINE_ADDRESSES
        private static HmiArray AddressRows(IEnumerable<ConfigurationAddress> addresses) =>
            new HmiArray(PlcGroupOperations.Items(addresses).Cast<ConfigurationAddress>()
                .Select(a => (object)new HmiObject { ["name"] = a.Name, ["address"] = a.Address }).ToArray());
#else
        private static HmiArray AddressRows(IEnumerable<ConfigurationAddress> addresses) =>
            new HmiArray(PlcGroupOperations.Items(addresses).Cast<ConfigurationAddress>()
                .Select(a => (object)new HmiObject { ["name"] = a.Name, ["address"] = OnlineRoutePrimitives.Address(a) }).ToArray());
#endif

        public HmiMessage ReadTransferRoutes(string softwarePath, int maxItems = 500)
            => _session.RunHmiStepTool("ListTransferRoutes", meta => {
                if (maxItems < 1 || maxItems > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
                var plc = _session.GetPlcSoftware(softwarePath)
                    ?? throw new PortalException(PortalErrorCode.NotFound, "Exact PLC software not found: " + softwarePath + _session
                        .AvailablePlcPathsSuffix());
#if ONLINE_DOWNLOAD
                var download = _session.ResolvePlcService<DownloadProvider>(softwarePath, plc);
                meta["downloadProviderAvailable"] = download != null;
                meta["routeProvider"] = "DownloadProvider";
#else
                // V14 SP1 has no DownloadProvider; the route tree is read from OnlineProvider.Configuration.
                var download = _session.ResolvePlcService<OnlineProvider>(softwarePath, plc);
                meta["downloadProviderAvailable"] = false;
                meta["routeProvider"] = "OnlineProvider";
#endif
                int items = 0; bool truncated = false;
                if (download != null)
                {
                    ConnectionConfiguration configuration = download.Configuration;
                    var modes = new HmiArray(); meta["modes"] = modes;
                    foreach (ConfigurationMode mode in PlcGroupOperations.Items(configuration.Modes).Cast<ConfigurationMode>())
                    {
                        var modeRow = new HmiObject { ["name"] = mode.Name }; var interfaces = new HmiArray(); modeRow["pcInterfaces"] = interfaces; modes
                            .Add(modeRow);
                        foreach (ConfigurationPcInterface pcInterface in PlcGroupOperations.Items(mode.PcInterfaces).Cast<ConfigurationPcInterface>())
                        {
                            if (++items > maxItems) { truncated = true; break; }
                            var row = new HmiObject { ["name"] = pcInterface.Name, ["number"] = pcInterface.Number };
                            try
                            {
#if ONLINE_ADDRESSES
                                row["addresses"] = AddressRows(pcInterface.Addresses);
#else
                                row["addresses"] = AddressRows(OnlineRoutePrimitives.PcAddresses(pcInterface));
#endif
                            }
                            catch (Exception ex) { row["addressesError"] = ex.GetBaseException().Message; }
                            try
                            {
                                row["subnets"] =
                                    new HmiArray(PlcGroupOperations.Items(pcInterface.Subnets).Cast<ConfigurationSubnet>().Select(s => (object)new HmiObject
                                {
                                    ["name"] = s.Name, ["addresses"] = AddressRows(s.Addresses),
                                    ["gateways"] =
                                        new HmiArray(PlcGroupOperations.Items(s.Gateways).Cast<ConfigurationGateway>()
                                            .Select(g => (object)new HmiObject { ["name"] = g.Name, ["addresses"] = AddressRows(g.Addresses) }).ToArray())
                                }).ToArray());
                            }
                            catch (Exception ex) { row["subnetsError"] = ex.GetBaseException().Message; }
                try
                {
#if ONLINE_TARGET_ADDRESSES
                    row["targetInterfaces"] =
                        new HmiArray(PlcGroupOperations.Items(pcInterface.TargetInterfaces).Cast<ConfigurationTargetInterface>()
                            .Select(t => (object)new HmiObject { ["name"] = t.Name, ["addresses"] = AddressRows(t.Addresses) }).ToArray());
#else
                    row["targetInterfaces"] =
                        new HmiArray(PlcGroupOperations.Items(pcInterface.TargetInterfaces).Cast<ConfigurationTargetInterface>()
                            .Select(t => (object)new HmiObject { ["name"] = t.Name, ["addresses"] = AddressRows(OnlineRoutePrimitives.TargetAddresses(t)) })
                            .ToArray());
#endif
                }
                            catch (Exception ex) { row["targetInterfacesError"] = ex.GetBaseException().Message; }
                            interfaces.Add(row);
                        }
                        if (truncated) break;
                    }
                }
                // R/H systems expose the redundant providers instead of / next to the standard ones; the CPU on the reference project is not R/H.
#if ONLINE_RH
                var rhDownload = _session.ResolvePlcService<RHDownloadProvider>(softwarePath, plc); meta["rhDownloadProviderAvailable"] = rhDownload != null;
                var rhOnline = _session.ResolvePlcService<RHOnlineProvider>(softwarePath, plc); meta["rhOnlineProviderAvailable"] = rhOnline != null;
                if (rhOnline != null) {
                    try {
                        meta["rhOnline"] = new HmiObject {
                            ["primaryState"] = rhOnline.PrimaryState.ToString(), ["backupState"] = rhOnline.BackupState.ToString() };
                    } catch (Exception ex) { meta["rhOnlineError"] = ex.GetBaseException().Message; }
                }
#else
                meta["rhDownloadProviderAvailable"] = false; meta["rhOnlineProviderAvailable"] = false;
#endif
                var onlineProvider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plc); meta["onlineProviderAvailable"] = onlineProvider != null;
                if (onlineProvider != null) {
                    try { meta["onlineState"] = onlineProvider.State.ToString(); } catch (Exception ex) {
                        meta["onlineStateError"] = ex.GetBaseException().Message; }
                }
                meta["compileProviderNote"] =
                    "Siemens.Engineering.Compiler.CompileProvider is internal in the supplied PublicAPI (documented, not public); compilation goes through " +
                    "ICompilable.";
                meta["truncated"] = truncated; meta["apiCallSuccess"] = true; meta["dataComplete"] = !truncated;
                meta["scope"] =
                    "ConnectionConfiguration route tree of the download provider (V14 SP1: the online provider; routeProvider names it) (Modes -> " +
                    "PcInterfaces with Addresses / Subnets (Gateways) / TargetInterfaces) plus availability of RHDownloadProvider / RHOnlineProvider (with " +
                    "Primary/BackupState) / OnlineProvider. Read-only; nothing is applied.";
                return "Transfer routes read; no route applied, no modification.";
            });

    }
}
