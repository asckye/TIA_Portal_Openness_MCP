using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class HmiArray : List<object?> { public HmiArray() { } public HmiArray(object[] rows) : base(rows) { } }
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
    internal sealed class ConfigurationAddress : IConfiguration
    {
        public string Address { get; set; } = "";
        public string Name => Address;
    }
    internal sealed class AddressComposition : List<ConfigurationAddress>
    {
        internal bool FindUnavailable;
        public ConfigurationAddress? Find(string ip) => FindUnavailable ? throw new IOException("Address lookup unavailable") :
            this.FirstOrDefault(a => a.Address == ip);
        public ConfigurationAddress Create(string ip)
        {
            var address = new ConfigurationAddress { Address = ip };
            Add(address);
            return address;
        }
    }
    internal sealed class ConfigurationTargetInterface : IConfiguration
    {
        public string Name => "X1";
        public AddressComposition Addresses { get; } = new();
        public ConfigurationPcInterface Parent { get; }
        internal ConfigurationTargetInterface(ConfigurationPcInterface pc) => Parent = pc;
    }
    internal sealed class ConfigurationGateway
    {
        public string Name => "gateway";
        public AddressComposition Addresses { get; } = new();
    }
    internal sealed class ConfigurationSubnet
    {
        public string Name => "subnet";
        public AddressComposition Addresses { get; } = new();
        public List<ConfigurationGateway> Gateways { get; } = new();
    }
    internal sealed class ConfigurationPcInterface
    {
        public string Name { get; }
        public int Number => 1;
        public AddressComposition Addresses { get; } = new();
        public List<ConfigurationSubnet> Subnets { get; } = new();
        public List<ConfigurationTargetInterface> TargetInterfaces { get; } = new();
        internal ConfigurationPcInterface(string name)
        {
            Name = name;
            TargetInterfaces.Add(new(this));
        }
    }
    internal sealed class ConfigurationMode
    {
        public string Name => "PN/IE";
        public List<ConfigurationPcInterface> PcInterfaces { get; } = new() { new("LAN") };
    }
    internal sealed class ConnectionConfiguration : IConfiguration
    {
        public List<ConfigurationMode> Modes { get; } = new() { new() };
        internal Func<bool> Apply = () => true;
        public bool ApplyConfiguration(ConfigurationAddress address) => Apply();
        public bool ApplyConfiguration(ConfigurationTargetInterface target) => Apply();
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
        internal DownloadResult DownloadToPrimary(IConfiguration config, ConfigurationAddress address, DownloadConfigurationDelegate pre,
            DownloadConfigurationDelegate post, int options) => DownloadToPrimary(config, pre, post, options);
        internal DownloadResult DownloadToBackup(IConfiguration config, ConfigurationAddress address, DownloadConfigurationDelegate pre,
            DownloadConfigurationDelegate post, int options) => DownloadToBackup(config, pre, post, options);
        internal DownloadResult DownloadToPrimary(IConfiguration config, DownloadConfigurationDelegate pre, DownloadConfigurationDelegate post, int options)
            => throw new NotSupportedException();
        internal DownloadResult DownloadToBackup(IConfiguration config, DownloadConfigurationDelegate pre, DownloadConfigurationDelegate post, int options)
            => throw new NotSupportedException();
    }
    internal sealed class DownloadProvider
    {
        internal readonly ConnectionConfiguration Configuration = new();
        internal int Calls;
        internal DownloadResult Download(IConfiguration config, ConfigurationAddress address, DownloadConfigurationDelegate pre,
            DownloadConfigurationDelegate post, int options) => Download(config, pre, post, options);
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
        internal string ReadReflectedString(object? owner, string name) => owner?.GetType().GetProperty(name)?.GetValue(owner)?.ToString() ?? "";
        internal IEnumerable<object?> EnumerateReflectedProperty(object? owner, string name)
            => (owner?.GetType().GetProperty(name)?.GetValue(owner) as System.Collections.IEnumerable)?.Cast<object?>() ?? Array.Empty<object?>();
        internal HmiMessage RunHmiStepTool(string name, Func<HmiObject, string> action) => throw new NotSupportedException();
        internal bool CanConnectOnline => true;
        internal PlcSoftware? GetPlcSoftware(string path) => ResolvePlc(path, false);
        internal PlcSoftware[] GetAllPlcSoftware() => Array.Empty<PlcSoftware>();
        internal bool IsProjectNull() => false;
        internal PlcSoftware? ResolvePlc(string path, bool writing) => Failure == "missing" ? null : new();
        internal T? ResolvePlcService<T>(string path, PlcSoftware plc) where T : class => Failure == "provider" ? null :
            typeof(T) == typeof(OnlineProvider) ? Online as T
            : typeof(T) == typeof(RHOnlineProvider) ? (Failure == "no-rh" ? null : Redundant as T) : Download as T;
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
        private DownloadPromptPolicy BuildDownloadPromptPolicy(OnlineRequest request) => new();
        private void RequireDownloadOffline(string path, PlcSoftware plc, string rh) { }
        private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
        private IDisposable? AttachOnlineLegitimationHandler(ConnectionConfiguration cfg, string? password, HmiObject meta, bool trust, bool allow) => null;
        private static void ApplyDownloadPrompt(object config, DownloadPromptPolicy policy) { }
        private IDisposable? AttachOnlineLegitimationHandler(ConnectionConfiguration cfg, string? password, string? user, string? type,
            HmiObject meta, bool trust, bool allow) => null;
        private static int DownloadScopeOption(string scope) => 0;
        private static OnlineDownloadResult BuildDownloadResponse(DownloadResult result, string path, DownloadRouteSelection route,
            DownloadPromptPolicy policy, HmiObject meta) => new() { Ok = true };
    }
}

namespace Siemens.Engineering.Compiler { }
namespace Siemens.Engineering.HW.Features { }
namespace Siemens.Engineering.SW.Blocks { }
namespace Siemens.Engineering.Upload.Configurations { }
namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcGroupOperations
    {
        internal static IEnumerable<object> Items(object values) => ((System.Collections.IEnumerable)values).Cast<object>();
    }
}
