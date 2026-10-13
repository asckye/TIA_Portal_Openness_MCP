#if OPTIONAL_V20_PLUS
using static TiaMcp.Adapters.Native.Optional.OptionalEvidence;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using EngineeringGroupOperations = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;
using EngineeringScalarProperties = TiaMcp.Adapters.Native.Optional.OptionalScalars;
using EngineeringObjectAddress = TiaMcp.Adapters.Native.Optional.OptionalObjectAddress;
using NativeFileOutput = TiaMcp.Adapters.Native.Safety.SafetyFileOutput;
using PlcBlockServicesLogic = TiaMcp.Adapters.Native.Safety.SafetyPrimitives;
using HardwareServicesLogic = TiaMcp.Adapters.Native.Optional.OptionalRules;
using ArgumentRules = TiaMcp.Adapters.Native.Optional.OptionalRules;
using EngineeringPath = TiaMcp.Adapters.Hardware.HardwarePath;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.DFI;
using Siemens.Engineering.MC.Drives.Enums;
using Siemens.Engineering.MC.Drives.SecurityObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcp.Adapters.Native.Optional.StartdrivePolicy;
using MotionConnectOption = Siemens.Engineering.SW.TechnologicalObjects.Motion.ConnectOption;
using DriveEncoderType = Siemens.Engineering.MC.Drives.Enums.EncoderType;
#if !OPTIONAL_V21
using DriveConnectOption = Siemens.Engineering.MC.Drives.Enums.ConnectOption;
#endif

namespace TiaMcp.Adapters.Native.Optional
{
    // Shared resolution and rows used by the DCC and Startdrive services.
    internal sealed partial class OptionalSession
    {
        internal DeviceItem ExactDriveItem(string devicePathJson, string itemPathJson)
            => ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("itemPathJson must select a device item (the drive unit / control unit), not the device itself.");
        internal DriveObjectContainer ExactDriveContainer(DeviceItem item)
            => item.GetService<DriveObjectContainer>() ?? throw new PlcSoftwareException("NotSupportedOnVersion", "DriveObjectContainer unavailable on device item '" + item.Name + "' (not a Startdrive drive object host, or Startdrive not installed).");
        internal static ushort? SafeDriveObjectNumber(DriveObject drive) { try { return drive.DriveObjectNumber; } catch { /* swallow(probe-optional): G120C may not expose DriveObjectNumber; the selector also supports composition index. */ return null; } }
        internal static DriveObject ExactDriveObject(DriveObjectContainer container, Logic.DriveSelector selector)
        {
            DriveObjectComposition objects = container.DriveObjects;
            if (selector.ByNumber)
            {
                var matches = objects.Where(o => SafeDriveObjectNumber(o) == selector.Number).Take(2).ToArray();
                if (matches.Length != 1) throw new PlcSoftwareException("NotFound", "Exactly one drive object with " + selector.Label + " expected, found " + matches.Length + " (use driveObjectIndex when DriveObjectNumber is unavailable on this drive).");
                return matches[0];
            }
            if (selector.Index >= objects.Count) throw new PlcSoftwareException("NotFound", selector.Label + " out of range: the container has " + objects.Count + " drive object(s).");
            return objects[selector.Index];
        }
        internal DriveObject ExactDriveObject(string devicePathJson, string itemPathJson, ushort driveObjectNumber, int driveObjectIndex)
            => ExactDriveObject(ExactDriveContainer(ExactDriveItem(devicePathJson, itemPathJson)), Logic.ParseDriveSelector(driveObjectNumber, driveObjectIndex));
    }
}

#endif
