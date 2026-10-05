using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.Dcc;
using Siemens.Engineering.MC.Drives.Dcc.DccExceptions;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.DccLogic;

namespace TiaMcpServer.Siemens
{
    // Shared resolution and rows used by the DCC and Startdrive services.
    public partial class Portal
    {
        private static JsonObject? DccContainerSummary(DriveObject drive)
        {
            var container = drive.GetService<DriveControlChartContainer>(); if (container == null) return null;
            var row = new JsonObject { ["available"] = true };
            Safe(row, "chartCount", () => container.Charts.Count); Safe(row, "charts", () => new JsonArray(EngineeringGroupOperations.Items(container.Charts).Cast<DriveControlChart>().Take(100).Select(c => (JsonNode)c.Name).ToArray()));
            Safe(row, "dcbLibraries", () => new JsonArray(EngineeringGroupOperations.Items(container.DcbLibraries).Cast<DcbLibrary>().Select(l => (JsonNode)DcbLibraryRow(l, false)).ToArray()));
            return row;
        }
        private static JsonObject DcbLibraryRow(DcbLibrary l, bool types)
        {
            var row = new JsonObject { ["libraryName"] = l.LibraryName };
            Safe(row, "version", () => l.Version?.ToString()); Safe(row, "blockTypeCount", () => l.BlockTypes.Count);
            if (types) Safe(row, "blockTypes", () => new JsonArray(EngineeringGroupOperations.Items(l.BlockTypes).Cast<DcbBlockType>().Take(500).Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Description }).ToArray()));
            return row;
        }
    }
}
