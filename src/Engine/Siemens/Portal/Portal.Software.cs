using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

#if TIA_SHARED_ADAPTER_PATHS
using PlcNative = TiaMcp.Adapters.Native.Plc.PlcBlockPrimitives;
#else
using PlcNative = TiaMcpServer.Siemens.LocalPlcBlocks.PlcBlockPrimitives;
#endif

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        #region software

        public PlcSoftware? GetPlcSoftware(string softwarePath)
            => ResolvePlc(softwarePath, PlcAccess.Read);

        // Compatibility entry point; matching policy lives in the session kernel.
        private PlcSoftware? ResolvePlcSoftwareFuzzy(string softwarePath)
            => ResolvePlc(softwarePath, PlcAccess.Read);

        private PlcSoftware? MatchAvailablePlcSoftware(string softwarePath)
        {
            var all = EnumerateSoftwareContainersForExactLookup()
                .Select(c => c.Software).OfType<PlcSoftware>().Distinct().ToList();
            if (all.Count == 0) return null;

            var matched = Guard.MatchPlcName(all.Select(p => p.Name).ToList(), softwarePath);
            if (matched == null) return null;

            return all.FirstOrDefault(p => string.Equals(p.Name, matched, StringComparison.Ordinal))
                ?? all.FirstOrDefault(p => string.Equals(p.Name, matched, StringComparison.OrdinalIgnoreCase));
        }

        // Enumerate every PlcSoftware in the open project (devices + device groups), de-duplicated by
        // name. Used for desktop enumeration and "Available PLC paths" error hints.
        public List<PlcSoftware> GetAllPlcSoftware()
        {
            var result = new List<PlcSoftware>();
            if (_project == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Collect(IEnumerable<DeviceItem>? roots)
            {
                if (roots == null) return;
                var stack = new Stack<DeviceItem>(roots.Where(x => x != null));
                while (stack.Count > 0)
                {
                    var it = stack.Pop();
                    if (it == null) continue;
                    try
                    {
                        if (it.GetService<SoftwareContainer>()?.Software is PlcSoftware plc &&
                            !string.IsNullOrEmpty(plc.Name) && seen.Add(plc.Name))
                        {
                            result.Add(plc);
                        }
                    }
                    catch { /* swallow(probe-optional): An unavailable software service must not prevent discovering PLCs on other device items. */ }
                    try { if (it.DeviceItems != null) foreach (var ch in it.DeviceItems) if (ch != null) stack.Push(ch); } catch { /* swallow(enumerate-optional): An unreadable child collection must not prevent inspecting other device items. */ }
                }
            }

            void WalkDevices(DeviceComposition? devices)
            {
                if (devices == null) return;
                foreach (var d in devices) { try { Collect(d.DeviceItems); } catch { /* swallow(enumerate-optional): An unreadable device must not prevent PLC discovery on other devices. */ } }
            }
            void WalkGroups(DeviceUserGroupComposition? groups)
            {
                if (groups == null) return;
                foreach (var g in groups) { try { WalkDevices(g.Devices); WalkGroups(g.Groups); } catch { /* swallow(enumerate-optional): An unreadable device group must not prevent inspecting sibling groups. */ } }
            }

            try { WalkDevices(_project.Devices); } catch { /* swallow(enumerate-optional): Root device enumeration is best effort; grouped devices are still inspected. */ }
            try { WalkGroups(_project.DeviceGroups); } catch { /* swallow(enumerate-optional): Grouped device enumeration is best effort; already discovered PLCs are retained. */ }
            return result;
        }

        // " Available PLC paths: a, b, c" suffix for not-found error messages (empty when none).
        public string AvailablePlcPathsSuffix()
        {
            if (_plcLookupPathsSuffix != null) return _plcLookupPathsSuffix;
            try
            {
                var names = GetAllPlcSoftware().Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
                var paths = names.Count > 0 ? " Available PLC paths: " + string.Join(", ", names) : string.Empty;
                // 可用路径提示保留设备树遍历错误，区分路径不存在与遍历失败。
                return paths + DeviceScanErrorSuffix();
            }
            catch { /* swallow(probe-optional): Unavailable PLC names leave the recorded device-scan diagnostic as the not-found hint. */ return DeviceScanErrorSuffix(); }
        }

        public CompilerResult CompileSoftware(string softwarePath, string password = "")
        => Organisation(() => new TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter(new TiaMcpServer.Worker.EnginePlcOrganisationSession(this)).CompileSoftware(softwarePath, password));

        /// <summary>
        /// Find the object that actually carries the ICompilable service for a software path,
        /// and return that service.
        ///
        /// PlcSoftware and classic WinCC (HmiTarget) carry it themselves. WinCC Unified's
        /// HmiSoftware does not — for Unified the compilable object sits further up the
        /// ownership chain (the HMI device), which is what the TIA UI compiles too.
        ///
        /// The judgement must be "does this object actually hand out ICompilable", NOT
        /// "is this object an IEngineeringServiceProvider" — in Openness practically
        /// everything implements that interface, while GetService&lt;T&gt;() just returns
        /// **null** when the service is absent instead of throwing. Testing the interface
        /// therefore picks the first ancestor unconditionally and then fails with a null
        /// service. Real-machine 2026-08-31, MTP700 Unified Basic on V21: the software's
        /// immediate parent DeviceItem passed the interface test and yielded a null
        /// service, so Unified compiles failed with
        /// "HmiSoftware via DeviceItemImpl.GetService&lt;ICompilable&gt;() returned null"
        /// — i.e. exactly the panel type this tool was added for.
        ///
        /// So: walk up from the software and take the first level that really provides
        /// the service. Walking is also depth-proof — Unified PC stations nest
        /// Device → DeviceItem → DeviceItem, and that nesting is not ours to predict.
        /// </summary>
        private ICompilable ResolveCompileService(SoftwareContainer? softwareContainer, string softwarePath, out string targetKind)
        {
            try { return new TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter(new TiaMcpServer.Worker.EnginePlcOrganisationSession(this)).ResolveCompileService(softwareContainer, softwarePath, out targetKind); }
            catch (TiaMcp.Adapters.Contracts.PlcSoftwareException error) { throw new PortalException((PortalErrorCode)Enum.Parse(typeof(PortalErrorCode), error.Code), error.Message, error.Candidates, error.InnerException); }
        }

        #endregion
    }
}
