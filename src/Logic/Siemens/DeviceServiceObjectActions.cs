namespace TiaMcpServer.Siemens
{
    // Shared action allowlists; native request validation retains the same arrays.
    internal static class DeviceServiceObjectActions
    {
        internal static readonly string[] ServiceObjectFamilies = { "webApplications", "telecontrolDataPoints", "certificateServices" };
        internal static readonly string[] WebApplicationActions = { "read", "setDefault" };
        internal static readonly string[] TelecontrolActions = { "read", "update", "delete", "export", "import" };
        internal static readonly string[] CertificateServiceActions = { "read", "update", "setServiceGroupName", "createService", "deleteService" };
    }
}
