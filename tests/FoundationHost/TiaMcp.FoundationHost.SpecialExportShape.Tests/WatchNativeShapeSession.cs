using System.Collections.Generic;
using System.Text.Json;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Engineering;

namespace Siemens.Engineering.HW { }
namespace Siemens.Engineering.Hmi { }
namespace Siemens.Engineering.SW.Tags { }
namespace Siemens.Engineering.SW.TechnologicalObjects.Motion { }

namespace TiaOpenness.Shared
{
    internal static class NativeInputPolicy { internal static string FullPath(string path) => Path.GetFullPath(path); }
    internal static class NativeExportPolicy
    {
        internal static void RequireApply(string status) { if (status != "planned") throw new ArgumentException(status); }
        internal static void RequireSoftwarePath(string path, string? exact, bool allowAlias)
        { if (path != exact) throw new ArgumentException("Exact PLC path required."); }
    }
}

namespace TiaMcp.Adapters.Native.Engineering
{
    internal static class HmiReadPolicy { internal static bool ConnectionUnavailable(Exception error) => false; }
}

namespace TiaMcp.Adapters.Native.Plc
{
    internal static class PlcMotionPolicy { }
    internal static class PlcOrganisationPaths
    {
        internal static string[] Parse(string path, bool rootAllowed) => PlcObjectPath.Parts(path, rootAllowed);
    }
    internal static partial class PlcBlockPrimitives
    {
        internal static int Count(PlcWatchTableComposition tables) => tables.Count;
        internal static PlcForceTableComposition ForceTables(PlcWatchAndForceTableGroup group) => group.ForceTables;
    }

    internal sealed class WatchShapeSession
    {
        internal PlcSoftware Software { get; } = new();
        internal Dictionary<string, object?> LastMeta { get; private set; } = new();
        internal int ExactCalls { get; private set; }
        internal string ReleaseKey => "21";
        internal IEqualityComparer<object> ReferenceEqualityComparer => System.Collections.Generic.ReferenceEqualityComparer.Instance;
        internal PlcSoftware GetPlcSoftware(string path) => path == "PLC" ? Software : throw new ArgumentException("Unknown PLC.");
        internal string PlcSoftwarePath(PlcSoftware? software) => "PLC";
        internal string AvailablePlcPathsSuffix() => "PLC";
        internal PlcSoftware ExactPlcForEngineering(string path, bool writing) { ExactCalls++; return GetPlcSoftware(path); }
        internal IDisposable AcquireHmiEditAccess() => new Access();
        internal string BindingIdentity() => "shape-binding";
        internal string SerializeEvidence(Dictionary<string, object?> evidence) => JsonSerializer.Serialize(evidence);
        internal HardwareAddressingReply RunHmiStepTool(string tool, Func<Dictionary<string, object?>, string> action)
        {
            var meta = new Dictionary<string, object?> { ["tool"] = tool }; LastMeta = meta;
            string message = EngineeringWriteJournal.Run(meta, () => action(meta));
            return new HardwareAddressingReply { Message = message, Meta = meta };
        }
        internal object ExactObjectUnder(object root, string path, string composition, string kind)
        {
            var parts = PlcObjectPath.Parts(path);
            var group = PlcGroupOperations.Group(root, PlcObjectPath.Format(parts.Take(parts.Length - 1)),
                pathParts: value => PlcObjectPath.Parts(value, true));
            return PlcGroupOperations.Find(PlcGroupOperations.Get(group, composition), parts.Last())
                ?? throw new PlcSoftwareException("NotFound", kind);
        }
        private sealed class Access : IDisposable { public void Dispose() { } }
    }

    public sealed partial class PlcOrganisationAdapter
    {
        private readonly WatchShapeSession _session;
        internal PlcOrganisationAdapter(WatchShapeSession session) => _session = session;
        internal bool MutationStarted;
        private bool IsProjectNull() => false;
        private static object ResolvePlcWatchAndForceTableGroup(PlcSoftware software) => software.WatchAndForceTableGroup;
        private static object? WatchProperty(object target, params string[] names)
            => names.Select(name => target.GetType().GetProperty(name)?.GetValue(target)).FirstOrDefault(value => value != null);
        private static List<string> TryListNamesFromCollection(object target, string[] names, string kind) => throw new NotSupportedException();
        private static object? WatchFindByNameInCollection(object target, string[] names, string name) => throw new NotSupportedException();
        private static IEnumerable<(string Path, string Name, object Table)> EnumeratePlcWatchTables(object root)
        {
            var group = (PlcWatchAndForceTableGroup)root;
            foreach (var table in group.WatchTables)
                yield return (ObjectPathEncoding.EncodeSegment(table.Name), table.Name, table);
            foreach (var child in group.Groups)
                foreach (var table in EnumeratePlcWatchTables(child))
                    yield return (ObjectPathEncoding.EncodeSegment(child.Name) + "/" + table.Path, table.Name, table.Table);
        }
        private static Dictionary<string, object?> TechnologyExport(object target, string path, bool overwrite)
        {
            ((PlcWatchTable)target).Export(new FileInfo(path), Siemens.Engineering.ExportOptions.None);
            return new Dictionary<string, object?> { ["success"] = true };
        }
        private static void Page(object?[] values, int offset, int limit, Dictionary<string, object?> meta)
            => meta["items"] = values.Skip(offset).Take(limit).ToArray();
    }
}
