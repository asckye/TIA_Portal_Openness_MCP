using System;
using System.Linq;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Tags;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
namespace TiaMcp.Adapters.Native.Plc
{
    public sealed class PlcCrossReferences
    {
        private readonly IPlcOrganisationSession _session;
        public PlcCrossReferences(IPlcOrganisationSession session) => _session = session;
#if PLC_SOFTWARE_CROSS_REFERENCES
        public List<PlcCrossReference>? GetCrossReferences(string softwarePath, string objectPath, string objectKind, string filter,
            out string? reason, out bool queried, string unitName, string unitKind)
        {
            queried = false;
            reason = PlcCrossReferencePolicy.PolicyRefusal(Environment.GetEnvironmentVariable(PlcCrossReferencePolicy.NativeQueryOptInVariable));
            if (reason != null) return null;
            if (string.IsNullOrWhiteSpace(filter)) filter = "AllObjects";
            if (!Enum.TryParse<global::Siemens.Engineering.CrossReference.CrossReferenceFilter>(filter, true, out var filterValue)
                || !Enum.IsDefined(typeof(global::Siemens.Engineering.CrossReference.CrossReferenceFilter), filterValue))
            { reason = "Invalid CrossReferenceFilter name: " + filter; return null; }
            if (_session.IsProjectNull()) { reason = "No project is open."; return null; }
            try
            {
                reason = CrossReferenceRefusal(softwarePath);
                if (reason != null) return null;
                var target = ExactCrossReferenceTarget(softwarePath, objectPath, objectKind, unitName, unitKind);
                var service = InvocationJournal.Native("CrossReference.GetService", () => target.GetService<global::Siemens.Engineering.CrossReference.CrossReferenceService>());
                if (service == null) { reason = "CrossReferenceService unavailable on this exact " + objectKind + ". No query was made."; return null; }
                queried = true;
                var result = InvocationJournal.Native("CrossReference.GetCrossReferences", () => PlcOrganisationPrimitives.CrossReferences(service, filterValue));
                return InvocationJournal.Native("CrossReference.readCompleteResult", () => TryFlattenCrossReferenceResult(result, objectPath));
            }
            catch (Exception ex) when (_session.RecoverableAuditError(ex))
            { reason = (queried ? "failed: result is incomplete; partial rows discarded. " : "notQueried: ") + ex.GetBaseException().Message; return null; }
        }

        public string? CrossReferenceRefusal(string softwarePath)
        {
            var policy = PlcCrossReferencePolicy.PolicyRefusal(Environment.GetEnvironmentVariable(PlcCrossReferencePolicy.NativeQueryOptInVariable));
            if (policy != null) return policy;
            try
            {
                var rows = _session.ReadPlcConsistency(softwarePath);
                if (rows.Count == 0) return "refused: no block/type consistency evidence; no native cross-reference query was made.";
                return PlcCrossReferencePolicy.Refusal(rows, softwarePath);
            }
            catch (Exception ex) when (_session.RecoverableAuditError(ex))
            { return "refused: root/unit consistency read incomplete: " + ex.GetBaseException().Message; }
        }

        public static List<PlcCrossReference> TryFlattenCrossReferenceResult(object crossReferenceResult, string sourcePathFallback)
        {
            if (!(crossReferenceResult is global::Siemens.Engineering.CrossReference.CrossReferenceResult result))
                throw new InvalidOperationException("Native query returned no supported result; completeness unknown.");
            return CrossReferenceTreeReader.Read<global::Siemens.Engineering.CrossReference.SourceObject,
                global::Siemens.Engineering.CrossReference.ReferenceObject, global::Siemens.Engineering.CrossReference.Location, PlcCrossReference>(
                PlcGroupOperations.Items(result.Sources).Cast<global::Siemens.Engineering.CrossReference.SourceObject>(),
                source => PlcGroupOperations.Items(source.References).Cast<global::Siemens.Engineering.CrossReference.ReferenceObject>(),
                reference => PlcGroupOperations.Items(reference.Locations).Cast<global::Siemens.Engineering.CrossReference.Location>(),
                source => PlcGroupOperations.Items(source.Children).Cast<global::Siemens.Engineering.CrossReference.SourceObject>(),
                (source, reference, location) => BuildPlcCrossReference(source, reference, location, sourcePathFallback));
        }

        private static PlcCrossReference BuildPlcCrossReference(global::Siemens.Engineering.CrossReference.SourceObject source,
            global::Siemens.Engineering.CrossReference.ReferenceObject reference, global::Siemens.Engineering.CrossReference.Location? location, string fallback)
            => new PlcCrossReference {
                SourceName = source.Name, SourcePath = source.Path ?? fallback, SourceTypeName = source.TypeName,
                SourceAddress = source.Address, SourceDevice = source.Device,
                ReferenceName = reference.Name, ReferencePath = reference.Path, ReferenceTypeName = reference.TypeName,
                ReferenceAddress = reference.Address, ReferenceDevice = reference.Device,
                LocationName = location?.Name, ReferenceLocation = location?.ReferenceLocation,
                ReferenceType = location?.ReferenceType.ToString(), Access = location?.Access.ToString()
            };

        private IEngineeringServiceProvider ExactCrossReferenceTarget(string softwarePath, string objectPath, string kind, string unitName, string unitKind)
        {
            if (unitKind != "unit" && unitKind != "safety") throw new ArgumentException("unitKind must be unit or safety.");
            var plc = _session.GetPlcSoftware(softwarePath) ?? throw new PlcSoftwareException("NotFound", "PLC not found: " + softwarePath + _session.AvailablePlcPathsSuffix());
            var unit = _session.OptionalUnit(plc, unitName, unitKind);
            if (kind.Equals("Block", StringComparison.OrdinalIgnoreCase)) return (PlcBlock)_session.ExactObjectUnder(_session.BlockRootOf(plc, unit), objectPath, "Blocks", "block");
            if (kind.Equals("Type", StringComparison.OrdinalIgnoreCase)) return (PlcType)_session.ExactObjectUnder(_session.TypeRootOf(plc, unit), objectPath, "Types", "type");
            if (!kind.Equals("Tag", StringComparison.OrdinalIgnoreCase) && !kind.Equals("SystemConstant", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("objectKind must be Block, Type, Tag or SystemConstant.");
            var parts = PlcGroupOperations.Parts(objectPath);
            if (parts.Length < 2) throw new ArgumentException("Tag/SystemConstant path must include the table: [group/]table/name.");
            object root = unit == null ? plc.TagTableGroup : PlcOrganisationPrimitives.TagTableGroup(unit);
            var table = (PlcTagTable)_session.ExactObjectUnder(root, string.Join("/", parts.Take(parts.Length - 1)), "TagTables", "tag table");
            object collection = kind.Equals("Tag", StringComparison.OrdinalIgnoreCase) ? table.Tags : (object)table.SystemConstants;
            return (IEngineeringServiceProvider)(PlcGroupOperations.Find(collection, parts.Last()) ?? throw new PlcSoftwareException("NotFound", "Exact tag/constant not found: " + objectPath));
        }
#else
        public List<PlcCrossReference>? GetCrossReferences(string softwarePath, string objectPath, string objectKind, string filter, out string? reason, out bool queried, string unitName, string unitKind)
        { reason = "CrossReferenceService requires V18 or later."; queried = false; return null; }
        public string? CrossReferenceRefusal(string softwarePath) => "CrossReferenceService requires V18 or later.";
        public static List<PlcCrossReference> TryFlattenCrossReferenceResult(object result, string path) => throw new NotSupportedException("CrossReferenceService requires V18 or later.");
#endif
    }
}
