using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Utilities;
using TiaMcp.Adapters.Contracts;
namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public (IReadOnlyList<HardwarePlugLocation> free, IReadOnlyList<HardwarePluggedItem> occupied)? HardwareReadPlugLocationsRaw(
            string deviceItemPath, bool plugOnDevice = false)
        {

            if (HardwareProjectMissing())
            {
                return null;
            }

            var item = ResolvePlugHost(deviceItemPath, plugOnDevice);
            if (item == null)
            {
                return null;
            }

            return (ReadFreeSlots(item), ReadOccupiedSlots(item));
        }

        private HardwareObject? ResolvePlugHost(string path, bool plugOnDevice)
            => plugOnDevice ? (HardwareObject?)HardwareResolveDevice(path) : HardwareResolveItem(path);

        private List<HardwarePlugLocation> ReadFreeSlots(HardwareObject host)
        {
            var list = new List<HardwarePlugLocation>();
            try
            {
                var locations = host.GetPlugLocations();
                if (locations == null)
                {
                    return list;
                }

                foreach (var loc in locations)
                {
                    if (loc == null)
                    {
                        continue;
                    }

                    list.Add(new HardwarePlugLocation
                    {
                        PositionNumber = loc.PositionNumber,
                        Label = loc.Label ?? ""
                    });
                }
            }
            catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
            {
                // 有的宿主对象（接口、通道之类）根本不支持插拔，这里会抛。空列表就是答案，不该整体失败。
            }

            return list.OrderBy(x => x.PositionNumber).ToList();
        }

        private static List<HardwarePluggedItem> ReadOccupiedSlots(HardwareObject host)
        {
            var list = new List<HardwarePluggedItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(DeviceItem? child)
            {
                if (child == null) return;
                var info = DescribeItem(child);
                if (seen.Add(info.Name + "@" + info.PositionNumber)) list.Add(info);
            }
            var children = host.DeviceItems;
            if (children != null) foreach (DeviceItem child in children) Add(child);
            // TIA V21 (2026-09-20; docs/reference/real-machine-ledger.md): on an S7-1500 rail the plugged modules are Device-level DeviceItems; the rail itself lists them only as hardware
            // components (HardwareObject.Items) - without this the readback after PlugNew found "no module in slot 2" (real project).
            try { foreach (var item in host.Items) Add(item as DeviceItem); } catch /* swallow(enumerate-optional): 保留已发现的槽位；无法枚举的硬件集合不提供额外条目。 */ { }

            return list.OrderBy(x => x.PositionNumber).ToList();
        }

        private static DeviceItem? FindDescendantByName(HardwareObject host, string name, int depth)
        {
            if (depth < 0) return null;
            var children = new List<DeviceItem>();
            try { foreach (DeviceItem child in host.DeviceItems) if (child != null) children.Add(child); } catch /* swallow(enumerate-optional): 跳过无法读取的子项或名称，继续在其余可用子项中查找。 */ { }
            try { foreach (var item in host.Items) if (item is DeviceItem child && !children.Contains(child)) children.Add(child); } catch /* swallow(enumerate-optional): 跳过无法读取的子项或名称，继续在其余可用子项中查找。 */ { }
            foreach (var child in children)
            {
                string? childName = null; try { childName = child.Name; } catch /* swallow(enumerate-optional): 跳过无法读取的子项或名称，继续在其余可用子项中查找。 */ { }
                if (string.Equals(childName, name, StringComparison.Ordinal)) return child;
            }
            foreach (var child in children)
            {
                var nested = FindDescendantByName(child, name, depth - 1);
                if (nested != null) return nested;
            }
            return null;
        }

        private static HardwarePluggedItem DescribeItem(DeviceItem item)
        {
            var info = new HardwarePluggedItem { Name = item.Name ?? "" };

            // 这些属性个别对象会抛（未插的代理/特殊类型），逐个兜底，别让一个属性毁掉整条描述。
            try { info.PositionNumber = item.PositionNumber; } catch /* swallow(probe-optional): 不支持的槽位属性保留既有默认值，仍返回其余模块信息。 */ { info.PositionNumber = -1; }
            try { info.IsPlugged = item.IsPlugged; } catch /* swallow(probe-optional): 不支持的槽位属性保留既有默认值，仍返回其余模块信息。 */ { info.IsPlugged = false; }
            try { info.IsBuiltIn = item.IsBuiltIn; } catch /* swallow(probe-optional): 不支持的槽位属性保留既有默认值，仍返回其余模块信息。 */ { info.IsBuiltIn = false; }
            try { info.TypeIdentifier = item.TypeIdentifier ?? ""; } catch /* swallow(probe-optional): 不支持的槽位属性保留既有默认值，仍返回其余模块信息。 */ { info.TypeIdentifier = ""; }

            return info;
        }

        public HardwarePlugResult HardwarePlugModule(
            string deviceItemPath, string orderNumber, string version, int positionNumber, string? name, bool dryRun, bool plugOnDevice = false)
        {

            var result = new HardwarePlugResult();

            if (string.IsNullOrWhiteSpace(orderNumber))
            {
                result.Reason = "InvalidParams";
                result.Message = "orderNumber is empty. Supply the signal board/module order number, for example 6ES7221-3BD30-0XB0.";
                return result;
            }

            if (HardwareProjectMissing())
            {
                result.Reason = "NotConnected";
                result.Message = "No TIA Portal project is bound. Call ConnectPortal, "
                               + "then AttachOpenProject to bind an open project (or OpenProject for a local project).";
                return result;
            }

            var host = ResolvePlugHost(deviceItemPath, plugOnDevice);
            if (host == null)
            {
                result.Reason = "DeviceItemNotFound";
                result.Message = plugOnDevice
                    ? $"Device '{deviceItemPath}' was not found (with plugOnDevice=true, the path is the whole device/station name, for example 'MCP_S120')."
                    : $"Device item '{deviceItemPath}' was not found. Signal boards must be inserted into **the CPU itself**; "
                               + "the path looks like 'PLC_1' or 'PLC_1/PLC_1'. Verify each segment with GetDeviceItemTree.";
                return result;
            }

            var free = ReadFreeSlots(host);
            var occupied = ReadOccupiedSlots(host);
            result.FreeSlots = free;
            result.OccupiedSlots = occupied;

            // ---- 槽位先判定，这样「槽位被占」不会被后面的 CanPlugNew 混成「不支持这块板」 ----
            List<int> slotCandidates;
            if (positionNumber >= 0)
            {
                var taken = occupied.FirstOrDefault(x => x.PositionNumber == positionNumber);
                if (taken != null)
                {
                    result.Reason = "SlotOccupied";
                    result.Message = $"Slot {positionNumber} is already occupied by '{taken.Name}'"
                                   + (taken.IsBuiltIn ? "(this module is integrated into the CPU and cannot be unplugged)" : "")
                                   + $". Free slots: {FormatSlots(free)}.";
                    return result;
                }

                if (free.Count > 0 && free.All(x => x.PositionNumber != positionNumber))
                {
                    result.Reason = "SlotNotAvailable";
                    result.Message = $"Slot {positionNumber} is not a plug location of '{deviceItemPath}'. "
                                   + $"This device currently reports the following free slots: {FormatSlots(free)}. "
                                   + "Slot numbers are reported by the TIA runtime; the engine does not hardcode them. "
                                   + "Inspect GetDevicePlugLocations before inserting the module.";
                    return result;
                }

                slotCandidates = new List<int> { positionNumber };
            }
            else
            {
                if (free.Count == 0)
                {
                    result.Reason = "SlotNotAvailable";
                    result.Message = $"'{deviceItemPath}' has no free slots (TIA reports zero available slots). "
                                   + "Verify that the path points to the CPU itself rather than a rack/interface, or free a slot first.";
                    return result;
                }

                slotCandidates = free.Select(x => x.PositionNumber).ToList();
            }

            var resolved = ResolvePlugTypeIdentifier(orderNumber, version);
            if (resolved.TypeIdentifier == null)
            {
                result.Reason = resolved.Reason;
                result.Message = resolved.Message;
                return result;
            }
            var typeIdentifiers = new List<string> { resolved.TypeIdentifier };

            var itemName = ResolveNewItemName(host, name, slotCandidates[0]);

            // ---- CanPlugNew 预检：这是 dryRun 的依据，也是「不支持」判定的依据 ----
            string? acceptedType = null;
            int acceptedSlot = -1;

            foreach (var slot in slotCandidates)
            {
                foreach (var typeId in typeIdentifiers)
                {
                    bool can;
                    try
                    {
                        can = host.CanPlugNew(typeId, itemName, slot);
                    }
                    catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
                    {
                        // 一抛异常代理就可能死掉，必须重新取宿主句柄再继续试，否则后面全是 disposed 假象。
                        result.Attempts.Add($"slot={slot} {typeId} -> preflight exception: {ex.Message}");
                        var again = ResolvePlugHost(deviceItemPath, plugOnDevice);
                        if (again == null)
                        {
                            result.Reason = "PlugFailed";
                            result.Message = $"The proxy became invalid during preflight and could not be relocated: '{deviceItemPath}': {ex.Message}";
                            return result;
                        }

                        host = again;
                        continue;
                    }

                    result.Attempts.Add($"slot={slot} {typeId} -> CanPlugNew={can}");
                    if (can)
                    {
                        acceptedType = typeId;
                        acceptedSlot = slot;
                        break;
                    }
                }

                if (acceptedType != null)
                {
                    break;
                }
            }

            if (acceptedType == null)
            {
                // 「订货号根本不存在」和「这台 CPU 不接受这块板」是两种病，靠硬件目录分开。
                var (known, catalogNote) = ProbeCatalogForOrderNumber(orderNumber);
                if (known == false)
                {
                    result.Reason = "OrderNumberNotFound";
                    result.Message = $"Order number '{orderNumber}' was not found in the TIA hardware catalog{catalogNote}. "
                                   + "Search with SearchHardwareCatalog first to confirm the order number and version; "
                                   + "modules whose GSD/HSP is not installed also produce this result.";
                }
                else if (known == null)
                {
                    // 目录查不了就别冒充结论：只能说「这台设备不接受」，不能说「订货号不存在」。
                    result.Reason = "NotSupportedByDevice";
                    result.Message = $"'{deviceItemPath}' slot(s) {FormatSlots(free)} do not accept '{orderNumber}'"
                                   + $"{catalogNote}, so an incorrect order number cannot be distinguished from a board unsupported by this CPU. "
                                   + "See attempts for the CanPlugNew results.";
                }
                else
                {
                    result.Reason = "NotSupportedByDevice";
                    result.Message = $"The hardware catalog contains '{orderNumber}'{catalogNote}, "
                                   + $"but '{deviceItemPath}' slot(s) {FormatSlots(free)} do not accept it. "
                                   + "Common causes: the board does not support this CPU model/firmware version, or version is incorrect "
                                   + $"(only '{resolved.TypeIdentifier}' was checked; see attempts).";
                }

                result.TypeIdentifier = null;
                return result;
            }

            result.TypeIdentifier = acceptedType;
            result.PositionNumber = acceptedSlot;

            if (dryRun)
            {
                result.Ok = true;
                result.Message = $"[dryRun] Preflight passed: '{acceptedType}' can be inserted with name '{itemName}' into "
                               + $"'{deviceItemPath}' slot(s) {acceptedSlot}. Use dryRun=false to perform the write. "
                               + "To change the start address after insertion (for example to start inputs at %I2.0), "
                               + "use SetDeviceItemIoAddress; do not pass an address here.";
                return result;
            }

            DeviceItem? created;
            try
            {
                result.MayHaveChanged = true;
                result.RequiresSessionReset = true;
                created = host.PlugNew(acceptedType, itemName, acceptedSlot);
            }
            catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
            {
                result.Reason = "PlugFailed";
                result.Message = $"CanPlugNew accepted the operation, but PlugNew failed during execution: {ex.Message} "
                               + "(the project may be locked/read-only, or another operation may have occupied the slot at the moment of the write).";
                return result;
            }

            if (created == null)
            {
                result.Reason = "PlugFailed";
                result.Message = "PlugNew returned null without throwing; the module was not created.";
                return result;
            }

            // ---- 读回验证：调用没报错 ≠ 模块真的在那个槽位上 ----
            var verifyHost = ResolvePlugHost(deviceItemPath, plugOnDevice);
            if (verifyHost == null)
            {
                result.Reason = "VerifyFailed";
                result.Message = $"Relocating '{deviceItemPath}' after insertion failed; the outcome cannot be confirmed. Verify it in the TIA UI.";
                return result;
            }

            // TIA V21 project (2026-09-20; docs/reference/real-machine-ledger.md; S120 drive components at position 65535 = "any"): TIA assigns the real position on insert (200 for the
            // Motor Module, 1000 for the motor), so the readback matches by the created item's name first and by slot second.
            string? createdName = null; try { createdName = created.Name; } catch /* swallow(probe-optional): 无法读取名称或位置时保留既有模块验证回退路径。 */ { }
            var occupiedAfter = ReadOccupiedSlots(verifyHost);
            var after = (createdName != null ? occupiedAfter.FirstOrDefault(x => string.Equals(x.Name, createdName, StringComparison.Ordinal)) : null)
                        ?? occupiedAfter.FirstOrDefault(x => x.PositionNumber == acceptedSlot);
            // TIA V21 (2026-09-20; docs/reference/real-machine-ledger.md): a Device-level PlugNew of a Startdrive Motor Module creates the rack item (驱动轴_n) AND the module below it - the
            // created object sits one level deeper than the host (real project: "no module in slot 65535" although MCP_MM existed).
            if (after == null && createdName != null)
            {
                var nested = FindDescendantByName(verifyHost, createdName, 3);
                if (nested != null) { after = DescribeItem(nested); result.Attempts.Add("readback: '" + createdName + "' found below the host at position " + after.PositionNumber); }
            }
            if (after != null && acceptedSlot == 65535) { acceptedSlot = after.PositionNumber; result.PositionNumber = acceptedSlot; }
            if (after == null)
            {
                result.Reason = "VerifyFailed";
                result.Message = $"Readback verification failed: slot {acceptedSlot} returned no module. "
                               + "PlugNew returned an object, but the module is not in its configured slot. Verify it in the TIA UI.";
                return result;
            }

            if (!after.IsPlugged)
            {
                result.Plugged = after;
                result.Reason = "VerifyFailed";
                result.Message = $"Readback verification failed: slot {acceptedSlot} contains '{after.Name}' with IsPlugged=false.";
                return result;
            }

            result.Plugged = after;

            // 地址一并读回：issue #26 的另一半就是要改这个，直接把现值摆出来，省一次往返。
            // The matched read-back item carries the addresses: by name (rail modules appear only in HardwareObject.Items),
            // else by slot among DeviceItems and Items, else the returned proxy; its sub-items are included.
            var verifyItem = (createdName != null ? FindDescendantByName(verifyHost, createdName, 3) : null)
                ?? HardwareSlotItems(verifyHost).FirstOrDefault(d =>
                {
                    try { return d.PositionNumber == acceptedSlot; } catch /* swallow(probe-optional): 无法读取名称或位置时保留既有模块验证回退路径。 */ { return false; }
                })
                ?? created;
            result.Addresses = ReadAddressesDeep(verifyItem);

            result.Ok = true;
            result.RequiresSessionReset = false;
            var addrText = result.Addresses == null || result.Addresses.Count == 0
                ? "the module currently has no I/O addresses"
                : "current addresses: " + string.Join(" / ", result.Addresses.Select(a => a.ToString()));

            result.Message = $"Inserted '{acceptedType}' into '{deviceItemPath}' slot(s) {acceptedSlot}, "
                           + $"module name '{after.Name}'; readback confirmed IsPlugged=true; {addrText}. "
                           + "To change the start address (for example inputs from %I2.0 -> startAddress=2), "
                           + "Use SetDeviceItemIoAddress; this tool does not change addresses. Run CompilePlcSoftware and SaveProject afterwards.";
            return result;
        }

        private static string FormatSlots(IReadOnlyList<HardwarePlugLocation> free)
        {
            return free.Count == 0
                ? "(none)"
                : string.Join(", ", free.Select(x => string.IsNullOrWhiteSpace(x.Label)
                    ? x.PositionNumber.ToString()
                    : $"{x.PositionNumber}({x.Label})"));
        }

        // Documented type resolution (functions-on-device-items/creating-and-plugging-a-device-item): a full TypeIdentifier is
        // used verbatim; otherwise ModuleInformationProvider.FindModuleTypes on the partial identifier "OrderNumber:<orderNumber>"
        // returns the complete identifiers of the installed versions and exactly the requested version is selected.
        // No spelling or version variants are generated and only one identifier reaches CanPlugNew/PlugNew.
        private (string? TypeIdentifier, string Reason, string Message) ResolvePlugTypeIdentifier(string orderNumber, string version)
        {
            var raw = (orderNumber ?? "").Trim();
            var requestedVersion = (version ?? "").Trim();
            if (raw.StartsWith("OrderNumber:", StringComparison.Ordinal) || raw.StartsWith("GSD:", StringComparison.Ordinal)) return (raw, "", "");
            var partial = "OrderNumber:" + raw;
            List<string> types;
            try
            {
                var provider = HardwareRequireUtility<ModuleInformationProvider>("ModuleInformationProvider");
                types = (provider.FindModuleTypes(partial) ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return (null, "InvalidParams", $"ModuleInformationProvider.FindModuleTypes('{partial}') failed: {ShortExceptionMessage(ex)}. Pass the full TypeIdentifier as orderNumber.");
            }
            if (types.Count == 0)
                return (null, "OrderNumberNotFound", $"ModuleInformationProvider.FindModuleTypes('{partial}') returned no installed module type. Check the article number (with its spaces) or pass the full TypeIdentifier.");
            var matches = requestedVersion.Length == 0 ? types : types.Where(t => t.EndsWith("/" + requestedVersion, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1) return (matches[0], "", "");
            var listed = string.Join("; ", types.Take(12)) + (types.Count > 12 ? "; ..." : "");
            return (null, "InvalidParams", (matches.Count == 0
                ? $"No installed type of '{raw}' has version '{requestedVersion}'."
                : $"'{raw}' has several installed versions; pass the exact version.") + $" Installed types: {listed}.");
        }

        private static IEnumerable<DeviceItem> HardwareSlotItems(HardwareObject host)
        {
            var items = new List<DeviceItem>();
            try { foreach (DeviceItem child in host.DeviceItems) if (child != null) items.Add(child); } catch /* swallow(enumerate-optional): an unreadable child list still lets the hardware component list be checked. */ { }
            try { foreach (var item in host.Items) if (item is DeviceItem child && !items.Contains(child)) items.Add(child); } catch /* swallow(enumerate-optional): an unreadable hardware component list keeps the children already found. */ { }
            return items;
        }

        private static List<IoAddressInfo> ReadAddressesDeep(DeviceItem item)
        {
            var rows = new List<IoAddressInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Walk(DeviceItem current, int depth)
            {
                foreach (var row in TiaMcp.Adapters.PlcFoundationEngine.ReadAddresses(current))
                    if (seen.Add(row.IoType + "|" + row.StartAddress + "|" + row.Length)) rows.Add(row);
                if (depth >= 3) return;
                foreach (DeviceItem child in current.DeviceItems) if (child != null) Walk(child, depth + 1);
            }
            Walk(item, 0);
            return rows;
        }

        private static string ResolveNewItemName(HardwareObject host, string? requested, int slot)
        {
            var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (DeviceItem child in host.DeviceItems)
                {
                    if (child?.Name != null)
                    {
                        siblings.Add(child.Name);
                    }
                }
            }
            catch /* swallow(enumerate-optional): 读不到兄弟项时由 TIA 原生插入操作检查重名。 */
            {
                // 读不到兄弟就不做去重，交给 TIA 自己报重名。
            }

            var baseName = string.IsNullOrWhiteSpace(requested) ? $"Module_{slot}" : requested!.Trim();
            if (!siblings.Contains(baseName))
            {
                return baseName;
            }

            for (int i = 2; i < 100; i++)
            {
                var candidate = $"{baseName}_{i}";
                if (!siblings.Contains(candidate))
                {
                    return candidate;
                }
            }

            return baseName;
        }

        private (bool? known, string note) ProbeCatalogForOrderNumber(string orderNumber)
        {
            try
            {
                var hits = HardwareLegacySearchHardwareCatalog(NormalizeOrderNumber(orderNumber), 5);
                if (hits == null || hits.Count == 0)
                {
                    return (false, "");
                }

                var first = hits.FirstOrDefault();
                var desc = first?.Description ?? first?.TypeName ?? "";
                var ver = string.IsNullOrWhiteSpace(first?.Version) ? "" : $", catalog version {first!.Version}";
                return (true, string.IsNullOrWhiteSpace(desc) ? ver : $"（{desc}{ver}）");
            }
            catch (Exception ex) /* swallow(probe-optional): Preserve the original optional catalog/module observation; the caller retains its existing fallback and readback. */
            {
                return (null, "(the hardware catalog is currently unavailable; the existence of the order number cannot be confirmed)");
            }
        }
        public Dictionary<string, object?>? HardwareReadPlugLocations(string deviceItemPath, bool plugOnDevice = false)
        {
            var result = HardwareReadPlugLocationsRaw(deviceItemPath, plugOnDevice);
            return result == null ? null : new Dictionary<string, object?> { ["free"] = result.Value.free, ["occupied"] = result.Value.occupied };
        }

    }
}
