#if OPTIONAL_V20_PLUS
using static TiaMcp.Adapters.Native.Optional.OptionalEvidence;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.Dcc;
using Siemens.Engineering.MC.Drives.Dcc.DccExceptions;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcp.Adapters.Native.Optional.DccPolicy;

namespace TiaMcp.Adapters.Native.Optional
{
    // Shared resolution and rows used by the DCC and Startdrive services.
    internal sealed partial class OptionalSession
    {
        internal static HmiObject? DccContainerSummary(DriveObject drive)
        {
            var container = drive.GetService<DriveControlChartContainer>(); if (container == null) return null;
            var row = new HmiObject { ["available"] = true };
            Safe(row, "chartCount", () => container.Charts.Count); Safe(row, "charts", () => new HmiArray(EngineeringGroupOperations.Items(container.Charts).Cast<DriveControlChart>().Take(100).Select(c => (object)c.Name).ToArray()));
            Safe(row, "dcbLibraries", () => new HmiArray(EngineeringGroupOperations.Items(container.DcbLibraries).Cast<DcbLibrary>().Select(l => (object)DcbLibraryRow(l, false)).ToArray()));
            return row;
        }
        internal static HmiObject DcbLibraryRow(DcbLibrary l, bool types)
        {
            var row = new HmiObject { ["libraryName"] = l.LibraryName };
            Safe(row, "version", () => l.Version?.ToString()); Safe(row, "blockTypeCount", () => l.BlockTypes.Count);
            if (types) Safe(row, "blockTypes", () => new HmiArray(EngineeringGroupOperations.Items(l.BlockTypes).Cast<DcbBlockType>().Take(500).Select(t => (object)new HmiObject { ["name"] = t.Name, ["description"] = t.Description }).ToArray()));
            return row;
        }
    }
}

#endif
