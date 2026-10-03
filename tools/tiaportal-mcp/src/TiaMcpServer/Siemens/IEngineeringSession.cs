using System;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Utilities;
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

        // Adopt a retrieved project; preserve the caller's null-result diagnostic before binding it.
        void AdoptProject(ProjectBase? project, string missingProjectMessage);
        // Release handles after native CloseAndCommit has already closed the local session.
        void ReleaseProject();
    }
}
