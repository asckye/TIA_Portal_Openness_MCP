using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
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

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HmiExchangeService
    {
        private readonly IEngineeringSession _session;

        public HmiExchangeService(IEngineeringSession session) => _session = session;

        #region software - HmiExchange

        public List<string>? GetHmiScreens(string softwarePath)
        {
            if (_session.IsProjectNull()) return null;
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;
            return HmiScreenTraversal.ListNames(softwareContainer.Software);
        }

        public List<string>? GetHmiTagTables(string softwarePath)
        {
            if (_session.IsProjectNull()) return null;
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;
            var sw = softwareContainer.Software;
            var tables = _session.TryGetHmiTagTablesCollection(sw);
            if (tables == null) return new List<string>();
            var names = _session.TryListNamesFromCollection(tables, Array.Empty<string>(), "TagTables");
            // Tables inside user folders are listed too (name only; ManageClassicHmiFolder read shows the folder).
            var root = _session.TryGetHmiTagRoot(sw);
            if (root != null)
                foreach (var table in _session.EnumerateHmiTagTablesRecursive(root))
                {
                    var name = _session.TryGetName(table);
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name!, StringComparer.Ordinal)) names.Add(name!);
                }
            return names;
        }

        public List<string>? GetHmiTags(string softwarePath, string tagTableName = "")
        {
            if (_session.IsProjectNull()) return null;
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;

            var sw = softwareContainer.Software;
            var tagRoot = _session.TryGetHmiTagRoot(sw);
            object? tagTable = string.IsNullOrWhiteSpace(tagTableName)
                ? null
                : _session.TryFindHmiTagTable(sw, tagTableName);
            // An unknown table is a lookup failure, not an empty table.
            if (!string.IsNullOrWhiteSpace(tagTableName) && tagTable == null)
                throw new PortalException(PortalErrorCode.NotFound, "HMI tag table not found: " + tagTableName + " (tables: " + string.Join(", ", GetHmiTagTables(softwarePath) ?? new List<string>()) + ").");

            var root = tagTable ?? tagRoot;
            return _session.TryListNamesFromCollection(root, new[] { "Tags" }, "Tags");
        }

        public List<string>? GetHmiConnections(string softwarePath)
        {
            if (_session.IsProjectNull()) return null;
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;
            var sw = softwareContainer.Software;
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) return new List<string>();
            return _session.TryListNamesFromCollection(connections, Array.Empty<string>(), "Connections");
        }

        public JsonObject ExportHmiScreen(string softwarePath, string screenName, string exportPath)
        {
            if (_session.IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project is null");
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            var screen = HmiExactAccess.Screen(softwareContainer.Software, screenName);
            if (screen == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI screen not found: {screenName}");

            var export = EngineeringExport.Export(screen, exportPath);
            export["operationSuccess"] = export["success"]?.DeepClone();
            return export;
        }

        public JsonObject ExportHmiTagTable(string softwarePath, string tagTableName, string exportPath)
        {
            if (_session.IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project is null");
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            var sw = softwareContainer.Software;
            var table = _session.TryFindHmiTagTable(sw, tagTableName);
            if (table == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI tag table not found: {tagTableName}");

            var export = EngineeringExport.Export(table, exportPath);
            export["operationSuccess"] = export["success"]?.DeepClone();
            return export;
        }

        public JsonObject ExportHmiConnection(string softwarePath, string connectionName, string exportPath)
        {
            if (_session.IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project is null");
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            var sw = softwareContainer.Software;
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI Connections collection not found on '{softwarePath}'");

            var connection = _session.FindExistingByName(connections, connectionName);
            if (connection == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI connection not found: {connectionName}");

            var export = EngineeringExport.Export(connection, exportPath);
            export["operationSuccess"] = export["success"]?.DeepClone();
            return export;
        }

        public (List<string> Exported, List<string> Failed)? ExportHmiProgram(string softwarePath, string exportDir, bool exportScreens = true, bool exportTagTables = true)
        {
            if (_session.IsProjectNull()) return null;

            var exported = new List<string>();
            var failed = new List<string>();

            Directory.CreateDirectory(exportDir);

            if (exportScreens)
            {
                var screens = GetHmiScreens(softwarePath) ?? new List<string>();
                foreach (var s in screens)
                {
                    var safe = _session.MakeSafeFileName(s);
                    var outPath = Path.Combine(exportDir, $"screen_{safe}.xml");
                    try { var result = ExportHmiScreen(softwarePath, s, outPath); if (result["success"]!.GetValue<bool>()) exported.Add(outPath); else failed.Add(result.ToJsonString()); }
                    catch (PortalException) { /* swallow(native-fallback): batch export records the failed object and continues with remaining entries */ failed.Add($"screen:{s}"); }
                }
            }

            if (exportTagTables)
            {
                var tables = GetHmiTagTables(softwarePath) ?? new List<string>();
                foreach (var t in tables)
                {
                    var safe = _session.MakeSafeFileName(t);
                    var outPath = Path.Combine(exportDir, $"tagtable_{safe}.xml");
                    try { var result = ExportHmiTagTable(softwarePath, t, outPath); if (result["success"]!.GetValue<bool>()) exported.Add(outPath); else failed.Add(result.ToJsonString()); }
                    catch (PortalException) { /* swallow(native-fallback): batch export records the failed object and continues with remaining entries */ failed.Add($"tagtable:{t}"); }
                }
            }

            return (exported, failed);
        }

        public void ImportHmiScreen(string softwarePath, string folderPath, string importPath)
        {
            if (_session.IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            LastImportNotes = null;
            try
            {
                var sw = softwareContainer.Software;
                GuardClassicScreenSize(sw, importPath);

                // Resolve screen folder then groups by folderPath
                object rootGroup = TryGetPropertyValue(sw, "ScreenFolder") ?? sw;
                var group = _session.TryResolveChildGroupByPath(rootGroup, folderPath) ?? rootGroup;

                // Locate screens collection: group.Screens OR group.ScreenFolder.Screens
                var screens = TryGetPropertyValue(group, "Screens");
                if (screens == null)
                {
                    var nestedFolder = TryGetPropertyValue(group, "ScreenFolder");
                    if (nestedFolder != null)
                    {
                        screens = TryGetPropertyValue(nestedFolder, "Screens");
                    }
                }

                if (screens == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"Screens collection not found. swType={sw.GetType().FullName} groupType={group.GetType().FullName}");

                if (_session.TryImportEngineeringObjectIntoCollection(screens, importPath, out _, out var err))
                    return;

                // A failed native call can already have modified the project. Its error text is not
                // proof of rollback: never strip attributes and replay an import after failure.
                throw new PortalException(PortalErrorCode.ImportFailed, (err ?? "ImportHmiScreen failed")
                    + " The project may have changed. No automatic retry was attempted; inspect the project before another import.");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, ex.Message, null, ex);
            }
        }

        // Notes of the last ImportHmiScreen, retained for compatibility with the tool wrapper.
        public string? LastImportNotes { get; private set; }

        // Native incident (TIA Portal V21; observation date not recorded; docs/reference/real-machine-ledger.md):
        // importing a classic screen whose Width / Height differ from the panel's display made TIA Portal V21
        // exit with NonRecoverableException "The screen size does not match the device" (TP700 Comfort 800x480, builder default
        // 640x480). The size is compared with an existing screen of the device (or its display attributes) before Import.
        private void GuardClassicScreenSize(object hmiSoftware, string importPath)
        {
            if (hmiSoftware is global::Siemens.Engineering.HmiUnified.HmiSoftware) return;       // Unified screens scale; the crash is a classic-panel behaviour
            int? xmlWidth = null, xmlHeight = null;
            try
            {
                var document = XDocument.Load(importPath);
                var screen = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Screen.Screen");
                var attributes = screen?.Element("AttributeList");
                if (int.TryParse(attributes?.Element("Width")?.Value, out var w)) xmlWidth = w;
                if (int.TryParse(attributes?.Element("Height")?.Value, out var h)) xmlHeight = h;
            }
            catch (Exception) { /* swallow(parse-fallback): unrecognized screen XML is left to the native importer for diagnosis */ return; }                                              // not a screen document we understand - TIA reports its own error
            if (xmlWidth == null || xmlHeight == null) return;

            int? deviceWidth = null, deviceHeight = null; string source = "";
            try
            {
                var root = TryGetPropertyValue(hmiSoftware, "ScreenFolder");
                var existing = root == null ? null : EnumerateClassicScreens(root).FirstOrDefault();
                if (existing is IEngineeringObject engineeringScreen)
                {
                    deviceWidth = Convert.ToInt32(engineeringScreen.GetAttribute("Width")); deviceHeight = Convert.ToInt32(engineeringScreen.GetAttribute("Height"));
                    source = "existing screen '" + _session.TryGetName(existing) + "'";
                }
            }
            catch (Exception) { /* swallow(probe-optional): unavailable display dimensions fall back to the next panel resolution source */ deviceWidth = null; }
            if (deviceWidth == null && hmiSoftware is IEngineeringObject software)
            {
                foreach (var pair in new[] { ("ScreenWidth", "ScreenHeight"), ("DisplayWidth", "DisplayHeight"), ("ResolutionWidth", "ResolutionHeight") })
                {
                    try { deviceWidth = Convert.ToInt32(software.GetAttribute(pair.Item1)); deviceHeight = Convert.ToInt32(software.GetAttribute(pair.Item2)); source = "HMI attributes " + pair.Item1 + "/" + pair.Item2; break; }
                    catch (Exception) { /* swallow(probe-optional): unavailable display dimensions fall back to the next panel resolution source */ deviceWidth = null; }
                }
            }
            if (deviceWidth == null)
            {
                // A fresh classic panel has no screen and no display attribute; the hardware catalog description of its head item
                // carries the display ("7" TFT 显示屏，800 x 480 像素").
                var resolution = TryReadPanelResolutionFromCatalog(hmiSoftware);
                if (resolution != null) { deviceWidth = resolution.Value.width; deviceHeight = resolution.Value.height; source = "hardware catalog description"; }
            }
            if (deviceWidth == null)
                throw new PortalException(PortalErrorCode.InvalidState, "Screen size " + xmlWidth + "x" + xmlHeight + " could not be checked against the panel (no existing screen, no display attribute, no catalog resolution) and a mismatch makes TIA Portal exit (real project, crash 10); import a screen of the panel's own size first or check the panel in TIA.");
            if (deviceWidth != xmlWidth || deviceHeight != xmlHeight)
                throw new PortalException(PortalErrorCode.InvalidParams, "Screen size " + xmlWidth + "x" + xmlHeight + " in the XML does not match the panel (" + deviceWidth + "x" + deviceHeight + " from " + source
                    + "); TIA Portal V21 exits with NonRecoverableException on such an import (real project, crash 10). Rebuild the screen with width/height " + deviceWidth + "/" + deviceHeight + ".");
        }

        private (int width, int height)? TryReadPanelResolutionFromCatalog(object hmiSoftware)
        {
            try
            {
                // HmiTarget -> DeviceItem (HMI_RT_x) -> its Container (the head item, TypeIdentifier "OrderNumber:6AV2 124-0GC01-0AX0/17.0.0.0")
                object? item = TryGetPropertyValue(hmiSoftware, "Parent");
                string? typeIdentifier = null;
                for (int hop = 0; hop < 4 && item != null && string.IsNullOrEmpty(typeIdentifier); hop++)
                {
                    typeIdentifier = TryGetPropertyValue(item, "TypeIdentifier")?.ToString();
                    if (string.IsNullOrEmpty(typeIdentifier)) item = TryGetPropertyValue(item, "Container") ?? TryGetPropertyValue(item, "Parent");
                }
                if (string.IsNullOrEmpty(typeIdentifier) || _session.CurrentPortal == null) return null;
                var catalog = TryGetPropertyValue(_session.CurrentPortal, "HardwareCatalog");
                if (catalog == null) return null;
                var orderNumber = typeIdentifier!.Replace("OrderNumber:", "").Split('/')[0].Trim();
                foreach (var entry in _session.FindHardwareCatalogEntries(catalog, orderNumber))
                {
                    var entryIdentifier = TryGetPropertyValue(entry, "TypeIdentifier")?.ToString();
                    if (!string.Equals(entryIdentifier, typeIdentifier, StringComparison.OrdinalIgnoreCase)) continue;
                    var description = TryGetPropertyValue(entry, "Description")?.ToString() ?? "";
                    var match = Regex.Match(description, @"(\d{3,4})\s*[x×X]\s*(\d{3,4})");
                    if (match.Success) return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
                }
            }
            catch (Exception) { /* swallow(probe-optional): missing catalog resolution returns null so the screen-size guard refuses an unchecked import */ }
            return null;
        }

        private static IEnumerable<object> EnumerateClassicScreens(object folder, int depth = 0)
        {
            if (depth > 16) yield break;
            if (TryGetPropertyValue(folder, "Screens") is IEnumerable screens)
                foreach (var screen in screens) if (screen != null) yield return screen;
            if (TryGetPropertyValue(folder, "Folders") is IEnumerable folders)
                foreach (var child in folders)
                    if (child != null) foreach (var screen in EnumerateClassicScreens(child, depth + 1)) yield return screen;
        }

        public void ImportHmiTagTable(string softwarePath, string folderPath, string importPath)
        {
            if (_session.IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            try
            {
                var sw = softwareContainer.Software;

                var tagRoot = _session.TryGetHmiTagRoot(sw);

                var group = _session.TryResolveChildGroupByPath(tagRoot, folderPath) ?? tagRoot;

                var tables = TryGetPropertyValue(group, "TagTables");
                if (tables == null)
                {
                    tables = _session.TryGetHmiTagTablesCollection(sw);
                }

                if (tables == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"TagTables collection not found. swType={sw.GetType().FullName} groupType={group.GetType().FullName}");

                if (_session.TryImportEngineeringObjectIntoCollection(tables, importPath, out _, out var err))
                    return;

                throw new PortalException(PortalErrorCode.ImportFailed, err ?? "ImportHmiTagTable failed");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, ex.Message, null, ex);
            }
        }

        public void ImportHmiConnection(string softwarePath, string importPath)
        {
            if (_session.IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            try
            {
                var sw = softwareContainer.Software;
                var connections = TryGetPropertyValue(sw, "Connections");
                if (connections == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"Connections collection not found. swType={sw.GetType().FullName}");

                if (_session.TryImportEngineeringObjectIntoCollection(connections, importPath, out _, out var err))
                    return;

                throw new PortalException(PortalErrorCode.ImportFailed, err ?? "ImportHmiConnection failed");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, ex.Message, null, ex);
            }
        }

        public ResponseImportBatch ImportHmiScreensFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (_session.IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name)) continue;

                    try
                    {
                        ImportHmiScreen(softwarePath, folderPath, file);
                        imported.Add(name);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new ImportFailure { Path = file, Error = ex.ToString() + " Batch stopped; later matching files were not attempted." });
                        break;
                    }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
        }

        public ResponseImportBatch ImportHmiTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (_session.IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name)) continue;

                    try
                    {
                        ImportHmiTagTable(softwarePath, folderPath, file);
                        imported.Add(name);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new ImportFailure { Path = file, Error = ex.ToString() });
                    }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
        }

        #endregion
    }
}
