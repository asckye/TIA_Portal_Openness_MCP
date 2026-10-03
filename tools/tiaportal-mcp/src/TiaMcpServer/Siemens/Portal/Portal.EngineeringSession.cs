using System.Collections.Generic;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.RuntimeScripting;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using System;
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
        Microsoft.Extensions.Logging.ILogger? IEngineeringSession.Logger => _logger;
        int IEngineeringSession.PortalMajorVersion => PortalMajorVersion;
        string IEngineeringSession.AvailablePlcPathsSuffix() => AvailablePlcPathsSuffix();
        System.Collections.Generic.List<string>? IEngineeringSession.GetPlcTagTables(string softwarePath, out TagTableWalkDiagnostics diagnostics) => GetPlcTagTables(softwarePath, out diagnostics);
        bool IEngineeringSession.ExportPlcTagTable(string softwarePath, string tagTableName, string exportPath, out string? error) => ExportPlcTagTable(softwarePath, tagTableName, exportPath, out error);
        void IEngineeringSession.ImportPlcTagTable(string softwarePath, string folderPath, string importPath) => ImportPlcTagTable(softwarePath, folderPath, importPath);
        object? IEngineeringSession.TryInvokeMethodByName(object target, string methodName, params object?[] args) => TryInvokeMethodByName(target, methodName, args);
        bool IEngineeringSession.TryExportEngineeringObject(object engineeringObject, string exportPath, out string? error) => TryExportEngineeringObject(engineeringObject, exportPath, out error);
        System.Collections.Generic.List<string>? IEngineeringSession.TryListNamesFromCollection(object root, string[] propHints, string kind) => TryListNamesFromCollection(root, propHints, kind);
        object? IEngineeringSession.TryFindByNameInCollection(object root, string[] propHints, string name) => TryFindByNameInCollection(root, propHints, name);
        string IEngineeringSession.MakeSafeFileName(string name) => MakeSafeFileName(name);
        string? IEngineeringSession.TryGetName(object? value) => TryGetName(value);
        System.Collections.Generic.IEnumerable<ObjectMember> IEngineeringSession.DescribeMembers(object value, int maxMembers) => DescribeMembers(value, maxMembers);
        Type? IEngineeringSession.FindTypeBySuffix(string typeSuffix) => FindTypeBySuffix(typeSuffix);
        object? IEngineeringSession.TryGetService(object target, Type serviceType) => TryGetService(target, serviceType);

        System.Collections.Generic.IEqualityComparer<object> IEngineeringSession.ReferenceEqualityComparer => ReferenceEqualityComparer.Instance;
        string IEngineeringSession.FormatExceptionDetail(Exception exception) => FormatExceptionDetail(exception);
        DeviceItem IEngineeringSession.ExactDeviceItem(string[] devicePath, string[] itemPath) => ExactDeviceItem(devicePath, itemPath);
        object IEngineeringSession.EngineeringLibraryFolder(object library, string folderPath, string rootProperty) => EngineeringLibraryFolder(library, folderPath, rootProperty);
        string IEngineeringSession.LibraryPathOf(object node, string rootTypeName) => LibraryPathOf(node, rootTypeName);
        string IEngineeringSession.OptionPackageLibraryTypeKind(LibraryType type) => OptionPackageLibraryTypeKind(type);
        Device IEngineeringSession.ExactEngineeringDevice(string pathJson) => ExactEngineeringDevice(pathJson);
        object IEngineeringSession.ResolveHmiScreenOrThrow(string hmiSoftwarePath, string screenName) => ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
        string[] IEngineeringSession.ExactNameList(string json) => ExactNameList(json);
        Portal.SivarcFamily IEngineeringSession.GetSivarcFamily(string category) => Family(category);
        global::Siemens.Engineering.SiVArc.Sivarc IEngineeringSession.RequireSivarc() => RequireSivarc();
        Exception IEngineeringSession.SivarcShape(object node) => SivarcShape(node);
        JsonObject IEngineeringSession.DescribeNode(object target, int depth) => DescribeNode(target, depth);
        PlcTag IEngineeringSession.ExactPlcTag(string softwarePath, string tagPath) => ExactPlcTag(softwarePath, tagPath);
        HmiTarget IEngineeringSession.ExactClassicHmi(string softwarePath) => ExactClassicHmi(softwarePath);
        VBScriptFolder IEngineeringSession.ExactVbScriptFolder(HmiTarget hmi, IEnumerable<string> folderPath) => ExactVbScriptFolder(hmi, folderPath);
        JsonObject IEngineeringSession.TechnologyObjectRow(TechnologicalInstanceDB db) => TechnologyObjectRow(db);
        JsonObject IEngineeringSession.TypedMotionView(TechnologicalInstanceDB to) => TypedMotionView(to);
        JsonObject IEngineeringSession.ParameterRow(TechnologicalParameter parameter) => ParameterRow(parameter);
        JsonObject IEngineeringSession.MappingRow(object mapping) => MappingRow(mapping);
        bool IEngineeringSession.DisconnectTyped(object iface) => DisconnectTyped(iface);
        bool? IEngineeringSession.IsConnectedTyped(object iface) => IsConnectedTyped(iface);
        Channel IEngineeringSession.ExactChannel(DeviceItem item, string channelType, string channelIoType, int channelNumber) => ExactChannel(item, channelType, channelIoType, channelNumber);
        bool IEngineeringSession.ConnectTyped(object iface, MotionProDiagClassicHmiLogic.ConnectionTarget target, string softwarePath, ConnectOption option) => ConnectTyped(iface, target, softwarePath, option);
        void IEngineeringSession.ImportTechnologyObject(string softwarePath, string folderPath, string importPath, bool overwrite, List<string> importedNames)
            => ImportTechnologyObject(softwarePath, folderPath, importPath, overwrite, importedNames);
        bool IEngineeringSession.TrySetProperty(object target, string propName, object? value) => TrySetProperty(target, propName, value);
        string IEngineeringSession.GetProjectTree() => GetProjectTree();
        List<Device> IEngineeringSession.GetDevices(string regexName) => GetDevices(regexName);
        Device? IEngineeringSession.GetDevice(string devicePath) => GetDevice(devicePath);
        DeviceItem? IEngineeringSession.GetDeviceItem(string deviceItemPath) => GetDeviceItem(deviceItemPath);
        string IEngineeringSession.GetDeviceItemTree(string deviceItemPath, int maxDepth) => GetDeviceItemTree(deviceItemPath, maxDepth);
        Device? IEngineeringSession.GetDeviceByPath(string devicePath) => GetDeviceByPath(devicePath);
        IEnumerable<object> IEngineeringSession.FindHardwareCatalogEntries(object catalog, string filter) => FindHardwareCatalogEntries(catalog, filter);
        bool? IEngineeringSession.IsAttributeWritable(object attributeInfo) => IsAttributeWritable(attributeInfo);
        object IEngineeringSession.CoerceAttributeValue(string value, object? oldValue, object attributeInfo) => CoerceAttributeValue(value, oldValue, attributeInfo);
        JsonArray IEngineeringSession.BuildDeviceItemNetworkReadbackJson(string deviceItemPath) => BuildDeviceItemNetworkReadbackJson(deviceItemPath);
        IEnumerable<Device> IEngineeringSession.EnumerateAllDevices() => EnumerateAllDevices();

        string[] IEngineeringSession.GetPlcSoftwareNamesForDesktop() => GetPlcSoftwareNamesForDesktop();
        ResponseMessage IEngineeringSession.ValidateAutomationContext(string expectedPlcSoftwarePath, string expectedHmiSoftwarePath)
            => ValidateAutomationContext(expectedPlcSoftwarePath, expectedHmiSoftwarePath);
        JsonArray IEngineeringSession.ToJsonArray(IEnumerable<string> values) => ToJsonArray(values);
        DeviceItem IEngineeringSession.RequireDeviceItem(HardwareObject owner, string parameter) => RequireDeviceItem(owner, parameter);
        void IEngineeringSession.ApplyScalarsAndAttributes(object target, string propertiesJson, string attributesJson, JsonObject meta, bool write) => ApplyScalarsAndAttributes(target, propertiesJson, attributesJson, meta, write);
        IEngineeringServiceProvider IEngineeringSession.ServiceProvider(HardwareObject owner) => ServiceProvider(owner);
        JsonNode? IEngineeringSession.LinkedTagRows(Channel channel, JsonObject row) => LinkedTagRows(channel, row);
        System.Collections.Generic.List<PlcSoftware> IEngineeringSession.GetAllPlcSoftware() => GetAllPlcSoftware();
        System.Collections.Generic.List<(string Path, bool? Consistent)> IEngineeringSession.ReadPlcConsistency(string softwarePath)
            => ReadPlcConsistency(softwarePath);
        bool IEngineeringSession.RecoverableAuditError(Exception ex) => RecoverableAuditError(ex);
        string IEngineeringSession.ReadReflectedString(object? owner, string propertyName) => ReadReflectedString(owner, propertyName);
        System.Collections.Generic.List<object?> IEngineeringSession.EnumerateReflectedProperty(object? owner, string propertyName)
            => EnumerateReflectedProperty(owner, propertyName);

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
