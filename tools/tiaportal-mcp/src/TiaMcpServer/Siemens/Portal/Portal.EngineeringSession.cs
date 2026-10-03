using System;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal : IEngineeringSession
    {
        TiaPortal? IEngineeringSession.CurrentPortal => _portal;
        ProjectBase? IEngineeringSession.CurrentProject => CurrentProject;
        LocalSession? IEngineeringSession.CurrentSession => _session;

        ResponseMessage IEngineeringSession.RunHmiStepTool(string toolName, Func<JsonObject, string> action, bool requiresProject)
            => RunHmiStepTool(toolName, action, requiresProject);
        IDisposable IEngineeringSession.AcquireHmiEditAccess() => AcquireHmiEditAccess();
        bool IEngineeringSession.IsProjectNull() => IsProjectNull();
        void IEngineeringSession.VerifyBinding(string operation) => VerifyBinding(operation);
        SoftwareContainer? IEngineeringSession.GetSoftwareContainer(string softwarePath) => GetSoftwareContainer(softwarePath);
        PlcSoftware? IEngineeringSession.GetPlcSoftware(string softwarePath) => GetPlcSoftware(softwarePath);
        PlcSoftware? IEngineeringSession.ResolvePlc(string softwarePath, PlcAccess access) => ResolvePlc(softwarePath, access);
        T? IEngineeringSession.ResolvePlcService<T>(string softwarePath, PlcSoftware plcSoftware) where T : class
            => ResolvePlcService<T>(softwarePath, plcSoftware);
        DeviceItem? IEngineeringSession.GetDeviceItemByPath(string deviceItemPath) => GetDeviceItemByPath(deviceItemPath);
        PlcSoftware IEngineeringSession.ExactPlcForEngineering(string softwarePath, bool writing) => ExactPlcForEngineering(softwarePath, writing);
        HardwareObject IEngineeringSession.ExactEngineeringHardware(string devicePathJson, string itemPathJson)
            => ExactEngineeringHardware(devicePathJson, itemPathJson);
        object IEngineeringSession.ExactOpenEngineeringLibrary(string libraryName) => ExactOpenEngineeringLibrary(libraryName);
        object IEngineeringSession.ResolveHmiSoftwareOrThrow(string hmiSoftwarePath) => ResolveHmiSoftwareOrThrow(hmiSoftwarePath);

        SoftwareContainer? IEngineeringSession.ResolveSoftwareContainerUncached(string softwarePath) => ResolveSoftwareContainerUncached(softwarePath);
        T IEngineeringSession.RequireHardwareUtility<T>(string identifier) => RequireHardwareUtility<T>(identifier);
        object IEngineeringSession.ExactSiVArcRoot(string category) => ExactSiVArcRoot(category);
        MasterCopy IEngineeringSession.ExactMasterCopy(string libraryName, string path) => ExactMasterCopy(libraryName, path);
        LibraryType IEngineeringSession.ExactLibraryType(object library, string typePath) => ExactLibraryType(library, typePath);
        LibraryTypeVersion IEngineeringSession.ExactTypeVersion(LibraryType type, string version) => ExactTypeVersion(type, version);
        JsonNode? IEngineeringSession.MultilingualJson(MultilingualText? text) => MultilingualJson(text);
        PlcUnitProvider IEngineeringSession.RequireUnitProvider(PlcSoftware plc) => RequireUnitProvider(plc);
        PlcUnitBase IEngineeringSession.ExactUnit(PlcUnitSystemGroup group, string unitKind, string name) => ExactUnit(group, unitKind, name);
        PlcUnitBase? IEngineeringSession.OptionalUnit(PlcSoftware plc, string unitName, string unitKind) => OptionalUnit(plc, unitName, unitKind);
        PlcBlockGroup IEngineeringSession.BlockRootOf(PlcSoftware plc, PlcUnitBase? unit) => BlockRootOf(plc, unit);
        PlcTypeGroup IEngineeringSession.TypeRootOf(PlcSoftware plc, PlcUnitBase? unit) => TypeRootOf(plc, unit);
        object IEngineeringSession.ExactObjectUnder(object root, string objectPath, string collection, string label)
            => ExactObjectUnder(root, objectPath, collection, label);
        JsonArray IEngineeringSession.DocumentMessages(DocumentResultMessageComposition? messages) => DocumentMessages(messages);
        JsonObject IEngineeringSession.DocumentExportRow(DocumentExportResult result) => DocumentExportRow(result);
        JsonObject IEngineeringSession.DocumentImportRow(DocumentImportResult result, JsonArray imported) => DocumentImportRow(result, imported);

        void IEngineeringSession.AdoptProject(ProjectBase? project, string missingProjectMessage) => AdoptProject(project, missingProjectMessage);
        void IEngineeringSession.ReleaseProject() => ReleaseProject();

        private void AdoptProject(ProjectBase? project, string missingProjectMessage)
        {
            _project = project;
            if (_project == null) throw new InvalidOperationException(missingProjectMessage);
            _projectOpenedByUs = true; RememberExpectedProject();
            InvalidateHmiSoftwareCache(); ResetHmiReadHealth();
        }

        private void ReleaseProject()
        {
            _session = null; _project = null; _projectOpenedByUs = false; InvalidateHmiSoftwareCache();
        }
    }
}
