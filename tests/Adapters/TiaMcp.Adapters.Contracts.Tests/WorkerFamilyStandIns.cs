using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace Siemens.Engineering
{
    public sealed class EngineeringException : Exception
    {
        public EngineeringException(string message) : base(message) { }
        public ExceptionMessageData MessageData { get; } = new();
        public ExceptionMessageData[] DetailMessageData { get; } = Array.Empty<ExceptionMessageData>();
    }
    public sealed class ExceptionMessageData
    {
        public string Text { get; set; } = "native message";
        public string DetailText { get; set; } = "native detail";
    }
}
namespace Siemens.Engineering.SW { public sealed class PlcSoftware { } }
namespace Siemens.Engineering.SW.Blocks { public sealed class PlcBlockGroup { } }
namespace Siemens.Engineering.HW { public sealed class DeviceItem { } }
namespace Siemens.Engineering.Online
{
    public enum OnlineState { Offline, Online, Unknown }
    public sealed class OnlineProvider { public OnlineState State { get; set; } }
}
namespace TiaMcpServer.ModelContextProtocol
{
    public static class ResponseClock { public static DateTime Now => new(2026, 10, 14, 12, 0, 0); }
}
namespace TiaMcp.Adapters.Native.Plc { }
namespace TiaMcp.Adapters.Native.Hmi
{
    public static class HmiReadSafety
    {
        public static bool ConnectionUnavailable(Exception error) => HmiReadPolicy.ConnectionUnavailable(error);
    }
    public static class HmiValues
    {
        public static object? DeepClone(object? value)
        {
            if (value is Dictionary<string, object?> fields) return fields.ToDictionary(pair => pair.Key, pair => DeepClone(pair.Value));
            if (value is IList list) return list.Cast<object?>().Select(DeepClone).ToList();
            return value;
        }
    }
    public static class HmiReadTrace
    {
        public static readonly List<(string Path, string Row)> Lines = new();
        public static void WriteLine(string path, string row) => Lines.Add((path, row));
    }
    public static class HmiExactAccess
    {
        public static object Screen(object software, string screen) => (software, screen);
    }
    public static class UnifiedScriptAccess
    {
        public static object Resolve(Func<string, object> resolve, string path, string software, bool collection)
            => (resolve(software), path, collection);
    }
}
namespace TiaMcp.Adapters
{
    public static class HardwareAmlPolicy
    {
        public static string? LastPath { get; private set; }
        public static void RequireExportTarget(string path) => LastPath = path;
    }
    public sealed partial class PlcFoundationEngine
    {
        public Func<string, Func<Dictionary<string, object?>, string>, HardwareAddressingReply>? EngineeringStep { get; set; }
        public Func<string, bool, Func<Dictionary<string, object?>, string>, HardwareAddressingReply>? EngineeringServicesStep { get; set; }
        public Func<string, string, object?>? ResolveEngineeringBlock { get; set; }
        public Func<string, object?>? ResolveEngineeringDevice { get; set; }
        public Func<string, object?>? ResolveEngineeringItem { get; set; }
        public Func<string, object?>? ResolveEngineeringSoftwareContainer { get; set; }
        public Func<string, string, object>? ResolveEngineeringHmiScreen { get; set; }
        public Func<string, object>? ResolveEngineeringLibraryDevice { get; set; }
        public Func<string, Siemens.Engineering.HW.DeviceItem?>? ResolveUnifiedHmiDeviceItem { get; set; }
        public Func<string, string, bool, object?>? ResolveEngineeringUnifiedScripts { get; set; }
        public Func<Exception, string>? FormatEngineeringException { get; set; }
        public Func<Exception, PlcSoftwareFailure>? EngineeringReflectionFailure { get; set; }
        public Func<object, object?>? RenderEngineeringValue { get; set; }
        public Func<object, object?>? RenderEngineeringReflectionValue { get; set; }
        public Func<string, Dictionary<string, object?>, string>? HmiContentToken { get; set; }
        public Func<List<Siemens.Engineering.SW.PlcSoftware>>? OnlinePlcs { get; set; }
        public Action<int, Exception?, string, object?[]>? OnlineLog { get; set; }
        public Func<string, Func<bool>, bool>? PlcB5AutoOffline { get; set; }
        public Func<object?, string>? PlcB5ResolvedPath { get; set; }
        public Func<string, object>? PlcB5DocumentPreflight { get; set; }
        public Func<object, string>? EngineeringLibraryOptionKind { get; set; }
        public Func<object, string>? EngineeringBlockGroupPath { get; set; }
        public Action<string?>? EngineeringRecordExportPath { get; set; }
        public Action<string>? AdmitHardwareAmlExport { get; set; }
        public Action<string?>? UpdateEngineeringReflectionLookupPaths { get; set; }
        public Func<string?>? EngineeringReflectionLookupPaths { get; set; }
        private string? reflectionLookupPaths;
        private string _hmiTokenGeneration = "fixture-start";
        private OrganisationSession? EngineeringPlcOrganisationSession;
        private OrganisationSession EngineeringPlcSession => EngineeringPlcOrganisationSession ?? new OrganisationSession(this);
        public readonly List<string> FamilyCalls = new();
        public readonly object FamilyObject = new();
        public readonly Siemens.Engineering.SW.PlcSoftware FamilyPlc = new();
        public readonly Siemens.Engineering.HW.DeviceItem FamilyItem = new();
        public readonly Siemens.Engineering.Online.OnlineProvider FamilyProvider = new();
        public bool MissingProvider { get; set; }
        public Exception? FamilyReadError { get; set; }
        public int ExportFailures { get; private set; }
        public bool OrganisationSessionConfigured => EngineeringPlcOrganisationSession != null;
        public void ReleaseFamilyState() => _resetWorkerFamilyState?.Invoke();
        private object HardwareLegacyDevice(string path) { FamilyCalls.Add("device:" + path); return FamilyObject; }
        private Siemens.Engineering.HW.DeviceItem HardwareLegacyItem(string path) { FamilyCalls.Add("item:" + path); return FamilyItem; }
        private static object DefaultLibraryValue(object value) => value;
        private static object DefaultHmiSoftware(OrganisationSession session, string path) => session.GetSoftwareContainer(path).Software;
        private string DefaultUnifiedHmiToken(string scope, Dictionary<string, object?> content)
            => HmiContentTokenPolicy.Compute(_hmiTokenGeneration, scope, content, EngineeringPlcSession.SerializeEvidence);
        private string DefaultB5ContainerPath(object? value) => value == FamilyObject ? "exact/path" : "";
        public static string OptionalLibraryKind(object value) => "option:" + value;
        private IEnumerable<(string Path, Siemens.Engineering.SW.PlcSoftware Value)> Plcs()
        { FamilyCalls.Add("plcs"); return new[] { ("PLC", FamilyPlc) }; }
        private sealed class SoftwareContainer
        {
            public object Software { get; }
            public SoftwareContainer(object software) => Software = software;
        }
        private sealed class OrganisationSession
        {
            private readonly PlcFoundationEngine _engine;
            public OrganisationSession(PlcFoundationEngine engine) => _engine = engine;
            public object GetBlock(string software, string block)
            { _engine.FamilyCalls.Add("block:" + software + ":" + block); return _engine.FamilyObject; }
            public SoftwareContainer ReadContainer(string path)
            {
                _engine.FamilyCalls.Add("container:" + path);
                if (_engine.FamilyReadError != null) throw _engine.FamilyReadError;
                return new SoftwareContainer(_engine.FamilyObject);
            }
            public SoftwareContainer GetSoftwareContainer(string path) => (SoftwareContainer)_engine.ResolveEngineeringSoftwareContainer!(path)!;
            public object ExactEngineeringHardware(string[] devices, string[] items)
            { _engine.FamilyCalls.Add("hardware:" + string.Join("/", devices) + ":" + string.Join("/", items)); return _engine.FamilyObject; }
            public string SerializeEvidence(Dictionary<string, object?> evidence) => JsonSerializer.Serialize(evidence);
            public bool IsProjectNull() => _engine.EngineeringProjectMissing!();
            public void RecordExportFailure(Exception error) => _engine.ExportFailures++;
            public void LogEngineeringFailure(Exception error, string message) => _engine.FamilyCalls.Add("log:" + message);
            public string GetPlcBlockGroupPath(Siemens.Engineering.SW.Blocks.PlcBlockGroup group) => "Folder/Subfolder";
            public Siemens.Engineering.SW.PlcSoftware ExactPlcForEngineering(string path, bool writing)
            { _engine.FamilyCalls.Add("plc:" + path + ":" + writing); return _engine.FamilyPlc; }
            public T? ResolvePlcService<T>(string path, Siemens.Engineering.SW.PlcSoftware software) where T : class
            { _engine.FamilyCalls.Add("service:" + path); return _engine.MissingProvider ? null : _engine.FamilyProvider as T; }
        }
    }
}
