using TiaMcp.Logic.V4.Hmi;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class ModulesTools
    {
        private readonly ModulesService _service;

        public ModulesTools(ModulesService service) => _service = service;
        #region plug submodule

        [McpServerTool(Name = "GetDevicePlugLocations"), Description(
            "[L2][Hardware] READ-ONLY. List the plug slots of one device item: which slot numbers are FREE "
            + "(as reported by TIA at runtime) and which are already OCCUPIED (by name / order number / built-in). "
            + "Call this BEFORE plugging a signal board (SB), signal module (SM) or communication module (CM) so the "
            + "slot / position number comes from TIA instead of a guess — slot numbers differ per CPU family and are "
            + "never hardcoded by this server. A signal board plugs into the CPU device item itself, so pass the CPU "
            + "path (e.g. 'PLC_1'). Get paths from GetDeviceItemTree. plugOnDevice=true asks the Device (station) itself instead "
            + "of a device item - that is where Startdrive drive components (Motor Modules; motors / encoders below them) plug "
            + "(official 'Creating a drive component': Device.PlugNew(\"OrderNumber:6SL3xxx-xxxxx-xxxx\", name, 65535)).")]
        public CallToolResult GetDevicePlugLocationsV4(
            [Description("deviceItemPath: path to the host device item, e.g. 'PLC_1' for a CPU; with plugOnDevice=true the device (station) name")] string deviceItemPath,
            [Description("plugOnDevice: true = the path names a Device (station) and its own plug locations are read (Startdrive drive components)")] bool plugOnDevice = false)
            => HardwareContract.Run("GetDevicePlugLocations", () =>
            {
                HardwareContract.RequireProject(_service.HasProject);
                return GetDevicePlugLocations(deviceItemPath, plugOnDevice);
            }, write: false, current: false);

        public ResponseMessage GetDevicePlugLocations(
            [Description("deviceItemPath: path to the host device item, e.g. 'PLC_1' for a CPU; with plugOnDevice=true the device (station) name")] string deviceItemPath,
            [Description("plugOnDevice: true = the path names a Device (station) and its own plug locations are read (Startdrive drive components)")] bool plugOnDevice = false)
        {
            try
            {
                var slots = _service.GetDevicePlugLocations(deviceItemPath, plugOnDevice);

                if (slots == null)
                {
                    // 「没连上」和「路径不存在」都会是 null，对调用方是两件事，一次说清楚。
                    throw new McpException(
                        $"Cannot read slots for '{deviceItemPath}': connect and bind the project with ConnectPortal / AttachOpenProject, "
                        + "then verify every device-item path segment with GetDeviceItemTree.",
                        McpErrorCode.InvalidParams);
                }

                var (free, occupied) = slots.Value;

                var freeArr = new JsonArray();
                foreach (var f in free)
                {
                    freeArr.Add(new JsonObject
                    {
                        ["positionNumber"] = f.PositionNumber,
                        ["label"] = f.Label
                    });
                }

                var occArr = new JsonArray();
                foreach (var o in occupied)
                {
                    occArr.Add(new JsonObject
                    {
                        ["positionNumber"] = o.PositionNumber,
                        ["name"] = o.Name,
                        ["isPlugged"] = o.IsPlugged,
                        ["isBuiltIn"] = o.IsBuiltIn,
                        ["typeIdentifier"] = o.TypeIdentifier
                    });
                }

                // 槽位是 TIA 运行时报出来的实际占用情况，读到什么报什么。
                var msg = free.Count == 0
                    ? $"'{deviceItemPath}' has no free slots ({occupied.Count} occupied)."
                    : $"'{deviceItemPath}' has {free.Count} free slot(s) and {occupied.Count} occupied slot(s).";

                return new ResponseMessage
                {
                    Message = msg,
                    // envelope: legacy-stamp-without-verdict
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["deviceItemPath"] = deviceItemPath,
                        ["freeSlotCount"] = free.Count,
                        ["freeSlots"] = freeArr,
                        ["occupiedSlots"] = occArr,
                        ["note"] = "positionNumber is passed directly to PlugDeviceItem. Free slots are reported by the TIA runtime; "
                                 + "this service does not hardcode a slot table for any CPU."
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error reading plug locations of '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlugDeviceItem"), Description(
            "[L2][Hardware][WRITE] Insert a SUBMODULE into an existing device: signal board (SB, e.g. SB 1221 "
            + "6ES7221-3BD30-0XB0), signal module (SM), or communication module (CM). This is the 'InsertDeviceItem' / "
            + "'AddSignalBoard' operation — CreateDevice only creates whole stations and cannot plug boards into a CPU. "
            + "Defaults to dryRun=true, which runs a REAL TIA feasibility check (CanPlugNew) without writing. "
            + "Pass positionNumber=-1 to let the server pick a free slot reported by TIA; slot numbers are never "
            + "hardcoded — use GetDevicePlugLocations to see them. After a successful plug the module is read back and "
            + "verified (IsPlugged / name / slot). This tool does NOT set addresses: to make the inputs start at %I2.0, "
            + "call SetDeviceItemIoAddress afterwards with startAddress=2, then CompilePlcSoftware and SaveProject. "
            + "Failures are reported by category: SlotOccupied, SlotNotAvailable, OrderNumberNotFound, "
            + "NotSupportedByDevice, PlugFailed, VerifyFailed. Startdrive drive components are plugged on the Device itself: "
            + "plugOnDevice=true with the station name, orderNumber '6SL3xxx-xxxxx-xxxx' (unspecified Motor Module) or a concrete "
            + "MLFB, positionNumber 65535 (official 'Creating a drive component'); motors / encoders then plug below the Motor "
            + "Module item ('OrderNumber:1PH2092-4WG4x-xxxx', 'OrderNumber:XExxxxx-xxxxx-xxxx//DRIVE-CLIQ.202'). Behavior policy is current; existing native selection/retry/overwrite behavior remains pending V4 acceptance.")]
        public CallToolResult PlugDeviceItemV4(
            [Description("deviceItemPath: host device item. A signal board plugs into the CPU itself, e.g. 'PLC_1'; with plugOnDevice=true the device (station) name")] string deviceItemPath,
            [Description("orderNumber: MLFB of the module, e.g. '6ES7221-3BD30-0XB0' (with or without the space). A full 'OrderNumber:.../V1.1' type identifier is also accepted")] string orderNumber,
            [Description("version: module/firmware version, e.g. 'V1.1'. Leave empty to let TIA pick the default")] string version = "",
            [Description("positionNumber: target slot. -1 (default) = pick the first free slot TIA accepts")] int positionNumber = -1,
            [Description("name: name for the new module. Empty = auto-generated and de-duplicated against siblings")] string name = "",
            [Description("dryRun: true (default) only runs the CanPlugNew feasibility check; set false to actually plug")] bool dryRun = true,
            [Description("plugOnDevice: true = plug on the Device (station) itself, the host of Startdrive drive components")] bool plugOnDevice = false)
            => HardwareContract.Run("PlugDeviceItem", () =>
            {
                HardwareContract.RequireProject(_service.HasProject);
                return PlugDeviceItem(deviceItemPath, orderNumber, version, positionNumber, name, dryRun, plugOnDevice);
            }, write: !dryRun, current: true);

        public ResponseMessage PlugDeviceItem(
            [Description("deviceItemPath: host device item. A signal board plugs into the CPU itself, e.g. 'PLC_1'; with plugOnDevice=true the device (station) name")] string deviceItemPath,
            [Description("orderNumber: MLFB of the module, e.g. '6ES7221-3BD30-0XB0' (with or without the space). A full 'OrderNumber:.../V1.1' type identifier is also accepted")] string orderNumber,
            [Description("version: module/firmware version, e.g. 'V1.1'. Leave empty to let TIA pick the default")] string version = "",
            [Description("positionNumber: target slot. -1 (default) = pick the first free slot TIA accepts")] int positionNumber = -1,
            [Description("name: name for the new module. Empty = auto-generated and de-duplicated against siblings")] string name = "",
            [Description("dryRun: true (default) only runs the CanPlugNew feasibility check; set false to actually plug")] bool dryRun = true,
            [Description("plugOnDevice: true = plug on the Device (station) itself, the host of Startdrive drive components")] bool plugOnDevice = false)
        {
            try
            {
                var r = _service.PlugSubmodule(deviceItemPath, orderNumber, version, positionNumber, name, dryRun, plugOnDevice);

                // Reason=VerifyFailed 是 Portal 明写的"插完之后重新定位失败，无法确认结果"——
                // 保留插入尝试和读回证据，由 V4 边界将未验证的写入映射为 unknown。
                var unverified = !r.Ok && string.Equals(r.Reason, "VerifyFailed", StringComparison.Ordinal);

                var attempts = new JsonArray();
                foreach (var a in r.Attempts)
                {
                    attempts.Add(a);
                }

                var meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["dryRun"] = dryRun,
                    // 失败类别是给调用方判定用的结构化字段，别让它只出现在中文正文里。
                    ["reason"] = r.Reason,
                    ["success"] = r.Ok,
                    ["mayHaveChanged"] = !dryRun && (r.Ok || r.Reason == "PlugFailed" || unverified),
                    ["verified"] = r.Ok && !unverified,
                    ["deviceItemPath"] = deviceItemPath,
                    ["orderNumber"] = orderNumber,
                    ["version"] = version,
                    ["requestedPositionNumber"] = positionNumber,
                    ["resolvedTypeIdentifier"] = r.TypeIdentifier,
                    ["resolvedPositionNumber"] = r.PositionNumber,
                    ["attempts"] = attempts
                };

                if (r.FreeSlots != null)
                {
                    var freeArr = new JsonArray();
                    foreach (var f in r.FreeSlots)
                    {
                        freeArr.Add(new JsonObject
                        {
                            ["positionNumber"] = f.PositionNumber,
                            ["label"] = f.Label
                        });
                    }

                    meta["freeSlots"] = freeArr;
                }

                if (r.Plugged != null)
                {
                    meta["plugged"] = new JsonObject
                    {
                        ["name"] = r.Plugged.Name,
                        ["positionNumber"] = r.Plugged.PositionNumber,
                        ["isPlugged"] = r.Plugged.IsPlugged,
                        ["typeIdentifier"] = r.Plugged.TypeIdentifier
                    };
                }

                if (r.Addresses != null)
                {
                    var addrArr = new JsonArray();
                    foreach (var a in r.Addresses)
                    {
                        addrArr.Add(new JsonObject
                        {
                            ["ioType"] = a.IoType,
                            ["startAddress"] = a.StartAddress,
                            ["length"] = a.Length
                        });
                    }

                    meta["addresses"] = addrArr;
                }

                if (r.Ok && !dryRun)
                {
                    meta["nextActions"] = new JsonArray
                    {
                        "SetDeviceItemIoAddress: pass startAddress=2 to start inputs at %I2.0 (this tool does not change addresses)",
                        "CompilePlcSoftware to validate the hardware changes",
                        "SaveProject: save only after compilation reports zero errors"
                    };
                }

                return new ResponseMessage
                {
                    Message = unverified
                        ? $"⚠ UNVERIFIED: {r.Message}: PlugNew has already been called; the module **may already have been inserted**, "
                          + "but readback confirmation failed, so the outcome cannot be determined. Verify with GetDevicePlugLocations or the TIA UI before continuing; "
                          + "do not retry directly (it would insert a duplicate)."
                        : r.Message,
                    Meta = meta
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException(
                    $"Unexpected error plugging '{orderNumber}' into '{deviceItemPath}': {ex.Message}{McpHints.Recovery(ex)}",
                    ex, McpErrorCode.InternalError);
            }
        }

        #endregion

    }
}
