using static TiaMcpServer.Siemens.Services.AddressesService;
using static TiaMcpServer.Siemens.Services.DevicesService;
using Microsoft.Extensions.Logging;
using Siemens.Engineering.HW;
using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    /// <summary>
    /// Partial: 往已存在的设备（CPU / 机架）上**插入子模块** —— 信号板 SB、信号模块 SM、通信模块 CM。
    ///
    /// 为什么单独一份：整机添加走 <c>Devices.CreateWithItem</c>，插子模块走的是完全不同的一套
    /// （<c>HardwareObject.PlugNew</c>），失败模式也不一样 —— 整机失败是「订货号不存在」，
    /// 插子模块失败还多出「槽位被占」和「这台 CPU 不接受这块板」两种，必须分开报。
    ///
    /// API 形态是**反射实测**出来的，不是推理的（2026-09-03，V21，Siemens.Engineering.Base.dll）：
    ///   DeviceItem : HardwareObject                      → 子模块直接插在 DeviceItem（CPU）上
    ///   HardwareObject.PlugNew(string typeIdentifier, string name, int positionNumber) → DeviceItem
    ///   HardwareObject.CanPlugNew(string, string, int)   → bool，**插之前可以预检**（dryRun 靠它）
    ///   HardwareObject.GetPlugLocations()                → IList&lt;PlugLocation&gt;，元素只有
    ///                                                      Label(string) + PositionNumber(int)，都只读
    ///   DeviceItem.PositionNumber / IsPlugged / IsBuiltIn → 只读，用来读回验证
    /// 注意 <c>DeviceItemComposition</c> 上**没有**任何 Create/PlugNew 方法（只有 CreateFrom(MasterCopy)），
    /// 所以插模块只能走宿主 HardwareObject，不能往集合里塞。
    ///
    /// **槽位号不写死**：S7-1200 信号板的槽位号由 <c>GetPlugLocations()</c> 在运行时报出来，
    /// 引擎不猜也不硬编码任何 CPU 的槽位表。
    /// </summary>
    internal sealed class ModulesService
    {
        private readonly IEngineeringSession _session;
        private readonly DevicesService _devices;

        public ModulesService(IEngineeringSession session, DevicesService devices)
        {
            _session = session;
            _devices = devices;
        }

        #region plug submodule

        /// <summary>一个可插槽位（空位）。Label 是 TIA 给的槽位描述，PositionNumber 是 PlugNew 要的那个数。</summary>
        public sealed class PlugLocationInfo
        {
            public int PositionNumber { get; set; }
            public string Label { get; set; } = "";
        }

        /// <summary>一个已经插着东西的槽位。用来把「槽位被占」和「槽位不存在」分开。</summary>
        public sealed class PluggedItemInfo
        {
            public string Name { get; set; } = "";
            public int PositionNumber { get; set; }
            public bool IsPlugged { get; set; }
            public bool IsBuiltIn { get; set; }
            public string TypeIdentifier { get; set; } = "";
        }

        /// <summary>插入子模块的结果。失败时 <see cref="Reason"/> 给出**可判定的失败类别**，不是一句「插入失败」。</summary>
        public sealed class PlugResult
        {
            /// <summary>整体成功（dryRun 时表示「预检通过、可以插」）。</summary>
            public bool Ok { get; set; }

            /// <summary>
            /// 失败类别，取值：NotConnected / DeviceItemNotFound / InvalidParams /
            /// SlotOccupied / SlotNotAvailable / OrderNumberNotFound / NotSupportedByDevice /
            /// PlugFailed / VerifyFailed。成功时为 null。
            /// </summary>
            public string? Reason { get; set; }

            public string Message { get; set; } = "";

            /// <summary>最终被接受的 TypeIdentifier（试出来的那个变体），失败时可能为 null。</summary>
            public string? TypeIdentifier { get; set; }

            /// <summary>最终落位的槽位号。positionNumber 传 -1 时这里是自动选中的那个。</summary>
            public int? PositionNumber { get; set; }

            /// <summary>插入后**读回**的模块信息。dryRun 或失败时为 null。</summary>
            public PluggedItemInfo? Plugged { get; set; }

            /// <summary>插入后读回的 I/O 地址（读到什么就报什么，不换算）。</summary>
            public IReadOnlyList<IoAddressInfo>? Addresses { get; set; }

            /// <summary>预检时该宿主上的空闲槽位，帮调用方直接改参数重试。</summary>
            public IReadOnlyList<PlugLocationInfo>? FreeSlots { get; set; }

            /// <summary>已被占用的槽位。</summary>
            public IReadOnlyList<PluggedItemInfo>? OccupiedSlots { get; set; }

            /// <summary>实际试过的 TypeIdentifier 变体和结论，失败时排障全靠它。</summary>
            public List<string> Attempts { get; } = new List<string>();
        }

        /// <summary>
        /// 读一个宿主（CPU / 机架）上的槽位情况：哪些空着、哪些被占。
        /// 返回 null 表示「没连项目 / 设备项没找到」——和「这台设备一个空槽都没有」（空列表）是两回事。
        /// </summary>
        public (IReadOnlyList<PlugLocationInfo> free, IReadOnlyList<PluggedItemInfo> occupied)? GetDevicePlugLocations(
            string deviceItemPath, bool plugOnDevice = false)
        {
            _session.Logger?.LogInformation($"Getting plug locations of: {deviceItemPath} (plugOnDevice={plugOnDevice})");

            if (_session.IsProjectNull())
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

        // Startdrive drive components (Motor Modules, and below them motors / encoders) are plugged on the Device itself -
        // official "Creating a drive component": sdrDevice.PlugNew(@"OrderNumber:6SL3xxx-xxxxx-xxxx", "MotorModul", 65535). On the
        // TIA V21 project (2026-09-20; docs/reference/real-machine-ledger.md): CanPlugNew on the CU device item and on the rack item answered false for every motor-module identifier;
        // a bare device name resolves to the head device item by default, so the Device host has to be asked for explicitly.
        private HardwareObject? ResolvePlugHost(string path, bool plugOnDevice)
            => plugOnDevice ? (HardwareObject?)_session.GetDeviceByPath(path) : _session.GetDeviceItemByPath(path);

        private List<PlugLocationInfo> ReadFreeSlots(HardwareObject host)
        {
            var list = new List<PlugLocationInfo>();
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

                    list.Add(new PlugLocationInfo
                    {
                        PositionNumber = loc.PositionNumber,
                        Label = loc.Label ?? ""
                    });
                }
            }
            catch (Exception ex)
            {
                // 有的宿主对象（接口、通道之类）根本不支持插拔，这里会抛。空列表就是答案，不该整体失败。
                _session.Logger?.LogWarning(ex, "GetPlugLocations failed; treating as no free slots");
            }

            return list.OrderBy(x => x.PositionNumber).ToList();
        }

        private static List<PluggedItemInfo> ReadOccupiedSlots(HardwareObject host)
        {
            var list = new List<PluggedItemInfo>();
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

        private static PluggedItemInfo DescribeItem(DeviceItem item)
        {
            var info = new PluggedItemInfo { Name = item.Name ?? "" };

            // 这些属性个别对象会抛（未插的代理/特殊类型），逐个兜底，别让一个属性毁掉整条描述。
            try { info.PositionNumber = item.PositionNumber; } catch /* swallow(probe-optional): 不支持的槽位属性保留既有默认值，仍返回其余模块信息。 */ { info.PositionNumber = -1; }
            try { info.IsPlugged = item.IsPlugged; } catch /* swallow(probe-optional): 不支持的槽位属性保留既有默认值，仍返回其余模块信息。 */ { info.IsPlugged = false; }
            try { info.IsBuiltIn = item.IsBuiltIn; } catch /* swallow(probe-optional): 不支持的槽位属性保留既有默认值，仍返回其余模块信息。 */ { info.IsBuiltIn = false; }
            try { info.TypeIdentifier = item.TypeIdentifier ?? ""; } catch /* swallow(probe-optional): 不支持的槽位属性保留既有默认值，仍返回其余模块信息。 */ { info.TypeIdentifier = ""; }

            return info;
        }

        /// <summary>
        /// 往一个宿主设备项上插子模块（信号板 / 信号模块 / 通信模块）。
        /// </summary>
        /// <param name="deviceItemPath">宿主设备项路径。信号板插在 **CPU 本体**上，所以传 CPU 的路径。</param>
        /// <param name="orderNumber">订货号，例如 6ES7221-3BD30-0XB0，带不带空格都行。也可直接传完整 TypeIdentifier（OrderNumber:.../V1.1）。</param>
        /// <param name="version">固件/模块版本，例如 V1.1。留空则让 TIA 自己挑。</param>
        /// <param name="positionNumber">槽位号。传 -1 表示由引擎从空闲槽位里自动挑一个能插的。</param>
        /// <param name="name">新模块名。留空则自动生成且避开同名兄弟。</param>
        /// <param name="dryRun">true 时只用 CanPlugNew 预检，绝不写工程。</param>
        public PlugResult PlugSubmodule(
            string deviceItemPath, string orderNumber, string version, int positionNumber, string? name, bool dryRun, bool plugOnDevice = false)
        {
            _session.Logger?.LogInformation(
                $"Plug submodule: host={deviceItemPath}, order={orderNumber}, version={version}, "
                + $"pos={positionNumber}, dryRun={dryRun}, plugOnDevice={plugOnDevice}");

            var result = new PlugResult();

            if (string.IsNullOrWhiteSpace(orderNumber))
            {
                result.Reason = "InvalidParams";
                result.Message = "orderNumber is empty. Supply the signal board/module order number, for example 6ES7221-3BD30-0XB0.";
                return result;
            }

            if (_session.IsProjectNull())
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

            var typeIdentifiers = BuildPlugTypeIdentifiers(orderNumber, version);
            if (typeIdentifiers.Count == 0)
            {
                result.Reason = "InvalidParams";
                result.Message = $"Cannot construct an available TypeIdentifier from orderNumber='{orderNumber}' version='{version}'.";
                return result;
            }

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
                    catch (Exception ex)
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
                                   + "See attempts for the variants tried.";
                }
                else
                {
                    result.Reason = "NotSupportedByDevice";
                    result.Message = $"The hardware catalog contains '{orderNumber}'{catalogNote}, "
                                   + $"but '{deviceItemPath}' slot(s) {FormatSlots(free)} do not accept it. "
                                   + "Common causes: the board does not support this CPU model/firmware version, or version is incorrect "
                                   + $"(see attempts for the version variants tried).";
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
                created = host.PlugNew(acceptedType, itemName, acceptedSlot);
            }
            catch (Exception ex)
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
            var verifyItem = verifyHost.DeviceItems?.FirstOrDefault(d =>
            {
                try { return d.PositionNumber == acceptedSlot; } catch /* swallow(probe-optional): 无法读取名称或位置时保留既有模块验证回退路径。 */ { return false; }
            });
            if (verifyItem != null)
            {
                result.Addresses = ReadAddresses(verifyItem);
            }

            result.Ok = true;
            var addrText = result.Addresses == null || result.Addresses.Count == 0
                ? "the module currently has no I/O addresses"
                : "current addresses: " + string.Join(" / ", result.Addresses.Select(a => a.ToString()));

            result.Message = $"Inserted '{acceptedType}' into '{deviceItemPath}' slot(s) {acceptedSlot}, "
                           + $"module name '{after.Name}'; readback confirmed IsPlugged=true; {addrText}. "
                           + "To change the start address (for example inputs from %I2.0 -> startAddress=2), "
                           + "Use SetDeviceItemIoAddress; this tool does not change addresses. Run CompilePlcSoftware and SaveProject afterwards.";
            return result;
        }

        private static string FormatSlots(IReadOnlyList<PlugLocationInfo> free)
        {
            return free.Count == 0
                ? "(none)"
                : string.Join(", ", free.Select(x => string.IsNullOrWhiteSpace(x.Label)
                    ? x.PositionNumber.ToString()
                    : $"{x.PositionNumber}({x.Label})"));
        }

        /// <summary>
        /// 拼 TypeIdentifier 变体。订货号空格写法（6ES7221... / 6ES7 221...）TIA 只认其中一种，
        /// 而用户两种都会写，所以复用整机添加那套归一化逻辑挨个试。
        /// </summary>
        private static List<string> BuildPlugTypeIdentifiers(string orderNumber, string version)
        {
            var raw = (orderNumber ?? "").Trim();

            // 用户直接给了完整 TypeIdentifier 就别再拼了，原样用。
            if (raw.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string> { raw };
            }

            var orders = new List<string>
            {
                raw,
                NormalizeOrderNumber(raw),
                TryFormatMlfbWithSpaces(raw),
                TryFormatMlfbWithSpaces(NormalizeOrderNumber(raw))
            }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var v = (version ?? "").Trim();
            var vNoV = v.StartsWith("V", StringComparison.OrdinalIgnoreCase) ? v.Substring(1) : v;

            var versions = new List<string>();
            if (!string.IsNullOrWhiteSpace(v))
            {
                versions.Add(v);
                versions.Add("V" + vNoV);
                versions.Add(vNoV);
                if (v.Contains('.'))
                {
                    versions.Add("V" + vNoV + ".0");
                }
            }

            versions = versions.Where(x => !string.IsNullOrWhiteSpace(x))
                               .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var result = new List<string>();
            foreach (var o in orders)
            {
                foreach (var ver in versions)
                {
                    result.Add($"OrderNumber:{o}/{ver}");
                }

                // 不带版本的形态放最后：让 TIA 自己挑默认版本，是版本写错时的最后一根救命稻草。
                result.Add($"OrderNumber:{o}");
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>新模块名：用户没给就自动生成，并且避开同名兄弟（重名 PlugNew 会直接失败）。</summary>
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

        /// <summary>
        /// 问硬件目录认不认这个订货号。
        /// 返回 (null, 说明) 表示**查不了**（没连 Portal / 目录不可用）—— 这和「查了但没有」必须分开，
        /// 否则会把「目录用不了」误报成「订货号不存在」。
        /// </summary>
        private (bool? known, string note) ProbeCatalogForOrderNumber(string orderNumber)
        {
            try
            {
                var hits = _devices.SearchHardwareCatalog(NormalizeOrderNumber(orderNumber), 5);
                if (hits == null || hits.Count == 0)
                {
                    return (false, "");
                }

                var first = hits.FirstOrDefault();
                var desc = first?.Description ?? first?.TypeName ?? "";
                var ver = string.IsNullOrWhiteSpace(first?.Version) ? "" : $", catalog version {first!.Version}";
                return (true, string.IsNullOrWhiteSpace(desc) ? ver : $"（{desc}{ver}）");
            }
            catch (Exception ex)
            {
                _session.Logger?.LogWarning(ex, "Hardware catalog probe failed while classifying plug failure");
                return (null, "(the hardware catalog is currently unavailable; the existence of the order number cannot be confirmed)");
            }
        }

        #endregion
        internal bool HasProject => _session.CurrentProject is object;
    }
}
