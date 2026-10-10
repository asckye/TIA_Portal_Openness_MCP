using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed partial class PlcOrganisationPortService
    {
        private ResponseMessage IntegrityStep(PlcSoftwareRequest request)
            => OrganisationStep(request);
        public ResponseMessage ReadPlcSoftwareUnits(string softwarePath, string unitName = "", string unitKind = "all", bool includeContents = true, int offset = 0, int limit = 50)
            => IntegrityStep(new PlcSoftwareRequest { Operation = "ListPlcSoftwareUnits", SoftwarePath = softwarePath, UnitName = unitName, UnitKind = unitKind, IncludeBlocks = includeContents, Offset = offset, Limit = limit });
        public ResponseMessage ReadPlcChecksums(string softwarePath)
            => IntegrityStep(new PlcSoftwareRequest { Operation = "GetPlcChecksums", SoftwarePath = softwarePath });
        public ResponseMessage ReadPlcObjectFingerprints(string softwarePath, string objectKind, string objectPath, string unitName = "", string unitKind = "unit")
            => IntegrityStep(new PlcSoftwareRequest { Operation = "GetPlcObjectFingerprints", SoftwarePath = softwarePath, Kind = objectKind, Path = objectPath, UnitName = unitName, UnitKind = unitKind });
        public ResponseMessage ManagePlcBlockWriteProtection(string softwarePath, string blockPath, string action, string password = "", string newPassword = "", bool confirmProtectionChange = false, bool dryRun = true, string unitName = "", string unitKind = "unit")
            => IntegrityStep(new PlcSoftwareRequest { Operation = "ManagePlcBlockWriteProtection", SoftwarePath = softwarePath, Path = blockPath, Action = action, Password = password, NewPassword = newPassword, Confirm = confirmProtectionChange, DryRun = dryRun, UnitName = unitName, UnitKind = unitKind });
        public ResponseMessage SetPlcUnitObjectAccess(string softwarePath, string unitName, string objectKind, string objectPath, string access, bool dryRun = true)
            => IntegrityStep(new PlcSoftwareRequest { Operation = "SetPlcUnitObjectAccess", SoftwarePath = softwarePath, UnitName = unitName, Kind = objectKind, Path = objectPath, Access = access, DryRun = dryRun });
        public ResponseMessage ManagePlcSoftwareUnit(string softwarePath, string action, string name = "", string relatedUnit = "", string relationType = "", string propertiesJson = "{}", bool dryRun = true, string unitKind = "unit", string commentsJson = "{}", string libraryName = "", string masterCopyPath = "", string copyMode = "")
        {
            var request = new PlcSoftwareRequest { Operation = "ManagePlcSoftwareUnit", SoftwarePath = softwarePath, Action = action, Name = name, RelatedUnit = relatedUnit, RelationType = relationType, DryRun = dryRun, UnitKind = unitKind, LibraryName = libraryName, MasterCopyPath = masterCopyPath, CopyMode = copyMode };
            try
            {
                var validated = SoftwareUnitDeepLogic.ValidateUnitRequest(action, unitKind, name, relatedUnit, relationType, propertiesJson, commentsJson, libraryName, masterCopyPath, copyMode, dryRun);
                request.Properties = validated.Properties.ToDictionary(p => p.Key, p => new HardwareScalar { Kind = "string", Text = p.Value!.GetValue<string>() });
                request.Comments = validated.Comments.Select(c => new PlcTagComment { Culture = c.Culture, Text = c.Text }).ToArray();
            }
            catch (Exception error) { request.ValidationError = error.Message; }
            if (HardwareContract.ReleaseKey is "16" or "17" && request.Properties.ContainsKey("NamespacePreset"))
                throw new NotSupportedException("PlcUnitBase.NamespacePreset requires V18.");
            return IntegrityStep(request);
        }
    }
}
