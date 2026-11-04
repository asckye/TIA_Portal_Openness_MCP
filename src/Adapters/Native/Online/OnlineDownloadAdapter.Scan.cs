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
        private sealed class PcInterfacePick
        {
            public object Interface = null!;
            public string ModeName = "";
            public string Name = "";
            public int Number;
            public List<string> Addresses = new List<string>();
        }

        private List<PcInterfacePick> EnumeratePcInterfaces(object? connectionConfiguration)
        {
            var list = new List<PcInterfacePick>();
            if (connectionConfiguration == null) return list;
            foreach (var mode in _session.EnumerateReflectedProperty(connectionConfiguration, "Modes"))
                foreach (var pcInterface in _session.EnumerateReflectedProperty(mode, "PcInterfaces"))
                {
                    if (pcInterface == null) continue;
                    list.Add(new PcInterfacePick
                    {
                        Interface = pcInterface, ModeName = _session.ReadReflectedString(mode, "Name"), Name = _session
                            .ReadReflectedString(pcInterface, "Name"),
                        Number = ReadReflectedInt(pcInterface, "Number"), Addresses = ReadConfigurationAddresses(pcInterface)
                    });
                }
            return list;
        }

        private static HmiArray DescribePcInterfaces(IEnumerable<PcInterfacePick> picks)
            =>
                new HmiArray(picks
                    .Select(p => (object)new HmiObject {
                        ["mode"] = p.ModeName, ["name"] = p.Name, ["number"] = p.Number, ["addresses"] = string.Join(", ", p.Addresses) })
                    .ToArray());

        // 精确按名称或编号选择 PG/PC 接口；只有一个时允许省略。绝不做子串猜测。
        private static PcInterfacePick PickPcInterface(List<PcInterfacePick> picks, string pgPcInterface)
        {
            if (picks.Count == 0) throw new PortalException(PortalErrorCode.NotFound, "No PG/PC interface available in this connection configuration.");
            if (string.IsNullOrWhiteSpace(pgPcInterface))
            {
                var single = SameAdapter(picks);
                if (single != null) return single;
                throw new ArgumentException("pgPcInterface is required because several PG/PC interfaces exist: " + string
                        .Join("; ", picks.Select(p => $"{p.Name} (#{p.Number}, {p.ModeName})")));
            }
            var matches = picks
                .Where(p => string.Equals(p.Name, pgPcInterface, StringComparison.Ordinal) || (int.TryParse(pgPcInterface, out var n) && p.Number == n))
                .ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0)
                throw new PortalException(PortalErrorCode.NotFound, "PG/PC interface not found (exact name or number required): " + pgPcInterface);
            var same = SameAdapter(matches);
            if (same != null) return same;
            throw new ArgumentException("pgPcInterface is ambiguous; use the interface number: " + string
                    .Join("; ", matches.Select(p => $"{p.Name} (#{p.Number}, {p.ModeName})")));
        }

        // Historical native observation: the project-level StationUploadProvider configuration lists the same adapter once per
        // connection mode ("PLCSIM (#1)" under PN/IE, PROFIBUS and MPI), so an exact name matched three picks and was refused as
        // ambiguous. Picks that share name and number are one adapter; the PN/IE mode wins, otherwise the first.
        private static PcInterfacePick? SameAdapter(List<PcInterfacePick> picks)
        {
            if (picks.Count == 0) return null;
            if (picks.Select(p => p.Name + "#" + p.Number).Distinct(StringComparer.Ordinal).Count() != 1) return null;
            return picks.FirstOrDefault(p => string.Equals(p.ModeName, "PN/IE", StringComparison.OrdinalIgnoreCase)) ?? picks[0];
        }

        // 在所选 PG/PC 接口的 TargetInterfaces.Addresses 中精确匹配目标地址。扫描后 TIA 才会填充可达目标。
        private ConfigurationAddress? FindTargetAddress(PcInterfacePick pick, string targetIpAddress, List<string> seen)
        {
            foreach (var target in _session.EnumerateReflectedProperty(pick.Interface, "TargetInterfaces"))
                foreach (var address in _session.EnumerateReflectedProperty(target, "Addresses"))
                {
                    var value = _session.ReadReflectedString(address, "Address");
                    if (!string.IsNullOrWhiteSpace(value)) seen.Add(value);
                    if (string.Equals(value, targetIpAddress, StringComparison.OrdinalIgnoreCase) && address is ConfigurationAddress typed) return typed;
                }
            // subnet / gateway addresses of the PC interface (where TIA lists configured CPU addresses)
            var viaSubnet = FindSubnetOrGatewayAddress(pick.Interface, targetIpAddress, out _);
            if (viaSubnet != null) return viaSubnet;
            return null;
        }

        // an address that ScanAccessibleDevices reported on the same PG/PC interface (IP or MAC of a station TIA has not
        // seen in the route tree) is created on the first target interface - or, without target interfaces (project-level
        // StationUploadProvider), on the first subnet - through the official ConfigurationAddressComposition.Create.
        private static ConfigurationAddress? CreateScannedAddress(PcInterfacePick pick, string targetAddress, out string note)
        {
#if ONLINE_SCAN
            note = "";
            if (pick.Interface is not ConfigurationPcInterface typed) { note = "PC interface is not typed"; return null; }
            var errors = new List<string>();
            try
            {
                var accessible = typed.GetAccessibleDevices() ?? new List<ConfigurationAccessibleDevice>();
                if (!accessible
                        .Any(d => string.Equals(d.Address, targetAddress, StringComparison.OrdinalIgnoreCase) || string
                            .Equals(d.MACAddress, targetAddress, StringComparison.OrdinalIgnoreCase)))
                { note = "address not reported by the network scan on this PG/PC interface"; return null; }
            }
            catch (Exception ex) { note = "network scan failed: " + ex.Message; return null; }
            foreach (ConfigurationTargetInterface target in PlcGroupOperations.Items(typed.TargetInterfaces).Cast<ConfigurationTargetInterface>())
            {
                try {
                    var created = target.Addresses.Find(targetAddress) ?? target.Addresses.Create(targetAddress); note = "created on target interface " + target
                        .Name; return created;
                }
                catch (Exception ex) { errors.Add(target.Name + ": " + ex.Message); }
            }
            foreach (ConfigurationSubnet subnet in PlcGroupOperations.Items(typed.Subnets).Cast<ConfigurationSubnet>())
            {
                try {
                    var created = subnet.Addresses.Find(targetAddress) ?? subnet.Addresses.Create(targetAddress); note = "created on subnet " + subnet.Name;
                    return created;
                }
                catch (Exception ex) { errors.Add("subnet " + subnet.Name + ": " + ex.Message); }
            }
            // Historical native observation: the project-level StationUploadProvider lists the PC interface with neither target interfaces nor
            // subnets, so the last composition left is the PC interface's own ConfigurationPcInterface.Addresses.
            try {
                var created = typed.Addresses.Find(targetAddress) ?? typed.Addresses.Create(targetAddress); note = "created on PC interface " + typed.Name;
                return created;
            }
            catch (Exception ex) { errors.Add("PC interface " + typed.Name + ": " + ex.Message); }
            note = errors.Count == 0 ? "no target interface, subnet or PC interface composition to create the address on" : string.Join("; ", errors);
            return null;
        
#else
            note = "Accessible-device scanning requires V19 or later; use an existing configured route.";
            return null;
#endif
        }

#if ONLINE_DOWNLOAD
        private ConnectionConfiguration ResolveScanConfiguration(string softwarePath, HmiObject meta)
        {
            if (!string.IsNullOrWhiteSpace(softwarePath))
            {
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var provider = _session.ResolvePlcService<DownloadProvider>(softwarePath, plc)
                    ?? throw new PortalException(PortalErrorCode.NotFound, "DownloadProvider unavailable for " + softwarePath);
                meta["configurationSource"] = "DownloadProvider:" + softwarePath;
                return provider.Configuration ?? throw new PortalException(PortalErrorCode.InvalidState, "PLC has no connection configuration.");
            }
            var upload = _session.CurrentProject!.GetService<StationUploadProvider>()
                ?? throw new NotSupportedException("StationUploadProvider service unavailable on this project/version; pass softwarePath to scan through an " +
                "existing PLC instead.");
            meta["configurationSource"] = "StationUploadProvider";
            return upload.Configuration ?? throw new PortalException(PortalErrorCode.InvalidState, "StationUploadProvider has no connection configuration.");
        }
#endif

#if ONLINE_SCAN
        public HmiMessage ScanAccessibleDevices(string pgPcInterface = "", string softwarePath = "", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("ScanAccessibleDevices", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset >= 0 and 1 <= limit <= 500 required.");
                var configuration = ResolveScanConfiguration(softwarePath, meta);
                var picks = EnumeratePcInterfaces(configuration);
                meta["pgPcInterfaces"] = DescribePcInterfaces(picks);
                var pick = PickPcInterface(picks, pgPcInterface);
                meta["pgPcInterface"] = new HmiObject { ["mode"] = pick.ModeName, ["name"] = pick.Name, ["number"] = pick.Number };
                if (pick.Interface is not ConfigurationPcInterface typed)
                    throw new NotSupportedException("Selected PG/PC interface is not a ConfigurationPcInterface.");
                var devices = typed.GetAccessibleDevices() ?? new List<ConfigurationAccessibleDevice>();
                meta["apiCallSuccess"] = true;
                var rows = devices
                    .Select(d => (object)new HmiObject {
                        ["name"] = d.Name, ["address"] = d.Address, ["macAddress"] = d.MACAddress, ["deviceSeries"] = d.DeviceSeries })
                    .ToList();
                meta["total"] = rows.Count; meta["offset"] = offset; meta["limit"] = limit;
                meta["devices"] = new HmiArray(rows.Skip(offset).Take(limit).ToArray());
                meta["dataComplete"] = offset + limit >= rows.Count;
                return rows.Count == 0 ? "Network scan returned no accessible devices on the selected PG/PC interface."
                    : $"{rows.Count} accessible device(s) found by a live network scan; no project change.";
            });
#endif

    }
}
