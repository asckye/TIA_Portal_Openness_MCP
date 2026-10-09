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

        public List<CrossReferenceEntry>? GetCrossReferences(string softwarePath, string objectPath, string objectKind, string filter, out string? reason, out bool queried, string unitName, string unitKind)
        {
            var rows = new TiaMcp.Adapters.Native.Plc.PlcCrossReferences(new TiaMcpServer.Worker.EnginePlcOrganisationSession(this))
                .GetCrossReferences(softwarePath, objectPath, objectKind, filter, out reason, out queried, unitName, unitKind);
            return rows == null ? null : System.Text.Json.JsonSerializer.Deserialize<List<CrossReferenceEntry>>(System.Text.Json.JsonSerializer.Serialize(rows));
        }




        public string? CrossReferenceRefusal(string softwarePath)
            => new TiaMcp.Adapters.Native.Plc.PlcCrossReferences(new TiaMcpServer.Worker.EnginePlcOrganisationSession(this)).CrossReferenceRefusal(softwarePath);

        private static List<CrossReferenceEntry> TryFlattenCrossReferenceResult(object result, string path)
            => System.Text.Json.JsonSerializer.Deserialize<List<CrossReferenceEntry>>(System.Text.Json.JsonSerializer.Serialize(TiaMcp.Adapters.Native.Plc.PlcCrossReferences.TryFlattenCrossReferenceResult(result, path)))!;

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



 // UnderlyingObject is deliberately not dereferenced just to obtain a display label.

        #endregion
    }
}
