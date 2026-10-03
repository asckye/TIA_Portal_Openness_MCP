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
        #region software - ExternalSources

        public List<string>? GetPlcExternalSources(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware) return null;

            var sources = TryGetExternalSourcesCollection(plcSoftware);
            if (sources == null) return new List<string>();

            var names = new List<string>();
            foreach (var item in sources)
            {
                if (item == null) continue;
                var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
            }
            return names;
        }

        /// <summary>
        /// Removes a PLC external source by name (e.g. <c>Ramp.scl</c> or <c>Ramp</c>) so a subsequent
        /// <see cref="ImportPlcExternalSource"/> can recreate it. Returns true if deleted or if no matching source exists.
        /// </summary>
        public void DeletePlcExternalSource(string softwarePath, string externalSourceName)
        {
            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState, "DeletePlcExternalSource: project is null");

            if (string.IsNullOrWhiteSpace(externalSourceName))
                throw new PortalException(PortalErrorCode.InvalidParams, "DeletePlcExternalSource: externalSourceName is empty");

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                throw new PortalException(PortalErrorCode.NotFound, $"DeletePlcExternalSource: PlcSoftware not found at '{softwarePath}'");

            var sources = TryGetExternalSourcesCollection(plcSoftware);
            if (sources == null)
                throw new PortalException(PortalErrorCode.OpennessError, "DeletePlcExternalSource: ExternalSources collection not available");

            foreach (var item in sources)
            {
                if (item == null) continue;
                var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!ExternalSourceNameMatches(name!, externalSourceName)) continue;

                try
                {
                    var del = item.GetType().GetMethod("Delete", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (del != null)
                    {
                        del.Invoke(item, null);
                        return;
                    }

                    throw new PortalException(PortalErrorCode.OpennessError, $"DeletePlcExternalSource: no parameterless Delete() on {item.GetType().Name}");
                }
                catch (PortalException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new PortalException(PortalErrorCode.OpennessError, $"DeletePlcExternalSource: {ex.Message}", null, ex);
                }
            }

            // source not present = idempotent no-op success
        }

        public void ImportPlcExternalSource(string softwarePath, string groupPath, string filePath)
        {
            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState, "ImportPlcExternalSource: project is null");
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                throw new PortalException(PortalErrorCode.NotFound, $"ImportPlcExternalSource: PlcSoftware not found at '{softwarePath}'");

            var group = TryGetExternalSourceGroupByPath(plcSoftware, groupPath);
            if (group == null)
                throw new PortalException(PortalErrorCode.NotFound, $"ImportPlcExternalSource: ExternalSourceGroup not found (groupPath='{groupPath}')");

            var fi = new FileInfo(filePath);
            if (!fi.Exists) throw new PortalException(PortalErrorCode.InvalidParams, "External source file not found: " + filePath);
            var target = group as global::Siemens.Engineering.SW.ExternalSources.PlcExternalSourceGroup;
            if (target == null) throw new PortalException(PortalErrorCode.OpennessError, "Expected a public PLC external-source group.");
            try
            {
                // Official V20/V21 signature: source name, then the full source file path.
                var source = target.ExternalSources.CreateFromFile(fi.Name, fi.FullName);
                if (source == null) throw new InvalidOperationException("CreateFromFile returned no source.");
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    "External source import outcome is unknown. Inspect the source collection before another write; no alternative import was attempted.", inner: ex);
            }
        }

        private static bool ExternalSourceNameMatches(string actualName, string requested)
        {
            if (string.IsNullOrWhiteSpace(actualName)) return false;
            if (string.Equals(actualName, requested, StringComparison.OrdinalIgnoreCase)) return true;
            var req = (requested ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(req)) return false;
            var reqNoExt = Path.GetFileNameWithoutExtension(req);
            var actNoExt = Path.GetFileNameWithoutExtension(actualName);
            if (string.Equals(actNoExt, reqNoExt, StringComparison.OrdinalIgnoreCase)) return true;
            if (req.IndexOf('.') < 0 &&
                string.Equals(actualName, req + ".scl", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        public void GenerateBlocksFromExternalSource(string softwarePath, string externalSourceName)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "GenerateBlocksFromExternalSource: project is null");
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware) throw new PortalException(PortalErrorCode.NotFound, $"GenerateBlocksFromExternalSource: PlcSoftware not found at '{softwarePath}'");

            var sources = TryGetExternalSourcesCollection(plcSoftware);
            if (sources == null) throw new PortalException(PortalErrorCode.OpennessError, "GenerateBlocksFromExternalSource: ExternalSources collection not available");

            object? src = null;
            foreach (var item in sources)
            {
                if (item == null) continue;
                var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (ExternalSourceNameMatches(name!, externalSourceName))
                {
                    src = item;
                    break;
                }
            }
            if (src == null) throw new PortalException(PortalErrorCode.NotFound, $"GenerateBlocksFromExternalSource: external source not found: {externalSourceName}");

            var source = src as global::Siemens.Engineering.SW.ExternalSources.PlcExternalSource;
            if (source == null) throw new PortalException(PortalErrorCode.OpennessError, "Expected a public PLC external source.");
            try
            {
                source.GenerateBlocksFromSource();
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    "Block generation outcome is unknown. Inspect generated blocks before another write; no alternative generation was attempted.", inner: ex);
            }
        }

        private static IEnumerable<object?>? TryGetExternalSourcesCollection(PlcSoftware plcSoftware)
        {
            try
            {
                var group = plcSoftware.GetType().GetProperty("ExternalSourceGroup")?.GetValue(plcSoftware)
                           ?? plcSoftware.GetType().GetProperty("ExternalSources")?.GetValue(plcSoftware);
                if (group == null) return null;

                var sources = group.GetType().GetProperty("ExternalSources")?.GetValue(group) ?? group;
                return sources as IEnumerable<object?>;
            }
            catch
            {
                return null;
            }
        }

        private static object? TryGetExternalSourceGroupByPath(PlcSoftware plcSoftware, string groupPath)
        {
            try
            {
                var root = plcSoftware.GetType().GetProperty("ExternalSourceGroup")?.GetValue(plcSoftware);
                if (root == null) return null;

                if (string.IsNullOrWhiteSpace(groupPath) || groupPath == "/")
                {
                    return root;
                }

                var segments = groupPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                object current = root;
                foreach (var seg in segments)
                {
                    var groups = current.GetType().GetProperty("Groups")?.GetValue(current) as IEnumerable;
                    if (groups == null) return null;

                    object? next = null;
                    foreach (var g in groups)
                    {
                        if (g == null) continue;
                        var name = g.GetType().GetProperty("Name")?.GetValue(g)?.ToString();
                        if (string.Equals(name, seg, StringComparison.OrdinalIgnoreCase))
                        {
                            next = g;
                            break;
                        }
                    }
                    if (next == null) return null;
                    current = next;
                }

                return current;
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
