using Siemens.Engineering.CrossReference;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Native.Plc;

namespace Siemens.Engineering
{
    public interface IEngineeringServiceProvider { public T GetService<T>() where T : class; }
}

namespace Siemens.Engineering.CrossReference
{
    public class ServiceProvider : IEngineeringServiceProvider
    {
        public CrossReferenceService Service { get; } = new();
        public T GetService<T>() where T : class => (T)(object)Service;
    }

    public enum CrossReferenceFilter { AllObjects, ObjectsWithReferences, UnusedObjects }
    public sealed class CrossReferenceService
    {
        public List<CrossReferenceFilter> Queries { get; } = new();
        public CrossReferenceResult GetCrossReferences(CrossReferenceFilter filter)
        {
            Queries.Add(filter);
            return new CrossReferenceResult();
        }
    }
    public sealed class CrossReferenceResult { public SourceObject[] Sources => [new()]; }
    public class ObjectInfo
    {
        public string Name { get; set; } = "";
        public string? Path => null;
        public string TypeName => "FC";
        public string Address => "";
        public string Device => "PLC";
    }
    public sealed class SourceObject : ObjectInfo
    {
        public ReferenceObject[] References => [new() { Name = "Caller" }];
        public SourceObject[] Children => [];
    }
    public sealed class ReferenceObject : ObjectInfo { public Location[] Locations => []; }
    public sealed class Location : ObjectInfo
    {
        public string ReferenceLocation => "";
        public string ReferenceType => "Block";
        public string Access => "Call";
        public string ReferencedAsName => Name;
    }
}

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class InvocationJournal { internal static T Native<T>(string site, Func<T> read) => read(); }
}

namespace TiaMcp.Adapters.Native.Plc
{
    public partial interface IPlcOrganisationSession
    {
        public PlcSoftware? GetPlcSoftware(string path);
        public List<(string Path, bool? Consistent)> ReadPlcConsistency(string path);
        public bool RecoverableAuditError(Exception error);
        public object? OptionalUnit(PlcSoftware plc, string unitName, string unitKind);
        public PlcBlockGroup BlockRootOf(PlcSoftware plc, object? unit);
        public PlcTypeGroup TypeRootOf(PlcSoftware plc, object? unit);
        public object ExactObjectUnder(object root, string path, string collection, string label);
    }
    internal static partial class PlcOrganisationPrimitives
    {
        internal static object Tags(Siemens.Engineering.SW.Tags.PlcTagTable table) => table.Tags;
        internal static object SystemConstants(Siemens.Engineering.SW.Tags.PlcTagTable table) => table.SystemConstants;
        internal static CrossReferenceService CrossReferences(Siemens.Engineering.IEngineeringServiceProvider target)
            => target.GetService<CrossReferenceService>();
        internal static CrossReferenceResult CrossReferences(CrossReferenceService service, CrossReferenceFilter filter)
            => service.GetCrossReferences(filter);
        internal static object TagTableGroup(object unit) => throw new NotSupportedException();
    }
}

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcCrossReferences BlockReferences => new(new OrganisationSession(this));
        private sealed partial class OrganisationSession
        {
            public PlcSoftware? GetPlcSoftware(string path) => ResolvePlc(path, false);
            public List<(string Path, bool? Consistent)> ReadPlcConsistency(string path)
            {
                var blocks = new List<PlcBlock>();
                GetBlocksRecursive(GetPlcSoftware(path)!.BlockGroup, blocks);
                return blocks.Select(block => (GetBlockPath(block), (bool?)block.IsConsistent)).ToList();
            }
            public bool RecoverableAuditError(Exception error) => error is InvalidOperationException;
            public object? OptionalUnit(PlcSoftware plc, string unitName, string unitKind)
                => unitName == "" ? null : throw new NotSupportedException();
            public PlcBlockGroup BlockRootOf(PlcSoftware plc, object? unit) => plc.BlockGroup;
            public PlcTypeGroup TypeRootOf(PlcSoftware plc, object? unit) => plc.TypeGroup;
            public object ExactObjectUnder(object root, string path, string collection, string label)
                => root == engine.Plc.BlockGroup && collection == "Blocks" ? GetBlockForRead("PLC", path) : throw new NotSupportedException();
        }
    }
}
