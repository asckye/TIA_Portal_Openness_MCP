using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        #region software - CrossReferences

        public List<ModelContextProtocol.CrossReferenceEntry>? GetCrossReferences(string softwarePath, string objectPath, string objectKind = "Block", string filter = "AllObjects")
            => GetCrossReferences(softwarePath, objectPath, objectKind, filter, out _);

        // Typed CrossReferenceService / CrossReferenceFilter (Siemens.Engineering.CrossReference, V20 and V21) with the real
        // reason for a null result - the reflective lookup swallowed everything (real project: filter "" -> Enum.Parse threw ->
        // "Cross reference service not available", although the service was there).
        public List<ModelContextProtocol.CrossReferenceEntry>? GetCrossReferences(string softwarePath, string objectPath, string objectKind, string filter, out string? reason)
            => GetCrossReferences(softwarePath, objectPath, objectKind, filter, out reason, out _);

        public List<ModelContextProtocol.CrossReferenceEntry>? GetCrossReferences(string softwarePath, string objectPath, string objectKind, string filter, out string? reason, out bool queried)
            => GetCrossReferences(softwarePath, objectPath, objectKind, filter, out reason, out queried, "", "unit");

        public List<CrossReferenceEntry>? GetCrossReferences(string softwarePath, string objectPath, string objectKind, string filter,
            out string? reason, out bool queried, string unitName, string unitKind)
        {
            queried = false;
            reason = CrossReferenceGuardLogic.PolicyRefusal(Environment.GetEnvironmentVariable(CrossReferenceGuardLogic.NativeQueryOptInVariable));
            if (reason != null) return null;
            if (string.IsNullOrWhiteSpace(filter)) filter = "AllObjects";
            if (!Enum.TryParse<global::Siemens.Engineering.CrossReference.CrossReferenceFilter>(filter, true, out var filterValue)
                || !Enum.IsDefined(typeof(global::Siemens.Engineering.CrossReference.CrossReferenceFilter), filterValue))
            { reason = "Invalid CrossReferenceFilter name: " + filter; return null; }
            if (IsProjectNull()) { reason = "No project is open."; return null; }
            try
            {
                reason = CrossReferenceRefusal(softwarePath);
                if (reason != null) return null;
                var target = ExactCrossReferenceTarget(softwarePath, objectPath, objectKind, unitName, unitKind);
                var service = InvocationJournal.Native("CrossReference.GetService", () => target.GetService<global::Siemens.Engineering.CrossReference.CrossReferenceService>());
                if (service == null) { reason = "CrossReferenceService unavailable on this exact " + objectKind + ". No query was made."; return null; }
                queried = true;
                var result = InvocationJournal.Native("CrossReference.GetCrossReferences", () => service.GetCrossReferences(filterValue));
                return InvocationJournal.Native("CrossReference.readCompleteResult", () => TryFlattenCrossReferenceResult(result, objectPath));
            }
            catch (Exception ex) when (RecoverableAuditError(ex))
            { reason = (queried ? "failed: result is incomplete; partial rows discarded. " : "notQueried: ") + ex.GetBaseException().Message; return null; }
        }

        public string? CrossReferenceRefusal(string softwarePath)
        {
            var policy = CrossReferenceGuardLogic.PolicyRefusal(Environment.GetEnvironmentVariable(CrossReferenceGuardLogic.NativeQueryOptInVariable));
            if (policy != null) return policy;
            try
            {
                var rows = ReadPlcConsistency(softwarePath);
                if (rows.Count == 0) return "refused: no block/type consistency evidence; no native cross-reference query was made.";
                return CrossReferenceGuardLogic.Refusal(rows, softwarePath);
            }
            catch (Exception ex) when (RecoverableAuditError(ex))
            { return "refused: root/unit consistency read incomplete: " + ex.GetBaseException().Message; }
        }

        private static object? TryGetServiceByTypeSuffix(object target, string serviceTypeNameSuffix)
        {
            DenyCrossReferenceReflection(serviceTypeNameSuffix, null);
            try
            {
                var getService = target.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (getService == null) return null;

                var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); } catch { /* swallow(probe-optional): An unavailable reflection service or assembly is skipped while probing the optional cross-reference API. */ return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t => t.Name.Equals(serviceTypeNameSuffix, StringComparison.OrdinalIgnoreCase) ||
                                         t.FullName?.EndsWith("." + serviceTypeNameSuffix, StringComparison.OrdinalIgnoreCase) == true);
                if (serviceType == null) return null;

                return getService.MakeGenericMethod(serviceType).Invoke(target, Array.Empty<object>());
            }
            catch
            { /* swallow(probe-optional): An unavailable reflection service or assembly is skipped while probing the optional cross-reference API. */
                return null;
            }
        }

        private static List<CrossReferenceEntry> TryFlattenCrossReferenceResult(object crossReferenceResult, string sourcePathFallback)
        {
            if (!(crossReferenceResult is global::Siemens.Engineering.CrossReference.CrossReferenceResult result))
                throw new InvalidOperationException("Native query returned no supported result; completeness unknown.");
            return CrossReferenceTreeReader.Read<global::Siemens.Engineering.CrossReference.SourceObject,
                global::Siemens.Engineering.CrossReference.ReferenceObject, global::Siemens.Engineering.CrossReference.Location, CrossReferenceEntry>(
                EngineeringGroupOperations.Items(result.Sources).Cast<global::Siemens.Engineering.CrossReference.SourceObject>(),
                source => EngineeringGroupOperations.Items(source.References).Cast<global::Siemens.Engineering.CrossReference.ReferenceObject>(),
                reference => EngineeringGroupOperations.Items(reference.Locations).Cast<global::Siemens.Engineering.CrossReference.Location>(),
                source => EngineeringGroupOperations.Items(source.Children).Cast<global::Siemens.Engineering.CrossReference.SourceObject>(),
                (source, reference, location) => BuildCrossReferenceEntry(source, reference, location, sourcePathFallback));
        }

        private static CrossReferenceEntry BuildCrossReferenceEntry(global::Siemens.Engineering.CrossReference.SourceObject source,
            global::Siemens.Engineering.CrossReference.ReferenceObject reference, global::Siemens.Engineering.CrossReference.Location? location, string fallback)
            => new CrossReferenceEntry {
                SourceName = source.Name, SourcePath = source.Path ?? fallback, SourceTypeName = source.TypeName,
                SourceAddress = source.Address, SourceDevice = source.Device,
                ReferenceName = reference.Name, ReferencePath = reference.Path, ReferenceTypeName = reference.TypeName,
                ReferenceAddress = reference.Address, ReferenceDevice = reference.Device,
                LocationName = location?.Name, ReferenceLocation = location?.ReferenceLocation,
                ReferenceType = location?.ReferenceType.ToString(), Access = location?.Access.ToString()
            }; // UnderlyingObject is deliberately not dereferenced just to obtain a display label.

        #endregion
    }
}
