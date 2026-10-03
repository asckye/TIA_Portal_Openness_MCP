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
