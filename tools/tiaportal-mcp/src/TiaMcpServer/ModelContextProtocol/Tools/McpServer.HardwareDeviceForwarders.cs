using System;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        public static ResponseProjectTree GetProjectTree()
            => ((DevicesTools)EngineServices.Get(typeof(DevicesTools))).GetProjectTree();

        public static ResponseDeviceInfo GetDeviceInfo(
            string devicePath)
            => ((DevicesTools)EngineServices.Get(typeof(DevicesTools))).GetDeviceInfo(devicePath);

        public static ResponseMessage ValidateAutomationContext(
            string expectedPlcSoftwarePath = "PLC_1",
            string expectedHmiSoftwarePath = "HMI_RT_1")
            => ((DevicesTools)EngineServices.Get(typeof(DevicesTools))).ValidateAutomationContext(expectedPlcSoftwarePath, expectedHmiSoftwarePath);

        public static ResponseMessage AddDevice(
            string orderNumber,
            string version,
            string deviceName)
            => ((DevicesTools)EngineServices.Get(typeof(DevicesTools))).AddDevice(orderNumber, version, deviceName);

        public static ResponseDeviceProbe AddDeviceWithFallback(
            string preferredMlfb,
            string preferredVersion,
            string deviceName,
            string family = "S7-1500")
            => ((DevicesTools)EngineServices.Get(typeof(DevicesTools))).AddDeviceWithFallback(preferredMlfb, preferredVersion, deviceName, family);

        public static ResponseGsdDeviceSearch SearchInstalledGsdDevices(
            string keyword,
            int limit = 50)
            => ((DevicesTools)EngineServices.Get(typeof(DevicesTools))).SearchInstalledGsdDevices(keyword, limit);

        public static ResponseHardwareCatalogSearch SearchHardwareCatalog(
            string keyword,
            int limit = 50)
            => ((DevicesTools)EngineServices.Get(typeof(DevicesTools))).SearchHardwareCatalog(keyword, limit);

        public static ResponseHardwareCatalogDeviceProbe AddHardwareCatalogDeviceWithProbe(
            string keyword,
            string deviceName,
            string preferredText = "")
            => ((DevicesTools)EngineServices.Get(typeof(DevicesTools))).AddHardwareCatalogDeviceWithProbe(keyword, deviceName, preferredText);
    }
}
