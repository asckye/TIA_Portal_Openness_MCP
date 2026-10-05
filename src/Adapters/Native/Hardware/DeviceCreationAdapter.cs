#if PLC_HARDWARE_CATALOG
using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using TiaMcp.Logic.V4;
using Primitives = TiaMcp.Adapters.Hardware.HardwarePrimitives;

namespace TiaMcp.Adapters.Hardware
{
    // The host owns identity checks and threading. No thread switch or lifecycle call occurs here.
    public sealed class DeviceCreationAdapter : IDeviceCreationAdapter
    {
        private readonly ProjectBase project;
        private readonly TiaPortal portal;
        private readonly List<KeyValuePair<object, string>> identities = new List<KeyValuePair<object, string>>();
        private DeviceComposition? rootDevices;
        public Func<PlanIdentity> Identity { private get; set; }
        public string RootId => Id(project);
        public bool IsProject(ProjectBase current) => object.Equals(project, current);

        public DeviceCreationAdapter(ProjectBase project, TiaPortal portal, Func<PlanIdentity> identity)
        { this.project = project; this.portal = portal; Identity = identity; }

        private string Id(object value)
        {
            foreach (var pair in identities) if (object.Equals(pair.Key, value)) return pair.Value;
            if (identities.Count >= 16384) throw new InvalidOperationException("Device identity budget exhausted.");
            string id = "device-object-" + identities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            identities.Add(new KeyValuePair<object, string>(value, id));
            return id;
        }

        public PlanIdentity ReadIdentity() => Identity();

        public IReadOnlyList<DeviceCatalogEntry> ReadCatalog(string typeIdentifier)
        {
            var rows = new List<DeviceCatalogEntry>();
            foreach (var entry in portal.HardwareCatalog.Find(typeIdentifier))
            {
                if (rows.Count >= 1000) throw new InvalidOperationException("Catalog observation budget exceeded.");
                rows.Add(new DeviceCatalogEntry { TypeIdentifier = Primitives.TypeIdentifier(entry), ArticleNumber = Primitives.ArticleNumber(entry),
                    Version = Primitives.Version(entry), TypeName = Primitives.TypeName(entry), Description = Primitives.Description(entry), CatalogPath = Primitives.CatalogPath(entry) });
            }
            return rows;
        }

        public IReadOnlyList<DeviceInventoryItem> ReadInventory()
        {
            var rows = new List<DeviceInventoryItem>();
            var groups = new List<object>();
            void Add(object item, object parent, string name, bool group, object actualParent)
            {
                if (rows.Count >= 4096 || !object.Equals(parent, actualParent)) throw new InvalidOperationException("Device inventory parent or budget is invalid.");
                rows.Add(new DeviceInventoryItem { Id = Id(item), ParentId = Id(parent), Name = name, IsGroup = group });
            }
            void Devices(DeviceComposition composition, object parent)
            { foreach (var device in composition) Add(device, parent, device.Name, false, device.Parent); }
            void Group(DeviceUserGroup group, object parent, int depth)
            {
                if (depth > 32 || groups.Any(g => object.Equals(g, group))) throw new InvalidOperationException("Device group graph is cyclic or oversized.");
                groups.Add(group);
                Add(group, parent, group.Name, true, group.Parent);
                Devices(group.Devices, group);
                foreach (var child in group.Groups) Group(child, group, depth + 1);
            }
            rootDevices = Primitives.Devices(project);
            Devices(rootDevices, project);
            var ungrouped = project.UngroupedDevicesGroup ?? throw new InvalidOperationException("Ungrouped device composition unavailable.");
            Add(ungrouped, project, "$ungrouped", true, ungrouped.Parent);
            Devices(ungrouped.Devices, ungrouped);
            foreach (var group in project.DeviceGroups) Group(group, project, 0);
            return rows;
        }

        public void BeforeCreate()
        { if (rootDevices == null) throw new InvalidOperationException("A complete inventory is required before creation."); }

        public DeviceInventoryItem Create(string typeIdentifier, string deviceName)
        {
            var created = Primitives.CreateWithItem(rootDevices!, typeIdentifier, deviceName, deviceName);
            if (created == null || !object.Equals(created.Parent, project)) throw new InvalidOperationException("Create returned no verifiable root device.");
            return new DeviceInventoryItem { Id = Id(created), Name = created.Name, ParentId = RootId };
        }
    }
}
#endif
