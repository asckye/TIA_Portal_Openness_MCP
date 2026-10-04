using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Utilities;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // ---- object identifiers and show-in-editor -------------------------------------------------------------------------------
        private IEngineeringObject ExactIdentifiableObject(string kind, string devicePathJson, string itemPathJson, string softwarePath, string objectPath, JsonObject meta)
        {
            if (kind == "device" || kind == "deviceItem")
            {
                var hardware = ExactEngineeringHardware(devicePathJson, kind == "device" ? "[]" : itemPathJson); meta["ownerPath"] = HardwareOwnerPath(hardware);
                return hardware;
            }
            var plc = ExactPlcForEngineering(softwarePath, false); var parts = EngineeringGroupOperations.Parts(objectPath);
            object root = kind == "plcBlock" ? plc.BlockGroup : kind == "plcType" ? (object)plc.TypeGroup : plc.TagTableGroup;
            var group = EngineeringGroupOperations.Group(root, string.Join("/", parts.Take(parts.Length - 1)));
            var collection = EngineeringGroupOperations.Get(group, kind == "plcBlock" ? "Blocks" : kind == "plcType" ? "Types" : "TagTables");
            return EngineeringGroupOperations.Find(collection, parts.Last()) as IEngineeringObject ?? throw new PortalException(PortalErrorCode.NotFound, "Exact " + kind + " not found: " + objectPath);
        }
        private static JsonObject IdentifiedObjectRow(IEngineeringObject target)
        {
            var row = new JsonObject { ["objectClass"] = target.GetType().Name };
            try { row["name"] = target.GetType().GetProperty("Name")?.GetValue(target)?.ToString(); } catch /* swallow(probe-optional): objects without a readable Name retain their class identity */ { }
            if (target is ISystemObject systemObject) { try { row["isSystemObject"] = systemObject.IsSystemObject; } catch (Exception ex) { row["isSystemObjectError"] = ex.GetBaseException().Message; } }
            return row;
        }

        public ResponseMessage ReadObjectIdentifier(string kind = "device", string devicePathJson = "[]", string itemPathJson = "[]", string softwarePath = "", string objectPath = "", string identifier = "")
            => RunHmiStepTool("ReadObjectIdentifier", meta => {
                ObjectIdentityRules.ValidateObjectSelection(kind, devicePathJson, itemPathJson, softwarePath, objectPath, identifier);
                ObjectIdentifierProvider provider = _project!.GetService<ObjectIdentifierProvider>() ?? throw new NotSupportedException("ObjectIdentifierProvider service unavailable on this project.");
                meta["kind"] = kind;
                if (!string.IsNullOrEmpty(identifier))
                {
                    // Find(identifier) returns the object behind an identifier issued earlier (cross-session stable per the official page).
                    var found = provider.Find(identifier) ?? throw new PortalException(PortalErrorCode.NotFound, "ObjectIdentifierProvider.Find returned null for the identifier.");
                    meta["identifier"] = identifier; meta["found"] = IdentifiedObjectRow(found);
                    if (found is HardwareObject hardware) meta["ownerPath"] = HardwareOwnerPath(hardware);
                    meta["apiCallSuccess"] = true; meta["dataComplete"] = true; return "Object found from its identifier; no modification.";
                }
                var target = ExactIdentifiableObject(kind, devicePathJson, itemPathJson, softwarePath, objectPath, meta);
                meta["object"] = IdentifiedObjectRow(target); meta["identifier"] = provider.GetIdentifier(target);
                meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                meta["scope"] = "ObjectIdentifierProvider.GetIdentifier / Find; official support: Device, DeviceItem, code and data blocks, PLC tags, software units, TechnologicalInstanceDB, PlcStruct.";
                return "Object identifier read; no modification.";
            });

        public ResponseMessage ShowObjectInEditor(string kind = "device", string devicePathJson = "[]", string itemPathJson = "[]", string softwarePath = "", string objectPath = "", bool dryRun = true)
            => RunHmiStepTool("ShowObjectInEditor", meta => {
                ObjectIdentityRules.ValidateObjectSelection(kind, devicePathJson, itemPathJson, softwarePath, objectPath, "");
                var target = ExactIdentifiableObject(kind, devicePathJson, itemPathJson, softwarePath, objectPath, meta);
                meta["kind"] = kind; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["object"] = IdentifiedObjectRow(target);
                IShowable showable = target as IShowable ?? throw new NotSupportedException(target.GetType().Name + " does not implement IShowable (Device and the STEP 7 blocks / types / tag tables / watch and force tables do).");
                if (dryRun) return "ShowInEditor preview; the TIA Portal UI was not touched.";
                showable.ShowInEditor(); meta["apiCallSuccess"] = true;
                return "IShowable.ShowInEditor invoked: the object is opened in the TIA Portal editor (UI only; no project change).";
            });

    }
}
