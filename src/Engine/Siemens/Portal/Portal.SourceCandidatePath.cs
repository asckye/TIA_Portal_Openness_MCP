using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private PlcPathTarget<SoftwareContainer> SourceCandidateSelection(string path)
        {
            var rows = new List<PlcPathTarget<SoftwareContainer>>(); var software = new List<PlcSoftware>(); int count = 0;
            void Items(IEnumerable<DeviceItem> items, string[] parents, int depth)
            {
                if (depth > 64) CandidatePrimitives.Fail("precondition", "plc-tree-depth");
                foreach (var item in items)
                {
                    if (++count > 100000) CandidatePrimitives.Fail("precondition", "complete-plc-inventory");
                    var names = parents.Concat(new[] { item.Name }).ToArray(); var sc = item.GetService<SoftwareContainer>();
                    if (sc?.Software is PlcSoftware plc)
                    {
                        var full = names.Concat(new[] { plc.Name }).ToArray();
                        string address = string.Join("/", full.Select(Uri.EscapeDataString));
                        var query = new[] { address, string.Join("/", full), plc.Name };
                        int existing = software.FindIndex(s => object.Equals(s, plc));
                        if (existing < 0) { software.Add(plc); rows.Add(new PlcPathTarget<SoftwareContainer> { Value = sc, Path = address, QueryNames = query }); }
                        else rows[existing].QueryNames = rows[existing].QueryNames.Concat(query).Distinct(StringComparer.Ordinal).ToArray();
                    }
                    Items(item.DeviceItems, names, depth + 1);
                }
            }
            void Devices(IEnumerable<Device> devices, string[] parents)
            { foreach (var device in devices) Items(device.DeviceItems, parents.Concat(new[] { device.Name }).ToArray(), 0); }
            void Groups(IEnumerable<DeviceUserGroup> groups, string[] parents, int depth)
            {
                if (depth > 64) CandidatePrimitives.Fail("precondition", "plc-tree-depth");
                foreach (var group in groups) { var names = parents.Concat(new[] { group.Name }).ToArray(); Devices(group.Devices, names); Groups(group.Groups, names, depth + 1); }
            }
            if (_project == null) CandidatePrimitives.Fail("project", "project-binding");
            if (!ReferenceEquals(_project, _softwareCacheProject)) { _softwareContainerCache.Clear(); _plcResolutionCache.Clear(); _softwareCacheProject = _project; }
            Devices(_project!.Devices, Array.Empty<string>()); Groups(_project.DeviceGroups, Array.Empty<string>(), 0);
            Devices(_project.UngroupedDevicesGroup.Devices, new[] { _project.UngroupedDevicesGroup.Name });
            var selected = PlcPathSelection.Select(rows, path);
            SoftwareContainerLookup.RememberPath(selected.Value, selected.Path);
            return selected;
        }
        internal string SourceCandidatePath(string path) => SourceCandidateSelection(path).Path;
        private bool SourceCandidateEnabled => TiaMcp.Adapters.Contracts.Candidates.CandidatePolicy.Enabled(typeof(Portal).Assembly, PortalMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), "P6-SOURCE");
    }
}
