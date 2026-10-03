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
using Siemens.Engineering.HW.Utilities;
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
    internal enum PlcAccess { Read, Write }

    internal interface IEngineeringSession
    {
        TiaPortal? CurrentPortal { get; }
        ProjectBase? CurrentProject { get; }
        LocalSession? CurrentSession { get; }

        ResponseMessage RunHmiStepTool(string toolName, Func<JsonObject, string> action, bool requiresProject = true);
        IDisposable AcquireHmiEditAccess();
        bool IsProjectNull();
        void VerifyBinding(string operation);
        SoftwareContainer? GetSoftwareContainer(string softwarePath);
        PlcSoftware? GetPlcSoftware(string softwarePath);
        PlcSoftware? ResolvePlc(string softwarePath, PlcAccess access);
        T? ResolvePlcService<T>(string softwarePath, PlcSoftware plcSoftware) where T : class, IEngineeringService;
        DeviceItem? GetDeviceItemByPath(string deviceItemPath);
        PlcSoftware ExactPlcForEngineering(string softwarePath, bool writing);
        HardwareObject ExactEngineeringHardware(string devicePathJson, string itemPathJson);
        object ExactOpenEngineeringLibrary(string libraryName);
        object ResolveHmiSoftwareOrThrow(string hmiSoftwarePath);

        SoftwareContainer? ResolveSoftwareContainerUncached(string softwarePath);
        T RequireHardwareUtility<T>(string identifier) where T : HardwareUtility;
        object ExactSiVArcRoot(string category);
        MasterCopy ExactMasterCopy(string libraryName, string path);
        LibraryType ExactLibraryType(object library, string typePath);
        LibraryTypeVersion ExactTypeVersion(LibraryType type, string version);
        JsonNode? MultilingualJson(MultilingualText? text);
        PlcUnitProvider RequireUnitProvider(PlcSoftware plc);
        PlcUnitBase ExactUnit(PlcUnitSystemGroup group, string unitKind, string name);
        PlcUnitBase? OptionalUnit(PlcSoftware plc, string unitName, string unitKind);
        PlcBlockGroup BlockRootOf(PlcSoftware plc, PlcUnitBase? unit);
        PlcTypeGroup TypeRootOf(PlcSoftware plc, PlcUnitBase? unit);
        object ExactObjectUnder(object root, string objectPath, string collection, string label);
        JsonArray DocumentMessages(DocumentResultMessageComposition? messages);
        JsonObject DocumentExportRow(DocumentExportResult result);
        JsonObject DocumentImportRow(DocumentImportResult result, JsonArray imported);

        DeviceItem ExactDriveItem(string devicePathJson, string itemPathJson);
        DriveObjectContainer ExactDriveContainer(DeviceItem item);
        DriveObject ExactDriveObject(string devicePathJson, string itemPathJson, ushort driveObjectNumber, int driveObjectIndex);
        JsonObject? DccContainerSummary(DriveObject drive);
        JsonObject DcbLibraryRow(DcbLibrary library, bool types);
        JsonObject AddressRow(Address address);
        object ExactTechnology(string softwarePath, string objectPath, bool writing);
        JsonObject InterfaceRow(object iface);
        object ExactMasterCopyPlcSource(string softwarePath, string sourcePath, bool block);
        void GetBlocksRecursive(PlcBlockGroup group, List<PlcBlock> list, string regexName = "");
        JsonArray HardwareOwnerPath(object? start);
        T RequireHardwareService<T>(HardwareObject owner, string parameter) where T : class, IEngineeringService;
        JsonObject DynamicAttributes(IEngineeringObject target, string[] names);
        void SetDynamicAttributes(IEngineeringObject target, JsonObject attributes, JsonObject meta);
        object? FindOnFresh(Func<object> composition, string name, JsonObject meta, string phase);
        int? CountOnFresh(Func<object> composition, JsonObject meta, string key);
        UmacConfigurator RequireUmac();
        object? FindOrdinal(object collection, string name, string kind);
        Role ExactUmacRole(UmacConfigurator umac, string name);
        JsonObject UmacRow(object item);
        JsonObject LibraryRef(object library);
        IEnumerable<Device> EnumerateGroupDevices(DeviceUserGroupComposition? groups);
        Microsoft.Extensions.Logging.ILogger? Logger { get; }
        int PortalMajorVersion { get; }
        string AvailablePlcPathsSuffix();
        System.Collections.Generic.List<string>? GetPlcTagTables(string softwarePath, out Portal.TagTableWalkDiagnostics diagnostics);
        bool ExportPlcTagTable(string softwarePath, string tagTableName, string exportPath, out string? error);
        void ImportPlcTagTable(string softwarePath, string folderPath, string importPath);
        object? TryInvokeMethodByName(object target, string methodName, params object?[] args);
        bool TryExportEngineeringObject(object engineeringObject, string exportPath, out string? error);
        System.Collections.Generic.List<string>? TryListNamesFromCollection(object root, string[] propHints, string kind);
        object? TryFindByNameInCollection(object root, string[] propHints, string name);
        string MakeSafeFileName(string name);
        string? TryGetName(object? value);
        System.Collections.Generic.IEnumerable<ObjectMember> DescribeMembers(object value, int maxMembers);
        Type? FindTypeBySuffix(string typeSuffix);
        object? TryGetService(object target, Type serviceType);

        System.Collections.Generic.IEqualityComparer<object> ReferenceEqualityComparer { get; }
        string FormatExceptionDetail(Exception exception);
        DeviceItem ExactDeviceItem(string[] devicePath, string[] itemPath);
        object EngineeringLibraryFolder(object library, string folderPath, string rootProperty);
        string LibraryPathOf(object node, string rootTypeName);
        string OptionPackageLibraryTypeKind(LibraryType type);
        Device ExactEngineeringDevice(string pathJson);
        object ResolveHmiScreenOrThrow(string hmiSoftwarePath, string screenName);
        string[] ExactNameList(string json);
        Portal.SivarcFamily GetSivarcFamily(string category);
        global::Siemens.Engineering.SiVArc.Sivarc RequireSivarc();
        Exception SivarcShape(object node);
        JsonObject DescribeNode(object target, int depth);
        PlcTag ExactPlcTag(string softwarePath, string tagPath);
        HmiTarget ExactClassicHmi(string softwarePath);
        VBScriptFolder ExactVbScriptFolder(HmiTarget hmi, IEnumerable<string> folderPath);
        JsonObject TechnologyObjectRow(TechnologicalInstanceDB db);
        JsonObject TypedMotionView(TechnologicalInstanceDB to);
        JsonObject ParameterRow(TechnologicalParameter parameter);
        JsonObject MappingRow(object mapping);
        bool DisconnectTyped(object iface);
        bool? IsConnectedTyped(object iface);
        Channel ExactChannel(DeviceItem item, string channelType, string channelIoType, int channelNumber);
        bool ConnectTyped(object iface, MotionProDiagClassicHmiLogic.ConnectionTarget target, string softwarePath, ConnectOption option);
        void ImportTechnologyObject(string softwarePath, string folderPath, string importPath, bool overwrite, List<string> importedNames);
        bool TrySetProperty(object target, string propName, object? value);
        string GetProjectTree();
        List<Device> GetDevices(string regexName = "");
        Device? GetDevice(string devicePath);
        DeviceItem? GetDeviceItem(string deviceItemPath);
        string GetDeviceItemTree(string deviceItemPath, int maxDepth = 4);
        Device? GetDeviceByPath(string devicePath);
        IEnumerable<object> FindHardwareCatalogEntries(object catalog, string filter);
        bool? IsAttributeWritable(object attributeInfo);
        object CoerceAttributeValue(string value, object? oldValue, object attributeInfo);
        JsonArray BuildDeviceItemNetworkReadbackJson(string deviceItemPath);
        IEnumerable<Device> EnumerateAllDevices();

        string[] GetPlcSoftwareNamesForDesktop();
        ResponseMessage ValidateAutomationContext(string expectedPlcSoftwarePath, string expectedHmiSoftwarePath);
        JsonArray ToJsonArray(IEnumerable<string> values);
        DeviceItem RequireDeviceItem(HardwareObject owner, string parameter);
        void ApplyScalarsAndAttributes(object target, string propertiesJson, string attributesJson, JsonObject meta, bool write);
        IEngineeringServiceProvider ServiceProvider(HardwareObject owner);
        JsonNode? LinkedTagRows(Channel channel, JsonObject row);
        System.Collections.Generic.List<PlcSoftware> GetAllPlcSoftware();
        System.Collections.Generic.List<(string Path, bool? Consistent)> ReadPlcConsistency(string softwarePath);
        bool RecoverableAuditError(Exception ex);
        string ReadReflectedString(object? owner, string propertyName);
        System.Collections.Generic.List<object?> EnumerateReflectedProperty(object? owner, string propertyName);

        string? LastExportedFile { get; }
        char[] RegexChars { get; }
        global::Siemens.Engineering.Compiler.CompilerResult CompileSoftware(string softwarePath, string password = "");
        PlcBlock? ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath = false);
        (string TempDir, List<string> Paths)? ExportBlockToTemp(string softwarePath, string blockPath, bool preservePath = false);
        PlcBlock? GetBlock(string softwarePath, string blockPath);
        PlcBlockGroup? GetBlockRootGroup(string softwarePath);
        List<PlcBlock>? GetBlocks(string softwarePath, string regexName = "");
        bool ImportBlock(string softwarePath, string groupPath, string importPath);
        ResponseImportBatch ImportBlocksFromDirectory(string softwarePath, string groupPath, string dir, string regexName = "", bool overwrite = true);
        void ImportTechnologyObject(string softwarePath, string folderPath, string importPath);
        bool ImportType(string softwarePath, string groupPath, string importPath);
        string GetBlockPath(PlcBlock block);
        PlcType? GetType(string softwarePath, string typePath);
        List<PlcType>? GetTypes(string softwarePath, string regexName = "");
        string GetPlcBlockGroupPath(PlcBlockGroup group);
        string GetPlcTypeGroupPath(PlcTypeGroup group);
        PlcBlockGroup? GetPlcBlockGroupByPath(string softwarePath, string groupPath);
        List<CrossReferenceEntry>? GetCrossReferences(string softwarePath, string objectPath, string objectKind, string filter, out string? reason, out bool queried);
        List<CrossReferenceEntry> TryFlattenCrossReferenceResult(object crossReferenceResult, string sourcePathFallback);
        JsonObject GetBindingIdentity();

        object? ResolvePlcTagTableGroup(object plc);
        string? CrossReferenceRefusal(string softwarePath);

        PlcType? ExportType(string softwarePath, string typePath, string exportPath, bool preservePath = false);
        ResponseImportBatch ImportHmiScreensFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true);
        ResponseImportBatch ImportHmiTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true);
        List<CrossReferenceEntry>? GetCrossReferences(string softwarePath, string objectPath, string objectKind, string filter, out string? reason, out bool queried, string unitName, string unitKind);
        (string TempDir, string XmlPath) ExportBlockDocumentForAnalysis(string softwarePath, string blockPath);
        ResponseMessage ManageProjectLanguage(string action = "read", string culture = "", bool dryRun = true);
        ResponseMessage RetrieveProjectArchive(string archivePath, string destinationDirectory, bool upgrade = false, bool dryRun = true);
        ResponseMessage ExportProjectTexts(string filePath, string sourceCulture, string targetCulture, bool dryRun = true);
        ResponseMessage ImportProjectTexts(string filePath, bool updateSourceLanguage, bool dryRun = true);

        // Adopt a retrieved project; preserve the caller's null-result diagnostic before binding it.
        void AdoptProject(ProjectBase? project, string missingProjectMessage);
        // Release handles after native CloseAndCommit has already closed the local session.
        void ReleaseProject();

        string? PlcLookupPathsSuffix { get; }
        PlcSoftware ResolvePlcForListing(string path);
        string GetTreePrefix(List<bool> ancestorStates, bool isLast);
        object? ResolveObject(string objectKind, string objectPath, string softwarePath, PlcAccess access = PlcAccess.Read);
        void DenyCrossReferenceReflection(string? service, string? method);
        object? CoerceReflectionValue(object? value, Type targetType);
        void ValidateUnitKind(string kind);
        IEnumerable<(string Name, string Kind, PlcUnitBase? Unit)> PlcScopes(PlcSoftware plc);
        IEnumerable<(string Path, object Value)> ScopedObjects(object group, string collection, string prefix = "", int depth = 0);
        List<ModelContextProtocol.NetworkAttribute>? GetDeviceItemNetworkInfo(string deviceItemPath);
        string ProbeConnectDeviceNodesToSubnet(string plcRootPath, string hmiRootPath, string subnetName);
        JsonArray BuildDeviceNodesJson(Device device);
        string NormalizeAttrName(string? n);
        IEnumerable<(DeviceItem Item, string Path)> TraverseDeviceItems(DeviceItem root, string path);
        IEnumerable<Portal.NetworkNodeInfo> FindNetworkNodes(DeviceItem root);
        bool IsIndustrialEthernetNode(object? node);
        string FormatNodeInfo(Portal.NetworkNodeInfo info);
        IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> BuildHardwareHmiConnectionCandidates(Portal.NetworkNodeInfo plcNode, Portal.NetworkNodeInfo hmiNode);
        IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> BuildDirectHardwareHmiConnectionCandidates(Portal.NetworkNodeInfo plcNode, Portal.NetworkNodeInfo hmiNode);
        IEnumerable<string> TryReadInterestingAttributes(object target);
        JsonObject GetPutGetAccess(string devicePath);
        (DeviceItem? item, string? attrName) FindPutGetAttribute(Device device);
        bool AttrValueIsEnabled(object? value);
        object? FindExistingByName(object compositionOrEnumerable, string name);
    }
}
