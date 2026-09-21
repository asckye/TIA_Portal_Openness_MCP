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

            // Openness API for external sources differs across TIA versions:
            // some expose Import(FileInfo,...), others expose Add/Create/ImportFromFile(FileInfo,...).
            // Prefer the ExternalSources composition first — CreateFromFile lives there in V21.
            var targets = new List<object>();
            try
            {
                var extSourcesObj = group.GetType().GetProperty("ExternalSources")?.GetValue(group);
                if (extSourcesObj != null) targets.Add(extSourcesObj);
            }
            catch { }
            targets.Add(group);

            var fi = new FileInfo(filePath);
            if (!fi.Exists)
                throw new PortalException(PortalErrorCode.InvalidParams, $"ImportPlcExternalSource: file not found '{filePath}'");

            var candidates = new List<(object Target, MethodInfo Method)>();
            foreach (var tgt in targets)
            {
                var methods = tgt.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                candidates.AddRange(methods
                    .Where(m =>
                    {
                        var ps = m.GetParameters();
                        if (ps.Length < 1) return false;
                        if (ps[0].ParameterType != typeof(FileInfo) && ps[0].ParameterType != typeof(string)) return false;
                        var n = m.Name ?? "";
                        return n.StartsWith("Import", StringComparison.OrdinalIgnoreCase)
                               || n.StartsWith("Add", StringComparison.OrdinalIgnoreCase)
                               || n.StartsWith("Create", StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(m => (Target: tgt, Method: m)));
            }

            candidates = candidates
                .OrderBy(c => c.Method.GetParameters()[0].ParameterType == typeof(FileInfo) ? 0 : 1)
                .ThenBy(c => c.Method.Name.StartsWith("Import", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.Method.GetParameters().Length)
                .ToList();

            if (candidates.Count == 0)
            {
                string Dump(object tgt)
                {
                    try
                    {
                        var ms = tgt.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => (m.Name ?? "").IndexOf("Import", StringComparison.OrdinalIgnoreCase) >= 0
                                     || (m.Name ?? "").IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0
                                     || (m.Name ?? "").IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                            .Take(10);
                        return string.Join("; ", ms);
                    }
                    catch { return ""; }
                }

                var extDump = targets.Count > 1 ? Dump(targets[1]) : "";
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"ImportPlcExternalSource: No import-like method found. group={group.GetType().FullName} methods=[{Dump(group)}] extSources=[{extDump}]");
            }

            var failures = new List<string>();
            foreach (var candidate in candidates)
            {
                var importMethod = candidate.Method;
                var parms = importMethod.GetParameters();
                var argLists = BuildExternalSourceImportArguments(parms, fi);
                if (argLists.Count == 0) continue;

                foreach (var args in argLists)
                {
                    var sig = $"{importMethod.Name}({string.Join(", ", parms.Select(p => p.ParameterType.Name))})";
                    try
                    {
                        var result = importMethod.Invoke(candidate.Target, args);
                        if (importMethod.ReturnType == typeof(void) || result != null)
                        {
                            return;
                        }

                        failures.Add($"{sig} returned null");
                    }
                    catch (Exception ex)
                    {
                        var inner = (ex is TargetInvocationException tie && tie.InnerException != null) ? tie.InnerException : ex;
                        failures.Add($"{sig} threw {inner.GetType().FullName}: {inner.Message}");
                    }
                }
            }

            throw new PortalException(PortalErrorCode.OpennessError, "ImportPlcExternalSource: all import-like methods failed: " + string.Join(" | ", failures.Take(12)));
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

        private static List<object?[]> BuildExternalSourceImportArguments(ParameterInfo[] parms, FileInfo fi)
        {
            var result = new List<object?[]>();
            if (parms.Length < 1) return result;
            if (parms[0].ParameterType != typeof(FileInfo) && parms[0].ParameterType != typeof(string)) return result;

            object firstArg = parms[0].ParameterType == typeof(FileInfo) ? fi : fi.FullName;
            var sourceName = Path.GetFileNameWithoutExtension(fi.Name);

            if (parms.Length == 1)
            {
                result.Add(new object?[] { firstArg });
                return result;
            }

            // Siemens.Openness: PlcExternalSourceComposition.CreateFromFile(string name, string path)
            // Manual 5.11.3.x — first arg is the external-source *name* (often "Block_1.scl"), second is full path.
            // Older reflection code wrongly passed (FullPath, fileTitleWithoutExtension).
            if (parms.Length == 2 && parms[0].ParameterType == typeof(string) && parms[1].ParameterType == typeof(string))
            {
                result.Add(new object?[] { fi.Name, fi.FullName });
                if (!string.IsNullOrEmpty(sourceName) &&
                    !string.Equals(sourceName, fi.Name, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new object?[] { sourceName, fi.FullName });
                }
                return result;
            }

            if (parms.Length == 2 && parms[0].ParameterType == typeof(FileInfo) && parms[1].ParameterType == typeof(string))
            {
                result.Add(new object?[] { fi, sourceName });
                return result;
            }

            if (parms.Length == 2 && parms[1].ParameterType.IsEnum)
            {
                foreach (var preferred in new[] { "Override", "Overwrite", "Replace", "None" })
                {
                    try
                    {
                        result.Add(new object?[] { firstArg, Enum.Parse(parms[1].ParameterType, preferred, ignoreCase: true) });
                    }
                    catch { }
                }
                foreach (var value in Enum.GetValues(parms[1].ParameterType))
                {
                    if (!result.Any(args => Equals(args[1], value))) result.Add(new object?[] { firstArg, value });
                }
                return result;
            }

            if (parms.Skip(1).All(p => p.IsOptional))
            {
                result.Add(new[] { firstArg }.Concat(parms.Skip(1).Select(p => p.DefaultValue)).ToArray());
            }

            return result;
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

            // V18+ often exposes GenerateBlocksFromSource(PlcBlockUserGroup, GenerateBlockOption) only;
            // parameterless GenerateBlocks() may not exist.
            var t = src.GetType();
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m =>
                {
                    var n = m.Name ?? "";
                    return n.Equals("GenerateBlocks", StringComparison.OrdinalIgnoreCase)
                           || n.Equals("GenerateBlocksFromSource", StringComparison.OrdinalIgnoreCase)
                           || n.Equals("GenerateBlocksFromExternalSource", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(m => m.GetParameters().Length)
                .ToList();

            var failures = new List<string>();
            foreach (var gen in methods)
            {
                var ps = gen.GetParameters();
                try
                {
                    if (ps.Length == 0)
                    {
                        gen.Invoke(src, Array.Empty<object>());
                        return;
                    }

                    if (ps.Length == 2 && ps[1].ParameterType.IsEnum)
                    {
                        var folderType = ps[0].ParameterType;
                        var blockRoot = plcSoftware.BlockGroup;
                        if (blockRoot == null)
                        {
                            failures.Add($"{gen.Name}: BlockGroup is null");
                            continue;
                        }

                        if (!folderType.IsAssignableFrom(blockRoot.GetType()))
                        {
                            failures.Add($"{gen.Name}: BlockGroup type {blockRoot.GetType().Name} not assignable to {folderType.Name}");
                            continue;
                        }

                        object optionVal;
                        try
                        {
                            optionVal = Enum.Parse(ps[1].ParameterType, "None", ignoreCase: true);
                        }
                        catch
                        {
                            var vals = Enum.GetValues(ps[1].ParameterType);
                            if (vals.Length == 0)
                            {
                                failures.Add($"{gen.Name}: GenerateBlockOption enum empty");
                                continue;
                            }
                            optionVal = vals.GetValue(0)!;
                        }

                        gen.Invoke(src, new[] { blockRoot, optionVal });
                        return;
                    }
                }
                catch (Exception ex)
                {
                    var inner = (ex is TargetInvocationException tie && tie.InnerException != null) ? tie.InnerException : ex;
                    failures.Add($"{gen.Name}({ps.Length}): {inner.Message}");
                }
            }

            throw new PortalException(PortalErrorCode.OpennessError, "GenerateBlocksFromExternalSource: " + string.Join(" | ", failures.Take(10)));
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
