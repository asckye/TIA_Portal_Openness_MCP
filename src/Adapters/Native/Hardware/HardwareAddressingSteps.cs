using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using static TiaMcp.Adapters.Hardware.HardwareAddressPrimitives;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        // Transitional engines supply their existing shared-session hooks. Older
        // PlcWorkers execute the same native algorithm with their own session.
        public Func<IDisposable>? AcquireEngineeringEditAccess { get; set; }
        public Func<bool>? EngineeringProjectMissing { get; set; }
        public Func<string, string, object?>? ResolveEngineeringBlock { get; set; }
        public Func<string, Func<Dictionary<string, object?>, string>, HardwareAddressingReply>? EngineeringStep { get; set; }
        public Action<string>? SharedFamilyMutationIdentity { get; set; }

        public HardwareIpReply ReadHardwareIpAddress(string devicePath)
        {
            Check(); HardwareAddressingPolicy.RequireAvailable(ReleaseKey, "GetDeviceIpAddress");
            if (HardwareProjectMissing()) return new HardwareIpReply { Message = "No project open." };
            // The original service checked the binding, then GetDevice checked it
            // again before entering its path resolver. Retain both checks.
            var device = HardwareProjectMissing() ? null : HardwareLegacyDevice(devicePath);
            if (device == null) return new HardwareIpReply { Device = devicePath, Message = $"Device not found: '{devicePath}'." };
            var nodes = new List<HardwareIpNode>();
            foreach (var root in device.DeviceItems)
                foreach (var n in FindNetworkNodes(root))
                {
                    var subnet = TryGetPropertyValue(n.Node, "ConnectedSubnet");
                    nodes.Add(new HardwareIpNode {
                        NodeName = TryGetName(n.Node) ?? "<unnamed>",
                        Address = ReadNodeAddress(n.Node) ?? string.Empty,
                        NodeType = TryGetPropertyValue(n.Node, "NodeType")?.ToString() ?? string.Empty,
                        ConnectedSubnet = TryGetName(subnet) ?? subnet?.ToString() ?? string.Empty,
                        InterfacePath = n.Path, IsIndustrialEthernet = IsIndustrialEthernetNode(n.Node)
                    });
                }
            string FirstAddress(bool ieOnly) => nodes.Where(n => !ieOnly || n.IsIndustrialEthernet)
                .Select(n => n.Address).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? string.Empty;
            var primary = FirstAddress(true);
            if (primary.Length == 0) primary = FirstAddress(false);
            return new HardwareIpReply { Found = nodes.Count > 0, Device = device.Name ?? devicePath,
                IpAddress = primary, Nodes = nodes.ToArray() };
        }

        private HardwareObject ExactHardware(string[] devicePath, string[] itemPath)
        {
            HardwareAddressingPolicy.ValidatePath(devicePath, true);
            HardwareAddressingPolicy.ValidatePath(itemPath, false);
            Device device;
            if (devicePath.Length == 1)
                device = (Device)(Find(EnumerateAllDevices().ToArray(), devicePath[0]) ?? throw new InvalidOperationException("Exact unique device not found."));
            else
            {
                var group = Find(project!.DeviceGroups, devicePath[0]) as DeviceUserGroup
                    ?? throw new InvalidOperationException("Device group not found: " + devicePath[0]);
                foreach (var name in devicePath.Skip(1).Take(devicePath.Length - 2))
                    group = (DeviceUserGroup)(Find(group.Groups, name) ?? throw new InvalidOperationException("Device group not found: " + name));
                device = (Device)(Find(group.Devices, devicePath.Last()) ?? throw new InvalidOperationException("Device not found."));
            }
            HardwareObject current = device;
            foreach (var name in itemPath)
            {
                var next = Find(current.DeviceItems, name)
                    ?? current.Items.Cast<object?>().FirstOrDefault(i => i is DeviceItem d && string.Equals(d.Name, name, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("Device item not found: " + name + " (children of " + current.Name + ": " + string.Join(", ", current.DeviceItems.Select(d => d.Name).Concat(current.Items.OfType<DeviceItem>().Select(d => d.Name)).Distinct().Take(20)) + ").");
                current = (DeviceItem)next;
            }
            return current;
        }

        public HardwareAddressingReply ReadHardwareAddressing(string[] devicePath, string[] itemPath, int offset = 0, int limit = 100)
        {
            Check(); HardwareAddressingPolicy.RequireAvailable(ReleaseKey, "GetDeviceAddressing");
            return RunHardwareAddressStep("GetDeviceAddressing", meta => {
                HardwareAddressingPolicy.ValidatePage(offset, limit);
                var owner = ExactHardware(devicePath, itemPath);
                meta["ownerPath"] = HardwareOwnerPath(owner); meta["ownerType"] = owner.GetType().Name;
                meta["hwIdentifiers"] = Items(owner.HwIdentifiers).Cast<HwIdentifier>().Select(HwIdentifierRow).ToArray();
                var rows = new List<Dictionary<string, object?>>();
                if (owner is DeviceItem item)
                {
                    rows.AddRange(Items(item.Addresses).Cast<Address>().Select(AddressRow));
                    var addressController = ((IEngineeringServiceProvider)item).GetService<AddressController>();
                    var hwController = ((IEngineeringServiceProvider)item).GetService<HwIdentifierController>();
                    meta["isAddressController"] = addressController != null; meta["isHwIdentifierController"] = hwController != null;
                    if (addressController != null)
                        meta["registeredAddresses"] = Items(addressController.RegisteredAddresses).Cast<Address>()
                            .Select(a => { var r = AddressRow(a); r["ownerPath"] = HardwareOwnerPath(a); return r; }).ToArray();
                    if (hwController != null)
                        meta["registeredHwIdentifiers"] = Items(hwController.RegisteredHwIdentifiers).Cast<HwIdentifier>()
                            .Select(h => { var r = HwIdentifierRow(h); r["ownerPath"] = HardwareOwnerPath(h); return r; }).ToArray();
                }
                var page = rows.Skip(offset).Take(limit).ToArray();
                meta["records"] = page; meta["expectedCount"] = rows.Count; meta["actualCount"] = page.Length;
                meta["offset"] = offset; meta["limit"] = limit;
                meta["nextOffset"] = offset + page.Length < rows.Count ? (int?)(offset + page.Length) : null;
                meta["truncated"] = offset + page.Length < rows.Count;
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Address StartAddress/Length/IoType, controller owner paths and official dynamic attributes; HwIdentifiers; AddressController/HwIdentifierController registrations when the item is a controller.";
                return "Addressing of the hardware object read; no modification.";
            });
        }

        public HardwareAddressingReply UpdateHardwareAddress(HardwareAddressUpdate update, bool dryRun = true)
        {
            Check(); HardwareAddressingPolicy.RequireAvailable(ReleaseKey, "SetDeviceAddress");
            if (update.DryRun != dryRun) throw new ArgumentException("Conflicting hardware preview mode.");
            return RunHardwareAddressStep("SetDeviceAddress", meta => {
                if (!HardwareAddressingPolicy.IoTypes.Contains(update.IoType)) throw new ArgumentException("Invalid ioType.");
                if (update.StartAddress < 0) throw new ArgumentException("startAddress identifies the existing address (>= 0).");
                using var access = dryRun ? null : AcquireEngineeringEditAccess != null ? AcquireEngineeringEditAccess() : Portal().ExclusiveAccess("MCP: verifying a precise HMI operation");
                var item = ExactHardware(update.DevicePath, update.ItemPath) as DeviceItem
                    ?? throw new ArgumentException("itemPathJson must address a DeviceItem (non-empty itemPathJson).");
                var wanted = (AddressIoType)Enum.Parse(typeof(AddressIoType), update.IoType);
                var matches = Items(item.Addresses).Cast<Address>().Where(a => a.IoType == wanted && a.StartAddress == update.StartAddress).Take(2).ToArray();
                if (matches.Length != 1) throw new HardwareAddressingException("NotFound", matches.Length == 0 ? "No address with that IoType/StartAddress on the item." : "Ambiguous address identity.");
                var address = matches[0];
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["ownerPath"] = HardwareOwnerPath(item); meta["before"] = AddressRow(address);
                meta["restrictions"] = "Changing StartAddress may move the opposite IoType of the same module and never rewires tags; packed addresses are unsupported.";
                OB? ob = null;
#if PLC_ADDRESS_PROCESS_IMAGE_SERVICE
                global::Siemens.Engineering.SW.ProcessImageProvider? processImage = null;
#endif
                if (!string.IsNullOrEmpty(update.ProcessImageObName))
                {
                    if (string.IsNullOrWhiteSpace(update.SoftwarePath) || update.SoftwarePath.Length > 256 || update.SoftwarePath != update.SoftwarePath.Trim())
                        throw new ArgumentException("softwarePath must be an exact nonempty name without surrounding whitespace.");
                    ob = (ResolveEngineeringBlock != null ? ResolveEngineeringBlock(update.SoftwarePath, update.ProcessImageObName) : HardwareBlock(update.SoftwarePath, update.ProcessImageObName)) as OB
                        ?? throw new HardwareAddressingException("NotFound", "OB not found in the PLC software: " + update.ProcessImageObName);
#if PLC_ADDRESS_PROCESS_IMAGE_SERVICE
                    processImage = address.GetService<global::Siemens.Engineering.SW.ProcessImageProvider>()
                        ?? throw new NotSupportedException("ProcessImageProvider unavailable on this address (V21 service replacing Address.AssignProcessImageToOrganizationBlock).");
#endif
                    meta["processImageOb"] = ob.Name;
#if PLC_ADDRESS_PROCESS_IMAGE_SERVICE
                    meta["processImageAccess"] = "ProcessImageProvider.AssignProcessImageToOrganizationBlock";
#endif
                }
                ApplyScalarsAndAttributes(address, update.Properties, update.Attributes, meta, !dryRun);
                if (ob != null && !dryRun)
                {
                    meta["mayHaveChanged"] = true;
#if PLC_ADDRESS_PROCESS_IMAGE_SERVICE
                    processImage!.AssignProcessImageToOrganizationBlock(ob);
#else
                    address.AssignProcessImageToOrganizationBlock(ob);
#endif
                    meta["processImageAssigned"] = true;
                }
                if (dryRun) return "Address update preview; nothing changed.";
                meta["after"] = AddressRow(address);
                return "Address properties/attributes written and read back. No save/compile/download.";
            });
        }

        private object? HardwareBlock(string softwarePath, string blockPath)
        {
            var software = ReadSelection(softwarePath).Value;
            var segments = blockPath.Split('/');
            PlcBlockGroup group = software.BlockGroup;
            foreach (var name in segments.Take(segments.Length - 1))
                group = group.Groups.Find(name) ?? throw new InvalidOperationException("Block group not found: " + name);
            return group.Blocks.Find(segments.Last());
        }

        private HardwareAddressingReply RunHardwareAddressStep(string tool, Func<Dictionary<string, object?>, string> action)
        {
            if (EngineeringStep != null) return EngineeringStep(tool, action);
            var reply = new HardwareAddressingReply();
            var meta = reply.Meta;
            meta["timestamp"] = DateTime.Now; meta["tool"] = tool; meta["success"] = false;
            try
            {
                if (HardwareProjectMissing())
                {
                    meta["error"] = "Project is null"; meta["status"] = "InvalidState"; meta["operationSuccess"] = false;
                    reply.Message = "Project is null";
                    return reply;
                }
                reply.Message = action(meta);
                meta["success"] = !meta.TryGetValue("operationSuccess", out var verdict) || !Equals(verdict, false);
                meta["operationSuccess"] = meta["success"];
            }
            catch (Exception ex)
            {
                meta["error"] = ex.ToString(); meta["operationSuccess"] = false; meta["apiCallSuccess"] = false; meta["dataComplete"] = false;
                meta["status"] = ex.GetBaseException() is HardwareAddressingException failure ? failure.Status : "ReadOrWriteFailed";
                if (ex.GetBaseException() is EngineeringException engineering)
                {
                    try
                    {
                        var data = engineering.MessageData;
                        meta["messageData"] = new Dictionary<string, object?> { ["text"] = data.Text, ["detailText"] = data.DetailText };
                        meta["detailMessageData"] = engineering.DetailMessageData.Select(d => new Dictionary<string, object?> { ["text"] = d.Text, ["detailText"] = d.DetailText }).ToArray();
                    }
                    catch { /* swallow(probe-optional): unavailable engineering detail metadata must not replace the original exception */ }
                }
                reply.RequiresSessionReset = TiaMcpServer.ModelContextProtocol.PortalFailureClassifier.IsPortalProcessLost(ex)
                    || reply.MayHaveChanged && !meta.ContainsKey("after");
                reply.Message = tool + " failed";
            }
            return reply;
        }
    }
}
