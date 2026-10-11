using Microsoft.AspNetCore.Builder;

namespace TiaMcp.FoundationHost;

// The HTTP hosts read nothing from the ASP.NET content root (no appsettings, no static files). Pin it to the host's own folder and
// turn off configuration reload, so a host started from a large or changing working directory does not watch that directory.
// Both HTTP entry points use these options.
internal static class HttpHostOptions
{
    internal const string NoConfigurationReload = "--hostBuilder:reloadConfigOnChange=false";

    internal static WebApplicationOptions Create() => new()
    {
        Args = [NoConfigurationReload],
        ContentRootPath = AppContext.BaseDirectory,
    };
}
