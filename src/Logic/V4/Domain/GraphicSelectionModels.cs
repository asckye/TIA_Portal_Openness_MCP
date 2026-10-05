using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class GraphicSelectionPage : DomainDto
    {
        internal GraphicSelectionPage(JsonElement json) : base(json) { }
        public int PageIndex => Required<int>("pageIndex");
        public string CollectionId => Required<string>("collectionId");
        public GraphicSelectionScope Scope => Required<GraphicSelectionScope>("scope");
        public bool ApiCallSuccess => Required<bool>("apiCallSuccess");
        public bool TraversalComplete => Required<bool>("traversalComplete");
        public bool Truncated => Required<bool>("truncated");
        public string? NextCursor => Optional<string?>("nextCursor", null);
        public IReadOnlyList<GraphicSelectionRecord> Records => Items<GraphicSelectionRecord>("records");
        public int? SchemaVersion => Optional<int?>("schemaVersion", null);
        public bool? ReadOnly => Optional<bool?>("readOnly", null);
        public string? PageCursor => Optional<string?>("pageCursor", null);
        public bool? DataComplete => Optional<bool?>("dataComplete", null);
        public bool? ReadComplete => Optional<bool?>("readComplete", null);
        public bool? ClassificationComplete => Optional<bool?>("classificationComplete", null);
        public int? ReadFailureCount => Optional<int?>("readFailureCount", null);
        public int? ClassificationFailureCount => Optional<int?>("classificationFailureCount", null);
        public int? ExpectedCount => Optional<int?>("expectedCount", null);
        public string? CountUnit => Optional<string?>("countUnit", null);
        public string? ExpectedCountReason => Optional<string?>("expectedCountReason", null);
        public int? ActualCount => Optional<int?>("actualCount", null);
        public int? CumulativeCount => Optional<int?>("cumulativeCount", null);
        public int? FailureCount => Optional<int?>("failureCount", null);
        public IReadOnlyList<GraphicPageFailure>? Failures => Optional<IReadOnlyList<GraphicPageFailure>?>("failures", null);
        public long? ElapsedMs => Optional<long?>("elapsedMs", null);
        public string? Consistency => Optional<string?>("consistency", null);
        public string? BudgetNote => Optional<string?>("budgetNote", null);
        public string? ReleaseCursor => Optional<string?>("releaseCursor", null);
        public string? CursorPolicy => Optional<string?>("cursorPolicy", null);
        public bool? Success => Optional<bool?>("success", null);
        public bool? OperationSuccess => Optional<bool?>("operationSuccess", null);
        public string? Timestamp => Optional<string?>("timestamp", null);
        public string? Tool => Optional<string?>("tool", null);
        public string? SoftwarePath => Optional<string?>("softwarePath", null);
        public string? ScreenPath => Optional<string?>("screenPath", null);
        public int? ExpectedObjectCount => Optional<int?>("expectedObjectCount", null);
        public string? SelectionKind => Optional<string?>("selectionKind", null);
        public bool? NativeGroupVerified => Optional<bool?>("nativeGroupVerified", null);
        public string? OperationId => Optional<string?>("operationId", null);
        public string? DiagnosticLog => Optional<string?>("diagnosticLog", null);
        public string? Phase => Optional<string?>("phase", null);
        public string? LastAttemptedPath => Optional<string?>("lastAttemptedPath", null);
        public string? LastCompletedPath => Optional<string?>("lastCompletedPath", null);
        public string? DiagnosticLogError => Optional<string?>("diagnosticLogError", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class GraphicPageFailure : DomainDto
    {
        internal GraphicPageFailure(JsonElement json) : base(json) { }
        public string Kind => Required<string>("kind");
        public GraphicEvidence? Evidence => Kind == "value" || Kind == "gap" ? V4Json.Deserialize<GraphicEvidence>(Json.GetRawText()) : null;
        public GraphicSelectionRecord? Record => Evidence == null ? V4Json.Deserialize<GraphicSelectionRecord>(Json.GetRawText()) : null;
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class GraphicSelectionScope : DomainDto
    {
        internal GraphicSelectionScope(JsonElement json) : base(json) { }
        public string Tool => Required<string>("tool");
        public string SoftwarePath => Required<string>("softwarePath");
        public string ExpectedProject => Required<string>("expectedProject");
        public string Project => Required<string>("project");
        public string ScreenPath => Required<string>("screenPath");
        public IReadOnlyList<string> ItemNames => Items<string>("itemNames");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal abstract class GraphicSelectionRecord : DomainDto
    {
        internal GraphicSelectionRecord(JsonElement json) : base(json) { }
        public string Kind => Required<string>("kind");
        public string Path => Required<string>("path");
        public string Status => Required<string>("status");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class GraphicObjectRecord : GraphicSelectionRecord
    {
        internal GraphicObjectRecord(JsonElement json) : base(json) { }
        public string Name => Required<string>("name");
        public string? Type => Optional<string?>("type", null);
        public bool GeometryComplete => Required<bool>("geometryComplete");
        public string? SampleStartedUtc => Optional<string?>("sampleStartedUtc", null);
        public string? SampleFinishedUtc => Optional<string?>("sampleFinishedUtc", null);
        public GraphicGeometry? Fields => Optional<GraphicGeometry?>("fields", null);
        public GraphicOwner? Owner => Optional<GraphicOwner?>("owner", null);
        public IReadOnlyList<GraphicEvidence>? RelationEvidence => Optional<IReadOnlyList<GraphicEvidence>?>("relationEvidence", null);
        public IReadOnlyList<GraphicCapability>? RelationCapabilities => Optional<IReadOnlyList<GraphicCapability>?>("relationCapabilities", null);
        public IReadOnlyList<GraphicEvidence>? Gaps => Optional<IReadOnlyList<GraphicEvidence>?>("gaps", null);
        public bool? NativeGroupVerified => Optional<bool?>("nativeGroupVerified", null);
        public string? CoordinateFrame => Optional<string?>("coordinateFrame", null);
        public bool? AttributeMetadataApiAvailable => Optional<bool?>("attributeMetadataApiAvailable", null);
        public bool? CompositionMetadataApiAvailable => Optional<bool?>("compositionMetadataApiAvailable", null);
        public string? Code => Optional<string?>("code", null);
        public string? Reason => Optional<string?>("reason", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class GraphicSummaryRecord : GraphicSelectionRecord
    {
        internal GraphicSummaryRecord(JsonElement json) : base(json) { }
        public string? Code => Optional<string?>("code", null);
        public string? Reason => Optional<string?>("reason", null);
        public int? ExpectedObjectCount => Optional<int?>("expectedObjectCount", null);
        public int? ActualObjectCount => Optional<int?>("actualObjectCount", null);
        public bool? GeometryComplete => Optional<bool?>("geometryComplete", null);
        public bool? NativeGroupVerified => Optional<bool?>("nativeGroupVerified", null);
        public Scalar NativeGroup => Optional("nativeGroup", NullScalar);
        public Scalar NativeGroupBounds => Optional("nativeGroupBounds", NullScalar);
        public GraphicCoordinateEnvelope? RawCoordinateEnvelope => Optional<GraphicCoordinateEnvelope?>("rawCoordinateEnvelope", null);
        public string? CoordinateCaveat => Optional<string?>("coordinateCaveat", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class GraphicGeometry : DomainDto
    {
        internal GraphicGeometry(JsonElement json) : base(json) { }
        public GraphicEvidence Left => Required<GraphicEvidence>("Left");
        public GraphicEvidence Top => Required<GraphicEvidence>("Top");
        public GraphicEvidence Width => Required<GraphicEvidence>("Width");
        public GraphicEvidence Height => Required<GraphicEvidence>("Height");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class GraphicEvidence : DomainDto
    {
        internal GraphicEvidence(JsonElement json) : base(json) { }
        public string Path => Required<string>("path");
        public string Kind => Required<string>("kind");
        public string Status => Required<string>("status");
        public string? Type => Optional<string?>("type", null);
        public Scalar Value => Optional("value", NullScalar);
        public string? Evidence => Optional<string?>("evidence", null);
        public string? Access => Optional<string?>("access", null);
        public string? Code => Optional<string?>("code", null);
        public string? Reason => Optional<string?>("reason", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class GraphicOwner : DomainDto
    {
        internal GraphicOwner(JsonElement json) : base(json) { }
        public string Status => Required<string>("status");
        public string? Evidence => Optional<string?>("evidence", null);
        public string? Type => Optional<string?>("type", null);
        public string? Meaning => Optional<string?>("meaning", null);
        public GraphicEvidence? Name => Optional<GraphicEvidence?>("name", null);
        public string? Path => Optional<string?>("path", null);
        public string? Kind => Optional<string?>("kind", null);
        public string? Code => Optional<string?>("code", null);
        public string? Reason => Optional<string?>("reason", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class GraphicCapability : DomainDto
    {
        internal GraphicCapability(JsonElement json) : base(json) { }
        public string Name => Required<string>("name");
        public string Source => Required<string>("source");
        public string? AccessMode => Optional<string?>("accessMode", null);
        public string? Type => Optional<string?>("type", null);
        public bool? Readable => Optional<bool?>("readable", null);
        public bool? ContentsRead => Optional<bool?>("contentsRead", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class GraphicCoordinateEnvelope : DomainDto
    {
        internal GraphicCoordinateEnvelope(JsonElement json) : base(json) { }
        public decimal Left => Required<decimal>("left");
        public decimal Top => Required<decimal>("top");
        public decimal Width => Required<decimal>("width");
        public decimal Height => Required<decimal>("height");
        public bool NativeGroupBounds => Required<bool>("nativeGroupBounds");
    }

}
