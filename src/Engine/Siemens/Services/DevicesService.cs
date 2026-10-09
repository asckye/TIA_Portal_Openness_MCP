using System;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class DevicesService
    {
        private readonly IEngineeringSession _session;
        private readonly HardwareDevicesService port;
        public DevicesService(IEngineeringSession session, HardwareDevicesService port) { _session = session; this.port = port; }
        internal bool HasProject => _session.CurrentProject is object;
        public HardwareDevicesService.FallbackProbe AddDeviceWithFallback(string preferredMlfb, string preferredVersion, string deviceName, string family)
            => port.AddDeviceWithFallback(preferredMlfb, preferredVersion, deviceName, family);
        public string GetProjectTree() => _session.GetProjectTree();

        public ResponseMessage ValidateAutomationContext(string expectedPlcSoftwarePath = "PLC_1", string expectedHmiSoftwarePath = "HMI_RT_1")
            => _session.ValidateAutomationContext(expectedPlcSoftwarePath, expectedHmiSoftwarePath);
    }
}
