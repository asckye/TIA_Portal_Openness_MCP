namespace TiaMcp.Adapters.Contracts;

public static class OnlineCapabilities
{
    public static bool IsReadOnly(OnlineRequest request)
        => request.Operation is "GetOnlineState" or "CompareSoftwareToOnline" or "CheckDownloadReadiness"
            or "ListTransferRoutes" or "ScanAccessibleDevices" or "GetPlcBlockFingerprints"
            || request.Operation == "ManagePlcDataBlockSnapshot" && request.Action == "read";

    public static string? Unsupported(string release, OnlineRequest request)
    {
        if (!PortedFamilies.Available(release, request.Operation))
            return request.Operation + " has no supported API on release " + release + ".";
        if (request.RhTarget.Length > 0 && release == "14sp1")
            return "RHOnlineProvider/RHDownloadProvider require V15.1 or later.";
        if ((request.UserName.Length > 0 || request.UserType.Length > 0) && release != "19" && release != "20" && release != "21")
            return "OnlineAuthenticationConfiguration requires V19 or later.";
        if (release == "14sp1" && (request.IpAddress.Length > 0 || request.TargetIpAddress.Length > 0))
            return "IP address routes require V15.1 or later.";
        if (request.Operation == "ConnectOnlinePlc" && (release == "14sp1" || release == "15.1" || release == "16") && request.Password.Length > 0)
            return "Online password legitimation requires V17 or later.";
        if (request.Operation == "ManagePlcDataBlockSnapshot" && request.Action != "read"
            && request.Action != "exportSnapshot" && release != "21")
            return "ValueService is available in V21 only; snapshot read/export use InterfaceSnapshot (15.1+).";
        if (request.Operation == "DownloadPlc" && request.DownloadScope == "softwareOnlyChanges" && release is "14sp1" or "15.1" or "16")
            return "DownloadOptions.SoftwareOnlyChanges requires V17 or later; use downloadScope=software.";
        return null;
    }

    // DownloadPlc downloadScope values, mapped to Siemens.Engineering.Download.DownloadOptions.Software ("Download software all")
    // and DownloadOptions.SoftwareOnlyChanges ("Download software only changes", V17+).
    public static readonly string[] DownloadScopes = { "software", "softwareOnlyChanges" };
}
