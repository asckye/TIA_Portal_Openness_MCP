using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    internal interface IHardwareAddressingService
    {
        bool HasProject { get; }
        IReadOnlyList<TiaMcp.Adapters.Contracts.IoAddressInfo>? GetDeviceItemAddresses(string path);
        List<string> DescribeChildItemsWithAddresses(string path);
        (bool ok, string message, TiaMcp.Adapters.Contracts.IoAddressInfo? before, TiaMcp.Adapters.Contracts.IoAddressInfo? after) SetDeviceItemStartAddress(string path, string ioType, int startAddress);
        ResponseMessage ReadDeviceAddressing(string devicePathJson, string itemPathJson, int offset, int limit);
        ResponseMessage UpdateDeviceAddress(string devicePathJson, string itemPathJson, string ioType, int startAddress, string propertiesJson, string attributesJson, string softwarePath, string processImageObName, bool dryRun);
        JsonObject GetDeviceIpAddress(string path);
    }

    [McpServerToolType]
    internal sealed class AddressesTools
    {
        private readonly IHardwareAddressingService _service;

        public AddressesTools(IHardwareAddressingService service) => _service = service;
        #region io addresses

        [McpServerTool(Name = "GetDeviceItemIoAddresses"), Description(
            "[L2][Hardware] READ-ONLY. List the I/O addresses of one device item (signal module, signal board, "
            + "built-in CPU I/O). Returns each address as ioType + startAddress + length in ENGINE RAW VALUES "
            + "(startAddress is the byte offset: %I2.0 is startAddress 2). Use this to confirm an address before "
            + "and after changing it. Device item path looks like 'PLC_1/DI 8x24VDC_1' — get it from GetDeviceItemTree.")]
        public CallToolResult GetDeviceItemIoAddressesV4(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath)
            => HardwareContract.Run("GetDeviceItemIoAddresses", () =>
            {
                HardwareContract.RequireProject(_service.HasProject);
                return GetDeviceItemIoAddresses(deviceItemPath);
            }, write: false, current: false);

        public ResponseMessage GetDeviceItemIoAddresses(
            [Description("deviceItemPath: path in the project structure to the device item")] string deviceItemPath)
        {
            try
            {
                var addresses = _service.GetDeviceItemAddresses(deviceItemPath);

                if (addresses == null)
                {
                    // 「没连上」和「设备项不存在」都会返回 null，但对调用方是两件事，分开问一句更有用。
                    throw new McpException(
                        $"Cannot read addresses for '{deviceItemPath}': connect and bind the project with ConnectPortal / AttachOpenProject, "
                        + "then verify every device-item path segment with GetDeviceItemTree.",
                        McpErrorCode.InvalidParams);
                }

                var arr = new JsonArray();
                foreach (var a in addresses)
                {
                    arr.Add(new JsonObject
                    {
                        ["ioType"] = a.IoType,
                        ["startAddress"] = a.StartAddress,
                        ["length"] = a.Length
                    });
                }

                // addresses==null（没连上/路径不存在）上面已经抛掉了；走到这里拿到的是真清单，
                // 空清单就是"这个设备项确实不占 I/O"。
                // 空清单时别只说「没有」。地址在分布式 IO（ET200 系列）和机架式站上
                // 常常挂在**子项**而不是模块对象本身，只回一句「没有任何 I/O 地址」
                // 会让人以为是工具读不到，转头去查一个没问题的组态。
                var childHints = addresses.Count == 0
                    ? _service.DescribeChildItemsWithAddresses(deviceItemPath)
                    : new System.Collections.Generic.List<string>();

                var msg = addresses.Count > 0
                    ? $"Device item '{deviceItemPath}' has {addresses.Count} I/O address(es)."
                    : childHints.Count > 0
                        ? $"Device item '{deviceItemPath}' has no I/O addresses **at this level**, but its children do: "
                          + $"Addresses belong to child items (common for distributed I/O). Use these paths: {string.Join("; ", childHints)}"
                        : $"Device item '{deviceItemPath}' has no I/O addresses, nor do its immediate children. "
                          + "This is common for racks, power supplies and interfaces that do not occupy I/O; "
                          + "if addresses are expected (for example on a distributed I/O module), inspect the hierarchy with GetDeviceItemTree: "
                          + "the addresses may belong to a deeper level.";

                return new ResponseMessage
                {
                    Message = msg,
                    // envelope: legacy-stamp-without-verdict
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["deviceItemPath"] = deviceItemPath,
                        ["addressCount"] = addresses.Count,
                        ["addresses"] = arr,
                        ["childItemsWithAddresses"] = new JsonArray(
                            childHints.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
                        ["note"] = "startAddress/length are raw engine values without conversion. startAddress is a byte offset."
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error reading IO addresses of '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetDeviceItemIoAddress"), Description(
            "[L2][Hardware][WRITE] Preview or change the I/O START ADDRESS of one device item "
            + "(e.g. move a DI module to start at %I2.0 by passing startAddress=2). Defaults to dryRun=true. "
            + "startAddress is the ENGINE RAW byte offset, not '2.0'. It writes hardware configuration, so a wrong "
            + "value does NOT fail compilation — the program silently reads a different module. Read back with "
            + "GetDeviceItemIoAddresses, then CompilePlcSoftware and SaveProject. Overlapping address ranges are "
            + "rejected by TIA and reported back with the reason.")]
        public CallToolResult SetDeviceItemIoAddressV4(
            [Description("deviceItemPath: path in the project structure to the device item, e.g. 'PLC_1/DI 8x24VDC_1'")] string deviceItemPath,
            [Description("ioType: Input, Output, Diagnosis or Substitute")] string ioType,
            [Description("startAddress: new start address as engine raw byte offset (%I2.0 -> 2)")] int startAddress,
            [Description("dryRun: true (default) only previews the change; set false to actually write it")] bool dryRun = true)
            => HardwareContract.Run("SetDeviceItemIoAddress", () =>
            {
                HardwareContract.Address(ioType, startAddress, false);
                HardwareContract.RequireProject(_service.HasProject);
                return SetDeviceItemIoAddress(deviceItemPath, ioType, startAddress, dryRun);
            }, write: !dryRun, current: false);

        public ResponseMessage SetDeviceItemIoAddress(
            [Description("deviceItemPath: path in the project structure to the device item, e.g. 'PLC_1/DI 8x24VDC_1'")] string deviceItemPath,
            [Description("ioType: Input, Output, Diagnosis or Substitute")] string ioType,
            [Description("startAddress: new start address as engine raw byte offset (%I2.0 -> 2)")] int startAddress,
            [Description("dryRun: true (default) only previews the change; set false to actually write it")] bool dryRun = true)
        {
            try
            {
                // 参数自身的合法性在分支之前判。放进 dryRun=false 那一路的后果是：
                // 预演对负数一律报「可以改」，等到真写时才失败 —— 预演就成了误导。
                if (startAddress < 0)
                {
                    throw new McpException(
                        $"startAddress must not be negative (received {startAddress}). It is the raw engine byte offset; "
                        + "%I2.0 corresponds to startAddress=2; do not write \"2.0\".",
                        McpErrorCode.InvalidParams);
                }

                if (dryRun)
                {
                    // 预览也必须走真实定位，否则「预览通过、实写失败」毫无意义。
                    var current = _service.GetDeviceItemAddresses(deviceItemPath);
                    if (current == null)
                    {
                        throw new McpException(
                            $"Cannot read addresses for '{deviceItemPath}': connect and bind the project with ConnectPortal / AttachOpenProject, "
                            + "then verify every device-item path segment with GetDeviceItemTree.",
                            McpErrorCode.InvalidParams);
                    }

                    var match = current.FirstOrDefault(
                        x => string.Equals(x.IoType, ioType, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                    {
                        // 预演定位失败时保留异常，由 V4 边界报告读取失败。
                        var have = current.Count == 0
                            ? "(none)"
                            : string.Join(" / ", current.Select(x => x.IoType).Distinct());
                        throw new McpException(
                            $"[dryRun] Cannot change: '{deviceItemPath}' has no {ioType} addresses; the available types are {have}.",
                            McpErrorCode.InvalidParams);
                    }

                    var preview = match.StartAddress == startAddress
                        ? $"[dryRun] No change needed: {ioType} start address is already {startAddress}."
                        : $"[dryRun] Will change '{deviceItemPath}'s {ioType} start address from "
                          + $"{match.StartAddress} to {startAddress} (length={match.Length} unchanged)."
                          + "After verifying the preview, use dryRun=false to perform the write. "
                          + "Warning: TIA checks for address overlap with other modules only when the write is actually performed.";

                    return new ResponseMessage
                    {
                        Message = preview,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            // dryRun 走到这里说明本次预演该验的都验到了（地址重不重叠只有真写时
                            // TIA 才判，这一点 Message 里已写明）。改不了的情形上面已经抛掉。
                            ["dryRun"] = true,
                            ["feasible"] = true,
                            ["deviceItemPath"] = deviceItemPath,
                            ["ioType"] = ioType,
                            ["requestedStartAddress"] = startAddress,
                            ["currentStartAddress"] = match.StartAddress,
                            ["currentLength"] = match.Length
                        }
                    };
                }

                var (ok, message, before, after) =
                    _service.SetDeviceItemStartAddress(deviceItemPath, ioType, startAddress);

                var meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["dryRun"] = false,
                    ["success"] = ok,
                    ["mayHaveChanged"] = before != null,
                    ["writeOutcomeUnknown"] = before != null && after == null,
                    ["postStateKnown"] = after != null,
                    ["deviceItemPath"] = deviceItemPath,
                    ["ioType"] = ioType,
                    ["requestedStartAddress"] = startAddress,
                    ["beforeStartAddress"] = before?.StartAddress,
                    ["afterStartAddress"] = after?.StartAddress,
                    ["length"] = after?.Length ?? before?.Length,
                    // after 是写后读回的那一份，它才是"地址真的改了"的证据。
                    // ok=true 却读不回 after 时，改没改成答不上来 —— 这既不是成功也不是失败，
                    // verified 和读回证据交给 V4 边界区分失败与 unknown。
                    ["verified"] = ok && after != null
                };

                if (after != null)
                {
                    meta["nextActions"] = new JsonArray
                    {
                        "CompilePlcSoftware to validate the hardware changes",
                        "SaveProject: save only after compilation reports zero errors",
                        "GetDeviceItemIoAddresses: perform an independent readback for final confirmation"
                    };
                }

                return new ResponseMessage
                {
                    Message = after != null
                        ? message
                        : $"⚠ UNVERIFIED: {message}: the write call reported no error, but the updated address could not be read back; "
                          + "the address change **cannot be confirmed**. Verify it with GetDeviceItemIoAddresses or the TIA UI; "
                          + "do not treat the operation as completed before verification.",
                    Meta = meta
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error setting IO address of '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        #endregion


        [McpServerTool(Name="GetDeviceAddressing"), Description("[L2][Hardware][READ] Addressing of the exact device or device item: HwIdentifiers (Identifier, controller owner paths), DeviceItem.Addresses (StartAddress, Length, IoType, AddressControllers, dynamic Context/ProcessImage/IsochronousMode/InterruptObNumber) and, when the item is a controller, AddressController.RegisteredAddresses / HwIdentifierController.RegisteredHwIdentifiers with owner paths. Paginated, no modification.")]
        public CallToolResult GetDeviceAddressingV4(string[] devicePath, string[]? itemPath = null, int offset=0, int limit=100)
            => HardwareContract.Run("GetDeviceAddressing", () =>
            {
                var devicePathJson = HardwareContract.Input(PathValidator.Device(), devicePath, "devicePath");
                var itemPathJson = HardwareContract.Input(PathValidator.Item(), itemPath ?? System.Array.Empty<string>(), "itemPath");
                HardwareServicesLogic.ValidatePagination(offset, limit);
                HardwareContract.RequireProject(_service.HasProject);
                return ReadDeviceAddressing(devicePathJson, itemPathJson, offset, limit);
            }, write: false, current: false);

        public ResponseMessage ReadDeviceAddressing(string devicePathJson, string itemPathJson="[]", int offset=0, int limit=100)
            => _service.ReadDeviceAddressing(devicePathJson,itemPathJson,offset,limit);

        [McpServerTool(Name="SetDeviceAddress"), Description("[L2][Hardware][WRITE] Edit one exact Address of a device item, identified by ioType (Input/Output/Diagnosis/Substitute) and its current startAddress: properties StartAddress/Length and attributes ProcessImage/IsochronousMode/InterruptObNumber, each read back. processImageObName (with softwarePath) assigns the process image partition to that OB: Address.AssignProcessImageToOrganizationBlock on V20, the address's ProcessImageProvider service on V21. Changing StartAddress may move the opposite IoType of the module and never rewires tags. Default dryRun=true; no save/compile/download.")]
        public CallToolResult SetDeviceAddressV4(
            string[] devicePath,
            string[] itemPath,
            [Description("ioType: None | Input | Output | Substitute | Diagnosis.")] string ioType,
            [Description("startAddress: new start address (byte).")] int startAddress,
            AttributeMap<Scalar>? properties = null,
            AttributeMap<Scalar>? attributes = null,
            string softwarePath="",
            [Description("processImageObName: exact name of the OB the process image partition is assigned to ('' = automatic).")] string processImageObName="",
            bool dryRun=true)
            => HardwareContract.Run("SetDeviceAddress", () =>
            {
                var devicePathJson = HardwareContract.Input(PathValidator.Device(), devicePath, "devicePath");
                var itemPathJson = HardwareContract.Input(PathValidator.Item(), itemPath, "itemPath");
                var propertiesJson = HardwareContract.Input(HardwareContract.AddressProperties(), properties ?? HardwareContract.EmptyAttributes(), "properties");
                var attributesJson = HardwareContract.Input(HardwareContract.Attributes(attributes), attributes ?? HardwareContract.EmptyAttributes(), "attributes");
                HardwareContract.Address(ioType, startAddress, true);
                HardwareContract.RequireProject(_service.HasProject);
                return UpdateDeviceAddress(devicePathJson, itemPathJson, ioType, startAddress, propertiesJson, attributesJson, softwarePath, processImageObName, dryRun);
            }, write: !dryRun, current: false);

        public ResponseMessage UpdateDeviceAddress(
            string devicePathJson,
            string itemPathJson,
            [Description("ioType: None | Input | Output | Substitute | Diagnosis.")] string ioType,
            [Description("startAddress: new start address (byte).")] int startAddress,
            string propertiesJson="{}",
            string attributesJson="{}",
            string softwarePath="",
            [Description("processImageObName: exact name of the OB the process image partition is assigned to ('' = automatic).")] string processImageObName="",
            bool dryRun=true)
            => _service.UpdateDeviceAddress(devicePathJson,itemPathJson,ioType,startAddress,propertiesJson,attributesJson,softwarePath,processImageObName,dryRun);

        [McpServerTool(Name = "GetDeviceIpAddress"), Description(
            "[L1][Category:Hardware][PreCondition:ConnectPortal+OpenProject]" +
            " Read a device's configured IP address straight from the TIA project (Openness PROFINET node) —" +
            " NOT by probing the CPU over S7 and NOT by exporting/parsing AML. Returns the primary IE IP plus all network nodes" +
            " (address, subnet, type). This is the correct, fast way to discover a PLC's IP before ConnectOnlinePlc/GetPlcLiveValuesS7.")]
        public CallToolResult GetDeviceIpAddressV4(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath)
            => HardwareContract.Run("GetDeviceIpAddress", () =>
            {
                HardwareContract.RequireProject(_service.HasProject);
                return GetDeviceIpAddress(devicePath);
            }, write: false, current: false);

        public ResponseJsonReport GetDeviceIpAddress(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath)
        {
            try
            {
                var data = _service.GetDeviceIpAddress(devicePath);
                bool found = data["found"]?.GetValue<bool>() ?? false;
                var ip = data["ipAddress"]?.ToString() ?? string.Empty;
                return new ResponseJsonReport
                {
                    Ok = found,
                    Message = found
                        ? $"{devicePath} IP: {(string.IsNullOrEmpty(ip) ? "(no address configured on any node)" : ip)}"
                        : (data["message"]?.ToString() ?? "Device not found."),
                    Data = data,
                    Meta = ResponseMeta.Basic(DateTime.Now, found)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetDeviceIpAddress failed for '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
