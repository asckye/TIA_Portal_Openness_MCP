using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
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
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;
using Microsoft.Extensions.Logging;
using Siemens.Engineering.HW;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens.Services
{
    /// <summary>
    /// Partial: 硬件 I/O 起始地址的读写。
    ///
    /// 为什么单独一份：这是**硬件组态**的写操作，和块级操作的风险模型完全不同 ——
    /// 改错地址不会编译报错，只会让程序读到别的模块的数据。
    ///
    /// API 形态是**反射实测**出来的，不是推理的（2026-09-03，V21）：
    ///   DeviceItem.Addresses     → AddressComposition，集合本身只读
    ///   Address.StartAddress     → Int32，**可写**
    ///   Address.Length           → Int32，**可写**
    ///   Address.IoType           → AddressIoType，**只读**（None/Input/Output/Substitute/Diagnosis）
    /// 集合只读但元素可写，所以改地址是改 Address 对象，不是往集合里塞新的。
    /// </summary>
    internal sealed class AddressesService
    {
        private readonly IEngineeringSession _session;

        public AddressesService(IEngineeringSession session) => _session = session;

        #region io addresses

        /// <summary>一条 I/O 地址的快照。StartAddress / Length 一律是**引擎原值**，不做任何换算。</summary>
        public sealed class IoAddressInfo
        {
            public string IoType { get; set; } = "";
            public int StartAddress { get; set; }
            public int Length { get; set; }

            public override string ToString() => $"{IoType} start={StartAddress} length={Length}";
        }

        /// <summary>
        /// 读一个设备项上的全部 I/O 地址。
        /// 返回 null 表示「没连项目 / 设备项没找到」——和「这个设备项就是没有地址」（空列表）是两回事。
        /// </summary>
        public IReadOnlyList<IoAddressInfo>? GetDeviceItemAddresses(string deviceItemPath)
        {
            _session.Logger?.LogInformation($"Getting IO addresses of device item: {deviceItemPath}");

            if (_session.IsProjectNull())
            {
                return null;
            }

            var item = _session.GetDeviceItemByPath(deviceItemPath);
            if (item == null)
            {
                return null;
            }

            return ReadAddresses(item);
        }

        /// <summary>
        /// 一个设备项本身没有 I/O 地址时，看看它的**子项**有没有。
        ///
        /// 为什么需要：分布式 IO（ET200 系列）和不少机架式站，地址挂在子项而不是
        /// 模块对象本身。只回一句「本设备项没有任何 I/O 地址」会让人以为工具读不到，
        /// 于是去查一个没有问题的组态 —— 用户在 issue #33 里就是这么被卡住的。
        /// 返回 "子项名 → 地址摘要" 的清单，只下钻一层（再深就变成整棵树了，噪音大于信息）。
        /// </summary>
        public List<string> DescribeChildItemsWithAddresses(string deviceItemPath)
        {
            var hints = new List<string>();
            if (_session.IsProjectNull()) return hints;
            var item = _session.GetDeviceItemByPath(deviceItemPath);
            if (item == null) return hints;

            foreach (DeviceItem child in item.DeviceItems)
            {
                if (child == null) continue;
                List<IoAddressInfo> childAddresses;
                try { childAddresses = ReadAddresses(child); }
                catch /* swallow(enumerate-optional): 子项地址不可读时跳过该子项，保留其他子项的地址提示。 */ { continue; }
                if (childAddresses.Count == 0) continue;
                hints.Add(child.Name + " → " + string.Join(", ",
                    childAddresses.Select(a => a.IoType + " " + a.StartAddress + "(len " + a.Length + ")")));
            }
            return hints;
        }

        internal static List<IoAddressInfo> ReadAddresses(DeviceItem item)
        {
            var list = new List<IoAddressInfo>();
            var addresses = item.Addresses;
            if (addresses == null)
            {
                return list;
            }

            foreach (Address a in addresses)
            {
                if (a == null)
                {
                    continue;
                }

                list.Add(new IoAddressInfo
                {
                    IoType = a.IoType.ToString(),
                    StartAddress = a.StartAddress,
                    Length = a.Length
                });
            }

            return list;
        }

        /// <summary>
        /// 改一个设备项的 I/O 起始地址，并**读回验证**。
        /// </summary>
        /// <param name="deviceItemPath">设备项路径，例如 "PLC_1/DI 8x24VDC_1"</param>
        /// <param name="ioType">Input / Output / Diagnosis / Substitute，大小写不敏感</param>
        /// <param name="startAddress">新的起始地址（引擎原值，字节偏移；%I2.0 对应 2）</param>
        /// <returns>
        /// (ok, message, before, after)。ok=false 时 message 说明**具体**是哪一步不成立，
        /// 绝不返回「成功了但其实没改」——写完一定重新读一次再比对。
        /// </returns>
        public (bool ok, string message, IoAddressInfo? before, IoAddressInfo? after) SetDeviceItemStartAddress(
            string deviceItemPath, string ioType, int startAddress)
        {
            _session.Logger?.LogInformation(
                $"Setting IO start address: item={deviceItemPath}, ioType={ioType}, start={startAddress}");

            if (_session.IsProjectNull())
            {
                return (false, "没有连接到 TIA Portal 项目。先调用 Connect / OpenProject "
                             + "（或 AttachToOpenProject 接管已打开的工程）。", null, null);
            }

            if (startAddress < 0)
            {
                return (false, $"起始地址不能为负数（收到 {startAddress}）。", null, null);
            }

            if (!TryParseIoType(ioType, out var wanted))
            {
                return (false, $"无法识别的 ioType '{ioType}'。可用值：Input、Output、Diagnosis、Substitute。",
                        null, null);
            }

            var item = _session.GetDeviceItemByPath(deviceItemPath);
            if (item == null)
            {
                return (false, $"设备项 '{deviceItemPath}' 没找到。用 GetDeviceItemTree 确认路径的每一段。",
                        null, null);
            }

            var addresses = item.Addresses;
            if (addresses == null || !addresses.Any())
            {
                // 「没有地址」和「地址改不了」是两种病，分开说。
                return (false, $"设备项 '{deviceItemPath}' 上没有任何 I/O 地址。"
                             + "常见于它是机架/电源/接口这类本来就不占 I/O 的对象 —— "
                             + "确认路径指向的是**信号模块本身**。", null, null);
            }

            Address? target = null;
            foreach (Address a in addresses)
            {
                if (a != null && a.IoType == wanted)
                {
                    target = a;
                    break;
                }
            }

            if (target == null)
            {
                var have = string.Join(" / ", ReadAddresses(item).Select(x => x.IoType).Distinct());
                return (false, $"设备项 '{deviceItemPath}' 上没有 {wanted} 类型的地址；它实际有的是：{have}。",
                        null, null);
            }

            var before = new IoAddressInfo
            {
                IoType = target.IoType.ToString(),
                StartAddress = target.StartAddress,
                Length = target.Length
            };

            if (before.StartAddress == startAddress)
            {
                // 幂等：本来就是这个值，如实说没改，别报一次假的「已修改」。
                return (true, $"起始地址本来就是 {startAddress}，未做修改。", before, before);
            }

            try
            {
                target.StartAddress = startAddress;
            }
            catch (Exception ex)
            {
                // 地址重叠、模块不允许改地址等都会走到这里。原因必须回给调用方。
                return (false, $"写入起始地址失败：{ex.Message}"
                             + "（常见原因：与其它模块的地址区重叠，或该模块的地址不允许修改）。",
                        before, null);
            }

            // 写完必须重新取一次再比对。设了不报错 ≠ 真的生效。
            var afterList = ReadAddresses(item);
            var after = afterList.FirstOrDefault(x => x.IoType == before.IoType);

            if (after == null)
            {
                return (false, "写入后读回时找不到同类型地址了，状态异常，请在 TIA 界面里确认。", before, null);
            }

            if (after.StartAddress != startAddress)
            {
                return (false, $"写入没有生效：期望 {startAddress}，读回仍是 {after.StartAddress}。"
                             + "该模块的起始地址可能被组态锁定。", before, after);
            }

            return (true, $"起始地址已从 {before.StartAddress} 改为 {after.StartAddress}"
                        + " (native byte offsets). Run CompilePlcSoftware and SaveProject after the change.",
                    before, after);
        }

        private static bool TryParseIoType(string text, out AddressIoType value)
        {
            value = AddressIoType.None;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (AddressIoType candidate in Enum.GetValues(typeof(AddressIoType)))
            {
                if (candidate == AddressIoType.None)
                {
                    continue;
                }

                if (string.Equals(candidate.ToString(), text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate;
                    return true;
                }
            }

            return false;
        }

        #endregion

        // Read a device's configured IP straight from Openness (PROFINET node Address), no S7 probe, no AML export.
        public JsonObject GetDeviceIpAddress(string devicePath)
        {
            if (_session.IsProjectNull()) return new JsonObject { ["found"] = false, ["message"] = "No project open." };
            var device = _session.GetDevice(devicePath);
            if (device == null) return new JsonObject { ["found"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            var nodes = _session.BuildDeviceNodesJson(device);
            var primary = FirstAddress(nodes, ieOnly: true);
            if (primary.Length == 0) primary = FirstAddress(nodes, ieOnly: false);

            return new JsonObject
            {
                ["found"] = nodes.Count > 0,
                ["device"] = device.Name ?? devicePath,
                ["ipAddress"] = primary,
                ["nodeCount"] = nodes.Count,
                ["nodes"] = nodes
            };
        }

        private static string FirstAddress(JsonArray nodes, bool ieOnly)
        {
            foreach (var node in nodes)
            {
                if (node is not JsonObject jo) continue;
                if (ieOnly && !(jo["isIndustrialEthernet"]?.GetValue<bool>() ?? false)) continue;
                var a = jo["address"]?.ToString();
                if (!string.IsNullOrWhiteSpace(a)) return a!;
            }
            return string.Empty;
        }

        // ---- addressing -------------------------------------------------------------------------------------------------
        public ResponseMessage ReadDeviceAddressing(string devicePathJson, string itemPathJson = "[]", int offset = 0, int limit = 100)
            => _session.RunHmiStepTool("ReadDeviceAddressing", meta => {
                HardwareServicesLogic.ValidatePagination(offset, limit);
                var owner = _session.ExactEngineeringHardware(devicePathJson, itemPathJson);
                meta["ownerPath"] = _session.HardwareOwnerPath(owner); meta["ownerType"] = owner.GetType().Name;
                meta["hwIdentifiers"] = new JsonArray(EngineeringGroupOperations.Items(owner.HwIdentifiers).Cast<HwIdentifier>().Select(h => (JsonNode)HwIdentifierRow(h)).ToArray());
                var rows = new List<JsonNode>();
                if (owner is DeviceItem item)
                {
                    rows.AddRange(EngineeringGroupOperations.Items(item.Addresses).Cast<Address>().Select(a => (JsonNode)_session.AddressRow(a)));
                    var addressController = _session.ServiceProvider(item).GetService<AddressController>(); var hwController = _session.ServiceProvider(item).GetService<HwIdentifierController>();
                    meta["isAddressController"] = addressController != null; meta["isHwIdentifierController"] = hwController != null;
                    if (addressController != null)
                        meta["registeredAddresses"] = new JsonArray(EngineeringGroupOperations.Items(addressController.RegisteredAddresses).Cast<Address>()
                            .Select(a => { var r = _session.AddressRow(a); r["ownerPath"] = _session.HardwareOwnerPath(a); return (JsonNode)r; }).ToArray());
                    if (hwController != null)
                        meta["registeredHwIdentifiers"] = new JsonArray(EngineeringGroupOperations.Items(hwController.RegisteredHwIdentifiers).Cast<HwIdentifier>()
                            .Select(h => { var r = HwIdentifierRow(h); r["ownerPath"] = _session.HardwareOwnerPath(h); return (JsonNode)r; }).ToArray());
                }
                Page(rows.ToArray(), offset, limit, meta);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Address StartAddress/Length/IoType, controller owner paths and official dynamic attributes; HwIdentifiers; AddressController/HwIdentifierController registrations when the item is a controller.";
                return "Addressing of the hardware object read; no modification.";
            });

        public ResponseMessage UpdateDeviceAddress(string devicePathJson, string itemPathJson, string ioType, int startAddress, string propertiesJson = "{}", string attributesJson = "{}",
            string softwarePath = "", string processImageObName = "", bool dryRun = true)
            => _session.RunHmiStepTool("UpdateDeviceAddress", meta => {
                HardwareNetworkLogic.RequireOneOf(ioType, HardwareNetworkLogic.AddressIoTypes, "ioType");
                if (startAddress < 0) throw new ArgumentException("startAddress identifies the existing address (>= 0).");
                using var access = dryRun ? null : _session.AcquireHmiEditAccess();
                var item = _session.RequireDeviceItem(_session.ExactEngineeringHardware(devicePathJson, itemPathJson), "itemPathJson");
                var wanted = (AddressIoType)Enum.Parse(typeof(AddressIoType), ioType);
                var matches = EngineeringGroupOperations.Items(item.Addresses).Cast<Address>().Where(a => a.IoType == wanted && a.StartAddress == startAddress).Take(2).ToArray();
                if (matches.Length != 1) throw new PortalException(PortalErrorCode.NotFound, matches.Length == 0 ? "No address with that IoType/StartAddress on the item." : "Ambiguous address identity.");
                var address = matches[0];
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["ownerPath"] = _session.HardwareOwnerPath(item); meta["before"] = _session.AddressRow(address);
                meta["restrictions"] = "Changing StartAddress may move the opposite IoType of the same module and never rewires tags; packed addresses are unsupported.";
                // Resolve (and on V21 refuse) the OB assignment before any property is written, so a refusal never leaves a partial edit.
#if TIA_V20
                OB? ob = null;
                if (!string.IsNullOrEmpty(processImageObName))
                {
                    ob = _session.GetBlock(HardwareNetworkLogic.RequireExactName(softwarePath, "softwarePath"), processImageObName) as OB ?? throw new PortalException(PortalErrorCode.NotFound, "OB not found in the PLC software: " + processImageObName);
                    meta["processImageOb"] = ob.Name;
                }
#else
                // V21 moved the assignment from Address to the ProcessImageProvider service of the address.
                OB? ob = null; global::Siemens.Engineering.SW.ProcessImageProvider? processImage = null;
                if (!string.IsNullOrEmpty(processImageObName))
                {
                    ob = _session.GetBlock(HardwareNetworkLogic.RequireExactName(softwarePath, "softwarePath"), processImageObName) as OB ?? throw new PortalException(PortalErrorCode.NotFound, "OB not found in the PLC software: " + processImageObName);
                    processImage = address.GetService<global::Siemens.Engineering.SW.ProcessImageProvider>() ?? throw new NotSupportedException("ProcessImageProvider unavailable on this address (V21 service replacing Address.AssignProcessImageToOrganizationBlock).");
                    meta["processImageOb"] = ob.Name; meta["processImageAccess"] = "ProcessImageProvider.AssignProcessImageToOrganizationBlock";
                }
#endif
                _session.ApplyScalarsAndAttributes(address, propertiesJson, attributesJson, meta, !dryRun);
#if TIA_V20
                if (ob != null && !dryRun) { meta["mayHaveChanged"] = true; address.AssignProcessImageToOrganizationBlock(ob); meta["processImageAssigned"] = true; }
#else
                if (ob != null && !dryRun) { meta["mayHaveChanged"] = true; processImage!.AssignProcessImageToOrganizationBlock(ob); meta["processImageAssigned"] = true; }
#endif
                if (dryRun) return "Address update preview; nothing changed.";
                meta["after"] = _session.AddressRow(address);
                return "Address properties/attributes written and read back. No save/compile/download.";
            });

        private JsonObject HwIdentifierRow(HwIdentifier id) => new JsonObject
        {
            ["identifier"] = id.Identifier,
            ["controllers"] = new JsonArray(EngineeringGroupOperations.Items(id.HwIdentifierControllers).Select(c => (JsonNode)_session.HardwareOwnerPath(c)).ToArray())
        };
    }
}
