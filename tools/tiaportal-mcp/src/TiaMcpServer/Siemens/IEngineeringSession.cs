using System;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.SW;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
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
        T? ResolvePlcService<T>(string softwarePath, PlcSoftware plcSoftware) where T : class, IEngineeringService;
        DeviceItem? GetDeviceItemByPath(string deviceItemPath);
        PlcSoftware ExactPlcForEngineering(string softwarePath, bool writing);
        HardwareObject ExactEngineeringHardware(string devicePathJson, string itemPathJson);
        object ExactOpenEngineeringLibrary(string libraryName);
        object ResolveHmiSoftwareOrThrow(string hmiSoftwarePath);

        // Adopt a retrieved project; preserve the caller's null-result diagnostic before binding it.
        void AdoptProject(ProjectBase? project, string missingProjectMessage);
        // Release handles after native CloseAndCommit has already closed the local session.
        void ReleaseProject();
    }
}
