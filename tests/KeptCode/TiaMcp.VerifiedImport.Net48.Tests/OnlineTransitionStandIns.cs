using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class HmiArray : List<object?> { }
    internal sealed class HmiObject : Dictionary<string, object?> { }
    internal static class HmiValues { internal static object? DeepClone(object? value) => value; }
    internal static class HmiMeta
    {
        internal static HmiObject Basic(bool success, params (string Key, object? Value)[] data)
        {
            var result = new HmiObject { ["success"] = success };
            foreach (var pair in data) result[pair.Key] = pair.Value;
            return result;
        }
    }
}
namespace Siemens.Engineering.Connection
{
    internal interface IConfiguration { }
    internal sealed class ConfigurationAddress : IConfiguration { }
    internal sealed class ConnectionConfiguration : IConfiguration
    {
        internal Func<bool> Apply = () => true;
        internal bool ApplyConfiguration(ConfigurationAddress address) => Apply();
    }
}
namespace Siemens.Engineering.Download.Configurations { }
namespace Siemens.Engineering.Download
{
    internal delegate void DownloadConfigurationDelegate(object config);
    internal sealed class DownloadResult { }
    internal sealed class RHDownloadProvider
    {
        internal readonly ConnectionConfiguration Configuration = new();
        internal DownloadResult DownloadToPrimary(IConfiguration config, DownloadConfigurationDelegate pre, DownloadConfigurationDelegate post, int options)
            => throw new NotSupportedException();
        internal DownloadResult DownloadToBackup(IConfiguration config, DownloadConfigurationDelegate pre, DownloadConfigurationDelegate post, int options)
            => throw new NotSupportedException();
    }
    internal sealed class DownloadProvider
    {
        internal readonly ConnectionConfiguration Configuration = new();
        internal int Calls;
        internal DownloadResult Download(IConfiguration config, DownloadConfigurationDelegate pre, DownloadConfigurationDelegate post, int options)
        { Calls++; throw new InvalidOperationException("Load interrupted"); }
    }
}
namespace Siemens.Engineering.Online
{
    internal enum OnlineState { Offline, Online, Connecting }
    internal sealed class RHOnlineProvider
    {
        internal readonly ConnectionConfiguration Configuration = new();
        internal OnlineState PrimaryState { get; set; }
        internal OnlineState BackupState { get; set; }
        internal string Issued = "";
        private OnlineState Refuse(string target) { Issued = target; throw new InvalidOperationException("R/H transition refused"); }
        internal OnlineState GoOnlineToPrimary() => Refuse("primary");
        internal OnlineState GoOnlineToBackup() => Refuse("backup");
    }
    internal sealed class OnlineProvider
    {
        internal string State => Read();
        internal readonly ConnectionConfiguration Configuration = new();
        internal Func<OnlineState> Connect = () => OnlineState.Online;
        internal OnlineState GoOnline() => Connect();
        internal Func<string> Read = () => "Online";
        internal Action Disconnect = () => throw new InvalidOperationException("Transition refused");
        internal void GoOffline() => Disconnect();
    }
}
namespace TiaMcp.Adapters.Native.Online
{
    internal sealed class OnlineSession
    {
        internal OnlineSession Log => this;
        internal OnlineSession CurrentProject => this;
        internal bool IsModified => false;
        internal string Failure = "";
        internal readonly OnlineProvider Online = new();
        internal readonly DownloadProvider Download = new();
        internal readonly RHOnlineProvider Redundant = new();
        internal void Information(string message, params object?[] args) { }
        internal void Error(Exception error, string message, string path) { }
        internal bool CanConnectOnline => true;
        internal PlcSoftware? GetPlcSoftware(string path) => ResolvePlc(path, false);
        internal PlcSoftware[] GetAllPlcSoftware() => Array.Empty<PlcSoftware>();
        internal bool IsProjectNull() => false;
        internal PlcSoftware? ResolvePlc(string path, bool writing) => Failure == "missing" ? null : new();
        internal T? ResolvePlcService<T>(string path, PlcSoftware plc) where T : class => Failure == "provider" ? null :
            typeof(T) == typeof(OnlineProvider) ? Online as T : typeof(T) == typeof(RHOnlineProvider) ? Redundant as T : Download as T;
        internal string AvailablePlcPathsSuffix() => "";
        internal List<(string Name, bool? Consistent)> ReadPlcConsistency(string path) => new() { ("Main", true) };
        internal bool RecoverableAuditError(Exception error) => false;
    }

    internal sealed partial class OnlineDownloadAdapter
    {
        internal OnlineSession FixtureSession => _session;
        internal OnlineDownloadAdapter() : this(new OnlineSession()) { }
        internal class DownloadPromptPolicy
        {
            internal HmiObject Summary() => new();
            internal string RefusedSummary() => "";
            internal string ReinitializationSummary() => "";
            internal string UnansweredSummary() => "";
        }
        internal class DownloadRouteSelection
        {
            internal string? Error { get; set; }
            internal object? Configuration { get; set; }
            internal bool WriteAttempted { get; set; }
            internal bool Unreadable { get; set; }
            internal bool Applied { get; set; }
            internal ConfigurationAddress? Address { get; set; }
            internal string Description => "PN/IE";
            internal List<string> Candidates { get; } = new();
        }
        private DownloadPromptPolicy BuildDownloadPromptPolicy(OnlineRequest request) => new();
        private void RequireDownloadOffline(string path, PlcSoftware plc, string rh) { }
        private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
        private IDisposable? AttachOnlineLegitimationHandler(ConnectionConfiguration cfg, string? password, HmiObject meta, bool trust, bool allow) => null;
        private static void ApplyDownloadPrompt(object config, DownloadPromptPolicy policy) { }
        private IDisposable? AttachOnlineLegitimationHandler(ConnectionConfiguration cfg, string? password, string? user, string? type,
            HmiObject meta, bool trust, bool allow) => null;
        private DownloadRouteSelection SelectDownloadRoute(ConnectionConfiguration cfg, string? iface, string? ip, bool deferApply = false) =>
            new() { Configuration = cfg, Address = new(), Applied = true, WriteAttempted = _session.Failure == "route-write",
                Error = _session.Failure is "route" or "route-write" ? "Missing route" : null };
        private ConfigurationAddress? FindConfigurationAddress(ConnectionConfiguration configuration, string ipAddress) => new();
        private static int DownloadScopeOption(string scope) => 0;
        private static string DescribeRoutes(List<string> routes) => string.Join(", ", routes);
        private static OnlineDownloadResult BuildDownloadResponse(DownloadResult result, string path, DownloadRouteSelection route,
            DownloadPromptPolicy policy, HmiObject meta) => new() { Ok = true };
    }
}
