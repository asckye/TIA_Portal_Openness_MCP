using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using TiaMcp.Adapters.Native.Engineering;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;

namespace TiaMcp.Adapters.Native.Safety
{
    internal sealed class SafetySession
    {
        private readonly SoftwareContainer container;
        internal SafetySession(SafetyAdministration administration)
            => container = new SoftwareContainer { Parent = new DeviceItem { Administration = administration } };

        internal SoftwareContainer GetSoftwareContainer(string path) => container;
        internal bool IsProjectNull() => false;
        internal bool CompilerLoggingEnabled => false;
        internal void LogCompilerMessage(params object[] args) => throw new NotSupportedException();
        internal EngineeringObject? CurrentPortal => null;
        internal IDisposable AcquireHmiEditAccess() => new MemoryStream();
        internal PlcSoftware ExactPlcForEngineering(string path, bool offline) => container.Software;
        internal T? ResolvePlcService<T>(string path, PlcSoftware plc) where T : class => ((DeviceItem)container.Parent!).GetService<T>();
        internal HardwareAddressingReply RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
        {
            var meta = new HmiObject();
            return new HardwareAddressingReply { Message = TestWrite == null ? action(meta)
                : EngineeringWriteJournal.RunReported(meta, () => action(meta), error => error.Message), Meta = meta };
        }
        internal object ExactMasterCopyPlcSource(string path, string block, bool exact) => throw new NotSupportedException();
        internal void GetBlocksRecursive(object group, List<PlcBlock> blocks) => throw new NotSupportedException();
        internal string? TestWrite;
        internal (HmiObject Scalars, HmiObject Attributes) RuntimeGroupChanges()
            => (new HmiObject { ["Cycle"] = 2 }, new HmiObject { ["FOBCycleTime"] = 2 });
        internal (HmiObject Scalars, HmiObject Attributes, HmiObject BlockNumbers, string? SafetySystemVersion) SettingsChanges()
            => (new HmiObject { ["Cycle"] = 2 },
                TestWrite is "version" or "version-abort" ? new HmiObject() : new HmiObject { ["EnableFCommunicationIdTag"] = true },
                new HmiObject(), TestWrite is "version" or "version-abort" ? "V2" : null);
        internal List<KeyValuePair<string, object>> GlobalChanges() => throw new NotSupportedException();
    }
    internal static class SafetyScalars
    {
        internal static object? Json(object value) => value;
        internal static HmiObject Read(object value) => value is SafetySettings { SafetySystemVersion: not null }
            ? throw new InvalidOperationException("Settings readback failed.") : new HmiObject();
        internal static List<(PropertyInfo Property, object? Value)> Prepare(Type type, HmiObject values)
            => values.Select(entry => (type.GetProperty(entry.Key)!, entry.Value)).ToList();
        internal static void Apply(object target, List<(PropertyInfo Property, object? Value)> values, HmiObject meta)
        {
            foreach (var entry in values)
            {
                EngineeringWriteJournal.Property(target, entry.Property);
                entry.Property.SetValue(target, entry.Value);
            }
        }
    }
    internal static class SafetyFileOutput
    {
        internal static FileInfo Plan(string path) => throw new NotSupportedException();
        internal static object Verify(FileInfo file) => throw new NotSupportedException();
    }
}
namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class HmiReadSafety { internal static bool ConnectionUnavailable(Exception error) => HmiReadPolicy.ConnectionUnavailable(error); }
}
namespace TiaMcp.Adapters.Hardware { }
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
        private readonly Safety.SafetySession _session;
        internal bool MutationStarted { get; set; }
        internal PlcOrganisationAdapter(Safety.SafetySession session) => _session = session;
    }
    internal static class PlcGroupOperations { internal static IEnumerable<object> Items(IEnumerable values) => values.Cast<object>(); }
    internal static class PlcBlockPrimitives
    {
        internal static PlcBlock? Find(List<PlcBlock> blocks, string name) => blocks.Find(b => b.Name == name);
        internal static PlcSoftware? SoftwareOrNull(SoftwareContainer? value) => value?.Software;
        internal static object? ParentOrNull(IEngineeringObject? value) => (value as EngineeringObject)?.Parent;
        internal static SafetyAdministration? SafetyOrNull(DeviceItem? item) => item?.GetService<SafetyAdministration>();
        internal static bool IsLoggedOn(SafetyAdministration admin) => admin.IsLoggedOnToSafetyOfflineProgram;
        internal static void Login(SafetyAdministration admin, SecureString password) => admin.LoginToSafetyOfflineProgram(password);
        internal static ICompilable? Compiler(IEngineeringServiceProvider provider) => provider.GetService<ICompilable>();
        internal static CompilerResult Compile(ICompilable compiler) => compiler.Compile();
        internal static CompilerResultState State(CompilerResult value) => value.State;
        internal static CompilerResultMessageComposition Messages(CompilerResult value) => value.Messages;
        internal static int ErrorCount(CompilerResult value) => 0;
        internal static int WarningCount(CompilerResult value) => 0;
        internal static string Path(CompilerResultMessage message) => "";
        internal static string Description(CompilerResultMessage message) => "";
        internal static DateTime DateTime(CompilerResultMessage message) => default;
    }
    internal static class PlcUnitScope
    {
        internal static object? Optional(PlcSoftware plc, string unit) => unit == "" ? null : throw new NotSupportedException();
        internal static PlcBlockGroup Blocks(PlcSoftware plc, object? unit) => plc.BlockGroup;
    }
}
