using System;
using System.Collections.Generic;
using static TiaMcp.Logic.V4.Domain.DomainShape;

using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    internal static partial class DomainSchemas
    {
        private static void AddGraphicShapes(Dictionary<Type, InputSchema> d)
        {
            var text = String(); var nullableText = Union(text, Null()); var flag = Boolean(); var count = Integer(0);
            var evidence = Object(("path", text), ("kind", String(0, null, "value", "gap")), ("status", text), ("type?", nullableText),
                ("value?", Scalar), ("evidence?", text), ("access?", text), ("code?", text), ("reason?", text));
            var owner = Object(("status", text), ("evidence?", text), ("type?", nullableText), ("meaning?", text), ("name?", evidence),
                ("path?", text), ("kind?", text), ("code?", text), ("reason?", text));
            var capability = Object(("name", text), ("source", text), ("accessMode?", text), ("type?", nullableText), ("readable?", flag), ("contentsRead?", flag));
            var geometry = Object(("Left", evidence), ("Top", evidence), ("Width", evidence), ("Height", evidence));
            var envelope = Object(("left", Number()), ("top", Number()), ("width", Number()), ("height", Number()), ("nativeGroupBounds", Boolean(false)));
            d[typeof(GraphicEvidence)] = evidence; d[typeof(GraphicOwner)] = owner;
            d[typeof(GraphicCapability)] = capability; d[typeof(GraphicGeometry)] = geometry; d[typeof(GraphicCoordinateEnvelope)] = envelope;
            d[typeof(GraphicObjectRecord)] = Object(("kind", String(0, null, "graphicObject")), ("path", text), ("name", text), ("status", text),
                ("type?", nullableText), ("geometryComplete", flag), ("sampleStartedUtc?", text), ("sampleFinishedUtc?", text),
                ("fields?", geometry), ("owner?", owner), ("relationEvidence?", Array(evidence)), ("relationCapabilities?", Array(capability)),
                ("gaps?", Array(evidence)), ("nativeGroupVerified?", Boolean(false)), ("coordinateFrame?", text),
                ("attributeMetadataApiAvailable?", flag), ("compositionMetadataApiAvailable?", flag), ("code?", text), ("reason?", text));
            d[typeof(GraphicSummaryRecord)] = Object(("kind", String(0, null, "graphicSelectionSummary")), ("path", text), ("status", text),
                ("code?", text), ("reason?", text), ("expectedObjectCount?", count), ("actualObjectCount?", count), ("geometryComplete?", flag),
                ("nativeGroupVerified?", Boolean(false)), ("nativeGroup?", Null()), ("nativeGroupBounds?", Null()),
                ("rawCoordinateEnvelope?", Union(envelope, Null())), ("coordinateCaveat?", text));
            d[typeof(GraphicSelectionRecord)] = Union(d[typeof(GraphicObjectRecord)], d[typeof(GraphicSummaryRecord)]);
            d[typeof(GraphicPageFailure)] = Union(evidence, d[typeof(GraphicSelectionRecord)]);
            d[typeof(GraphicSelectionScope)] = Object(("tool", String(0, null, "ReadUnifiedGraphicSelection")), ("softwarePath", text),
                ("expectedProject", text), ("project", text), ("screenPath", text), ("itemNames", Array(String(1, 1024), 1, 128, true)));
            d[typeof(GraphicSelectionPage)] = Object(("pageIndex", count), ("collectionId", String(1)), ("scope", d[typeof(GraphicSelectionScope)]),
                ("apiCallSuccess", flag), ("traversalComplete", flag), ("truncated", flag), ("nextCursor", nullableText),
                ("records", Array(d[typeof(GraphicSelectionRecord)])), ("schemaVersion?", Integer(1, 1)), ("readOnly?", Boolean(true)),
                ("pageCursor?", nullableText), ("dataComplete?", flag), ("readComplete?", flag), ("classificationComplete?", flag),
                ("readFailureCount?", count), ("classificationFailureCount?", count), ("expectedCount?", Union(count, Null())),
                ("countUnit?", text), ("expectedCountReason?", text), ("actualCount?", count), ("cumulativeCount?", count),
                ("failureCount?", count), ("failures?", Array(d[typeof(GraphicPageFailure)])), ("elapsedMs?", Integer(0, long.MaxValue)),
                ("consistency?", text), ("budgetNote?", text), ("releaseCursor?", text), ("cursorPolicy?", text),
                ("success?", flag), ("operationSuccess?", flag), ("timestamp?", text), ("tool?", text), ("softwarePath?", text), ("screenPath?", text),
                ("expectedObjectCount?", count), ("selectionKind?", text), ("nativeGroupVerified?", Boolean(false)), ("operationId?", text),
                ("diagnosticLog?", text), ("phase?", text), ("lastAttemptedPath?", nullableText), ("lastCompletedPath?", nullableText), ("diagnosticLogError?", nullableText));
            d[typeof(GraphicSelectionPage[])] = Array(d[typeof(GraphicSelectionPage)], 1, 1024);
        }
    }
}
