using System;
using System.Collections.Generic;
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

        // Adopt a retrieved project; preserve the caller's null-result diagnostic before binding it.
        void AdoptProject(ProjectBase? project, string missingProjectMessage);
        // Release handles after native CloseAndCommit has already closed the local session.
        void ReleaseProject();
    }
}
