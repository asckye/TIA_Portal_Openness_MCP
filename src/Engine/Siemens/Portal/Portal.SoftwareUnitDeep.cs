using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Alarm.TextLists;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Step7 software units and the small PlcSoftware service providers. Official pages:
    // "Accessing software unit" / "Working with software unit" / "Accessing software unit underlying objects" / "Updating
    // software unit properties" / "Accessing namespaces for software units" / "Units as mastercopies", "Accessing the
    // SafetyUnit" / "Creating/deleting SafetyUnit relations", "Accessing name value type document", "Exporting UDT as
    // document" / "Importing UDT from document", "Accessing Software Checksum", "Changing blocks using fingerprints",
    // "Setting up write protection of blocks", "Updating project properties" (simulation / virtual PLC support) and
    // "Accessing attributes of an address object" (process image assignment). Everything is the official V20/V21 API;
    // V21-only members (PlcUnitSystemGroup.Name, PlcDocumentComposition.CreateFrom, PlcBlockWriteProtectionProvider,
    // PlcSimulationSettingsProvider / VirtualPlcSettingsProvider, PlcTagProvider, ProcessImageProvider) are #if-guarded.
    public partial class Portal
    {
        // ---- unit resolution ----------------------------------------------------------------------------------------------------
        private static PlcUnitProvider RequireUnitProvider(PlcSoftware plc) => TiaMcp.Adapters.Native.Plc.PlcUnitScope.RequireProvider(plc);
        private static PlcUnitBase ExactUnit(PlcUnitSystemGroup group, string unitKind, string name)
        {
            try { return TiaMcp.Adapters.Native.Plc.PlcUnitScope.Exact(group, unitKind, name); }
            catch (TiaMcp.Adapters.Contracts.PlcSoftwareException error) { throw new PortalException(PortalErrorCode.NotFound, error.Message); }
        }
        private PlcUnitBase? OptionalUnit(PlcSoftware plc, string unitName, string unitKind)
            => string.IsNullOrEmpty(unitName) ? null : ExactUnit(RequireUnitProvider(plc).UnitGroup, unitKind, unitName);
        private static PlcBlockGroup BlockRootOf(PlcSoftware plc, PlcUnitBase? unit) => TiaMcp.Adapters.Native.Plc.PlcUnitScope.Blocks(plc, unit);
        private static PlcTypeGroup TypeRootOf(PlcSoftware plc, PlcUnitBase? unit) => TiaMcp.Adapters.Native.Plc.PlcUnitScope.Types(plc, unit);
        private static object ExactObjectUnder(object root, string objectPath, string collection, string label)
        {
            var parts = EngineeringGroupOperations.Parts(objectPath);
            var group = EngineeringGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
            return EngineeringGroupOperations.Find(EngineeringGroupOperations.Get(group, collection), parts.Last()) ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + label + " not found: " + objectPath);
        }

        private static JsonArray DocumentMessages(DocumentResultMessageComposition? messages)
            => messages == null ? new JsonArray() : new JsonArray(EngineeringGroupOperations.Items(messages).Cast<DocumentResultMessage>().Select(m => (JsonNode)m.Message).ToArray());
        private static JsonObject DocumentExportRow(DocumentExportResult result)
            => new JsonObject { ["state"] = result.State.ToString(), ["exportedDocuments"] = new JsonArray((result.ExportedDocuments ?? Enumerable.Empty<FileInfo>()).Select(f => (JsonNode)SoftwareUnitDeepLogic.FileRow(f)).ToArray()), ["messages"] = DocumentMessages(result.Messages) };
        private static JsonObject DocumentImportRow(DocumentImportResult result, JsonArray imported)
            => new JsonObject { ["state"] = result.State.ToString(), ["imported"] = imported, ["messages"] = DocumentMessages(result.Messages) };

    }
}
