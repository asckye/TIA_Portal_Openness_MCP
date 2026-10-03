using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.Dcc;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using Siemens.Engineering.Umac;
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

        DeviceItem IEngineeringSession.ExactDriveItem(string devicePathJson, string itemPathJson) => ExactDriveItem(devicePathJson, itemPathJson);
        DriveObjectContainer IEngineeringSession.ExactDriveContainer(DeviceItem item) => ExactDriveContainer(item);
        DriveObject IEngineeringSession.ExactDriveObject(string devicePathJson, string itemPathJson, ushort driveObjectNumber, int driveObjectIndex) => ExactDriveObject(devicePathJson, itemPathJson, driveObjectNumber, driveObjectIndex);
        JsonObject? IEngineeringSession.DccContainerSummary(DriveObject drive) => DccContainerSummary(drive);
        JsonObject IEngineeringSession.DcbLibraryRow(DcbLibrary library, bool types) => DcbLibraryRow(library, types);
        JsonObject IEngineeringSession.AddressRow(Address address) => AddressRow(address);
        object IEngineeringSession.ExactTechnology(string softwarePath, string objectPath, bool writing) => ExactTechnology(softwarePath, objectPath, writing);
        JsonObject IEngineeringSession.InterfaceRow(object iface) => InterfaceRow(iface);
        object IEngineeringSession.ExactMasterCopyPlcSource(string softwarePath, string sourcePath, bool block) => ExactMasterCopyPlcSource(softwarePath, sourcePath, block);
        void IEngineeringSession.GetBlocksRecursive(PlcBlockGroup group, List<PlcBlock> list, string regexName) => GetBlocksRecursive(group, list, regexName);
        JsonArray IEngineeringSession.HardwareOwnerPath(object? start) => HardwareOwnerPath(start);
        T IEngineeringSession.RequireHardwareService<T>(HardwareObject owner, string parameter) where T : class => RequireHardwareService<T>(owner, parameter);
        JsonObject IEngineeringSession.DynamicAttributes(IEngineeringObject target, string[] names) => DynamicAttributes(target, names);
        void IEngineeringSession.SetDynamicAttributes(IEngineeringObject target, JsonObject attributes, JsonObject meta) => SetDynamicAttributes(target, attributes, meta);
        object? IEngineeringSession.FindOnFresh(Func<object> composition, string name, JsonObject meta, string phase) => FindOnFresh(composition, name, meta, phase);
        int? IEngineeringSession.CountOnFresh(Func<object> composition, JsonObject meta, string key) => CountOnFresh(composition, meta, key);
        UmacConfigurator IEngineeringSession.RequireUmac() => RequireUmac();
        object? IEngineeringSession.FindOrdinal(object collection, string name, string kind) => FindOrdinal(collection, name, kind);
        Role IEngineeringSession.ExactUmacRole(UmacConfigurator umac, string name) => ExactUmacRole(umac, name);
        JsonObject IEngineeringSession.UmacRow(object item) => UmacRow(item);
        JsonObject IEngineeringSession.LibraryRef(object library) => LibraryRef(library);
        IEnumerable<Device> IEngineeringSession.EnumerateGroupDevices(DeviceUserGroupComposition? groups) => EnumerateGroupDevices(groups);

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
