using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private bool HardwareProjectMissing() { Check(); return EngineeringProjectMissing != null ? EngineeringProjectMissing() : project == null; }
        private static IoAddressWriteReply IoWrite(bool ok, string message, IoAddressInfo? before, IoAddressInfo? after)
            => new IoAddressWriteReply { Ok = ok, Message = message, Before = before, After = after,
                RequiresSessionReset = before != null && after == null };
        public IReadOnlyList<IoAddressInfo>? ReadHardwareIoAddresses(string deviceItemPath)
        {
            Check(); HardwareAddressingPolicy.RequireAvailable(ReleaseKey, "GetDeviceItemIoAddresses");

            if (HardwareProjectMissing())
            {
                return null;
            }

            var item = HardwareLegacyItem(deviceItemPath);
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
        public List<string> DescribeHardwareIoChildren(string deviceItemPath)
        {
            Check(); HardwareAddressingPolicy.RequireAvailable(ReleaseKey, "GetDeviceItemIoAddresses");
            var hints = new List<string>();
            if (HardwareProjectMissing()) return hints;
            var item = HardwareLegacyItem(deviceItemPath);
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

        public static List<IoAddressInfo> ReadAddresses(DeviceItem item)
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
        public IoAddressWriteReply SetHardwareIoAddress(
            string deviceItemPath, string ioType, int startAddress, bool dryRun = false)
        {
            Check(); HardwareAddressingPolicy.RequireAvailable(ReleaseKey, "SetDeviceItemIoAddress");
            if (dryRun) throw new ArgumentException("Use ReadHardwareIoAddresses for a read-only preview.");

            if (HardwareProjectMissing())
            {
                return IoWrite(false, "No TIA Portal project is bound. Call ConnectPortal / OpenProject "
                             + "(or AttachOpenProject to bind an already open project).", null, null);
            }

            if (startAddress < 0)
            {
                return IoWrite(false, $"The start address must not be negative (received {startAddress}).", null, null);
            }

            if (!TryParseIoType(ioType, out var wanted))
            {
                return IoWrite(false, $"Unrecognized ioType '{ioType}'. Allowed values: Input, Output, Diagnosis, Substitute.", null, null);
            }

            var item = HardwareLegacyItem(deviceItemPath);
            if (item == null)
            {
                return IoWrite(false, $"Device item '{deviceItemPath}' was not found. Verify each path segment with GetDeviceItemTree.", null, null);
            }

            var addresses = item.Addresses;
            if (addresses == null || !addresses.Any())
            {
                // 「没有地址」和「地址改不了」是两种病，分开说。
                return IoWrite(false, $"Device item '{deviceItemPath}' has no I/O addresses. "
                             + "This is common for racks, power supplies and interfaces that do not occupy I/O: "
                             + "verify that the path points to **the signal module itself**.", null, null);
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
                return IoWrite(false, $"Device item '{deviceItemPath}' has no {wanted} addresses; the available types are: {have}.", null, null);
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
                return IoWrite(true, $"The start address is already {startAddress}; no change was made.", before, before);
            }

            try
            {
                target.StartAddress = startAddress;
            }
            catch (Exception ex)
            {
                // 地址重叠、模块不允许改地址等都会走到这里。原因必须回给调用方。
                return IoWrite(false, $"Writing the start address failed: {ex.Message} "
                             + "(common causes: overlap with another module address range, or the module address cannot be modified).", before, null);
            }

            // 写完必须重新取一次再比对。设了不报错 ≠ 真的生效。
            var afterList = ReadAddresses(item);
            var after = afterList.FirstOrDefault(x => x.IoType == before.IoType);

            if (after == null)
            {
                return IoWrite(false, "Readback after the write could not find an address of the same type. The state is abnormal; verify it in the TIA UI.", before, null);
            }

            if (after.StartAddress != startAddress)
            {
                return IoWrite(false, $"The write did not take effect: expected {startAddress}, readback is still {after.StartAddress}. "
                             + "The module start address may be locked by its configuration.", before, after);
            }

            return IoWrite(true, $"The start address changed from {before.StartAddress} to {after.StartAddress}"
                        + " (native byte offsets). Run CompilePlcSoftware and SaveProject after the change.", before, after);
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

    }
}
