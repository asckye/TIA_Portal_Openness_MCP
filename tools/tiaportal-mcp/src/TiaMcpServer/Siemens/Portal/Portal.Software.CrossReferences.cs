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
    // Partial: software. Family file split out of Portal.Software.cs (2.8.0); behavior unchanged.
    public partial class Portal
    {
        #region software - CrossReferences

        public List<ModelContextProtocol.CrossReferenceEntry>? GetCrossReferences(string softwarePath, string objectPath, string objectKind = "Block", string filter = "AllObjects")
            => GetCrossReferences(softwarePath, objectPath, objectKind, filter, out _);

        // 2.7.46: typed CrossReferenceService / CrossReferenceFilter (Siemens.Engineering.CrossReference, V20 and V21) with the real
        // reason for a null result - the reflective lookup swallowed everything (real project: filter "" -> Enum.Parse threw ->
        // "Cross reference service not available", although the service was there).
        public List<ModelContextProtocol.CrossReferenceEntry>? GetCrossReferences(string softwarePath, string objectPath, string objectKind, string filter, out string? reason)
        {
            reason = null;
            if (IsProjectNull()) { reason = "No project is open."; return null; }

            IEngineeringServiceProvider? target = string.Equals(objectKind, "Type", StringComparison.OrdinalIgnoreCase)
                ? GetType(softwarePath, objectPath)
                : GetBlock(softwarePath, objectPath);
            if (target == null) { reason = objectKind + " not found: " + objectPath; return null; }

            if (string.IsNullOrWhiteSpace(filter)) filter = "AllObjects";
            if (!Enum.TryParse<global::Siemens.Engineering.CrossReference.CrossReferenceFilter>(filter, true, out var filterValue))
            {
                reason = "filter must be one of: " + string.Join("/", Enum.GetNames(typeof(global::Siemens.Engineering.CrossReference.CrossReferenceFilter))) + " (got '" + filter + "').";
                return null;
            }

            var crossReferenceService = target.GetService<global::Siemens.Engineering.CrossReference.CrossReferenceService>();
            if (crossReferenceService == null) { reason = "CrossReferenceService is not provided by this " + objectKind + " (TIA answers it for blocks and types; not for software / device level)."; return null; }

            global::Siemens.Engineering.CrossReference.CrossReferenceResult result;
            try { result = crossReferenceService.GetCrossReferences(filterValue); }
            catch (Exception ex) { reason = "GetCrossReferences(" + filterValue + ") failed: " + ex.GetBaseException().Message; return null; }
            if (result == null) { reason = "GetCrossReferences returned null."; return null; }

            return TryFlattenCrossReferenceResult(result, objectPath);
        }

        private static object? TryGetServiceByTypeSuffix(object target, string serviceTypeNameSuffix)
        {
            try
            {
                var getService = target.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (getService == null) return null;

                var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t => t.Name.Equals(serviceTypeNameSuffix, StringComparison.OrdinalIgnoreCase) ||
                                         t.FullName?.EndsWith("." + serviceTypeNameSuffix, StringComparison.OrdinalIgnoreCase) == true);
                if (serviceType == null) return null;

                return getService.MakeGenericMethod(serviceType).Invoke(target, Array.Empty<object>());
            }
            catch
            {
                return null;
            }
        }

        private static object? TryInvokeGetCrossReferences(object crossReferenceService, string filterName)
        {
            try
            {
                var svcType = crossReferenceService.GetType();
                var filterType = svcType.Assembly.GetTypes()
                    .FirstOrDefault(t => t.IsEnum && t.Name.Equals("CrossReferenceFilter", StringComparison.OrdinalIgnoreCase));
                if (filterType == null) return null;

                var filterValue = Enum.Parse(filterType, filterName, ignoreCase: true);
                var m = svcType.GetMethod("GetCrossReferences", new[] { filterType });
                if (m == null) return null;

                return m.Invoke(crossReferenceService, new[] { filterValue });
            }
            catch
            {
                return null;
            }
        }

        private static List<ModelContextProtocol.CrossReferenceEntry> TryFlattenCrossReferenceResult(object crossReferenceResult, string sourcePathFallback)
        {
            var items = new List<ModelContextProtocol.CrossReferenceEntry>();
            if (crossReferenceResult is global::Siemens.Engineering.CrossReference.CrossReferenceResult typedResult)
            {
                // 2.7.33: typed walk - SourceObject (Name/Path/TypeName/Address/Device/Children/References/UnderlyingObject) and
                // ReferenceObject (same scalars + Locations/UnderlyingObject) straight from the official CrossReference namespace.
                try { FlattenTypedSources(typedResult.Sources, sourcePathFallback, items, 0); } catch { }
                return items;
            }

            try
            {
                var sources = crossReferenceResult.GetType().GetProperty("Sources")?.GetValue(crossReferenceResult) as IEnumerable;
                if (sources == null) return items;

                foreach (var src in sources)
                {
                    if (src == null) continue;
                    var srcName = src.GetType().GetProperty("Name")?.GetValue(src)?.ToString();
                    var srcPath = src.GetType().GetProperty("Path")?.GetValue(src)?.ToString() ?? sourcePathFallback;

                    var refs = src.GetType().GetProperty("References")?.GetValue(src) as IEnumerable;
                    if (refs == null) continue;

                    foreach (var rf in refs)
                    {
                        if (rf == null) continue;
                        var refName = rf.GetType().GetProperty("Name")?.GetValue(rf)?.ToString();
                        var refPath = rf.GetType().GetProperty("Path")?.GetValue(rf)?.ToString();

                        var locations = rf.GetType().GetProperty("Locations")?.GetValue(rf) as IEnumerable;
                        if (locations == null)
                        {
                            items.Add(new ModelContextProtocol.CrossReferenceEntry
                            {
                                SourceName = srcName,
                                SourcePath = srcPath,
                                ReferenceName = refName,
                                ReferencePath = refPath
                            });
                            continue;
                        }

                        foreach (var loc in locations)
                        {
                            if (loc == null) continue;
                            items.Add(new ModelContextProtocol.CrossReferenceEntry
                            {
                                SourceName = srcName,
                                SourcePath = srcPath,
                                ReferenceName = refName,
                                ReferencePath = refPath,
                                LocationName = loc.GetType().GetProperty("Name")?.GetValue(loc)?.ToString(),
                                ReferenceLocation = loc.GetType().GetProperty("ReferenceLocation")?.GetValue(loc)?.ToString(),
                                ReferenceType = loc.GetType().GetProperty("ReferenceType")?.GetValue(loc)?.ToString(),
                                Access = loc.GetType().GetProperty("Access")?.GetValue(loc)?.ToString()
                            });
                        }
                    }
                }
            }
            catch
            {
                // best-effort
            }

            return items;
        }

        private static void FlattenTypedSources(global::Siemens.Engineering.CrossReference.SourceObjectComposition sources, string sourcePathFallback, List<ModelContextProtocol.CrossReferenceEntry> items, int depth)
        {
            if (depth > 16) return;
            foreach (global::Siemens.Engineering.CrossReference.SourceObject source in EngineeringGroupOperations.Items(sources).Cast<global::Siemens.Engineering.CrossReference.SourceObject>())
            {
                string? sourceName = source.Name, sourcePath = source.Path ?? sourcePathFallback, sourceType = source.TypeName, sourceAddress = source.Address, sourceDevice = source.Device;
                string? sourceClass = null; try { sourceClass = source.UnderlyingObject?.GetType().Name; } catch { }
                foreach (global::Siemens.Engineering.CrossReference.ReferenceObject reference in EngineeringGroupOperations.Items(source.References).Cast<global::Siemens.Engineering.CrossReference.ReferenceObject>())
                {
                    string? referenceClass = null; try { referenceClass = reference.UnderlyingObject?.GetType().Name; } catch { }
                    var locations = EngineeringGroupOperations.Items(reference.Locations).ToArray();
                    if (locations.Length == 0)
                    {
                        items.Add(new ModelContextProtocol.CrossReferenceEntry { SourceName = sourceName, SourcePath = sourcePath, SourceTypeName = sourceType, SourceAddress = sourceAddress, SourceDevice = sourceDevice, SourceObjectClass = sourceClass,
                            ReferenceName = reference.Name, ReferencePath = reference.Path, ReferenceTypeName = reference.TypeName, ReferenceAddress = reference.Address, ReferenceDevice = reference.Device, ReferenceObjectClass = referenceClass });
                        continue;
                    }
                    foreach (var location in locations)
                        items.Add(new ModelContextProtocol.CrossReferenceEntry { SourceName = sourceName, SourcePath = sourcePath, SourceTypeName = sourceType, SourceAddress = sourceAddress, SourceDevice = sourceDevice, SourceObjectClass = sourceClass,
                            ReferenceName = reference.Name, ReferencePath = reference.Path, ReferenceTypeName = reference.TypeName, ReferenceAddress = reference.Address, ReferenceDevice = reference.Device, ReferenceObjectClass = referenceClass,
                            LocationName = location.GetType().GetProperty("Name")?.GetValue(location)?.ToString(), ReferenceLocation = location.GetType().GetProperty("ReferenceLocation")?.GetValue(location)?.ToString(),
                            ReferenceType = location.GetType().GetProperty("ReferenceType")?.GetValue(location)?.ToString(), Access = location.GetType().GetProperty("Access")?.GetValue(location)?.ToString() });
                }
                try { FlattenTypedSources(source.Children, sourcePathFallback, items, depth + 1); } catch { }
            }
        }

        #endregion
    }
}
