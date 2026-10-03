using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.DFI;
using Siemens.Engineering.MC.Drives.Enums;
using Siemens.Engineering.MC.Drives.SecurityObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.StartdriveLogic;
using MotionConnectOption = Siemens.Engineering.SW.TechnologicalObjects.Motion.ConnectOption;
using DriveEncoderType = Siemens.Engineering.MC.Drives.Enums.EncoderType;
#if TIA_V20
using DriveConnectOption = Siemens.Engineering.MC.Drives.Enums.ConnectOption;
#endif

namespace TiaMcpServer.Siemens
{
    // Shared resolution and rows used by the DCC and Startdrive services.
    public partial class Portal
    {
        private DeviceItem ExactDriveItem(string devicePathJson, string itemPathJson)
            => ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("itemPathJson must select a device item (the drive unit / control unit), not the device itself.");
        private DriveObjectContainer ExactDriveContainer(DeviceItem item)
            => item.GetService<DriveObjectContainer>() ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "DriveObjectContainer unavailable on device item '" + item.Name + "' (not a Startdrive drive object host, or Startdrive not installed).");
        private static ushort? SafeDriveObjectNumber(DriveObject drive) { try { return drive.DriveObjectNumber; } catch { /* swallow(probe-optional): G120C may not expose DriveObjectNumber; the selector also supports composition index. */ return null; } }
        private static DriveObject ExactDriveObject(DriveObjectContainer container, Logic.DriveSelector selector)
        {
            DriveObjectComposition objects = container.DriveObjects;
            if (selector.ByNumber)
            {
                var matches = objects.Where(o => SafeDriveObjectNumber(o) == selector.Number).Take(2).ToArray();
                if (matches.Length != 1) throw new PortalException(PortalErrorCode.NotFound, "Exactly one drive object with " + selector.Label + " expected, found " + matches.Length + " (use driveObjectIndex when DriveObjectNumber is unavailable on this drive).");
                return matches[0];
            }
            if (selector.Index >= objects.Count) throw new PortalException(PortalErrorCode.NotFound, selector.Label + " out of range: the container has " + objects.Count + " drive object(s).");
            return objects[selector.Index];
        }
        private DriveObject ExactDriveObject(string devicePathJson, string itemPathJson, ushort driveObjectNumber, int driveObjectIndex)
            => ExactDriveObject(ExactDriveContainer(ExactDriveItem(devicePathJson, itemPathJson)), Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex));
    }
}
