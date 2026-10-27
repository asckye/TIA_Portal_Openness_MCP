using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Globalization;
using Siemens.Engineering.Hmi.RuntimeScripting;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Reflection;
using TiaMcpServer.ModelContextProtocol;
using static TiaMcp.Adapters.Native.Hmi.HmiAccessHelpers;
#if HMI_UNIFIED
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiTags;
#endif
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
using System.Xml.Linq;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal sealed class HmiExchangeAdapter
    {
        private readonly HmiSession _session;

        public HmiExchangeAdapter(HmiSession session) => _session = session;

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
                throw new PlcSoftwareException("NotFound", "HMI tag table not found: " + tagTableName + " (tables: " + string.Join(", ", GetHmiTagTables(softwarePath) ?? new List<string>()) + ").");

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

        public HmiObject ExportHmiScreen(string softwarePath, string screenName, string exportPath, string exportOptions = "WithDefaults")
        {
            if (_session.IsProjectNull()) throw new PlcSoftwareException("InvalidState", "Project is null");
            HmiExchangePolicy.ExportOption(exportOptions);
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PlcSoftwareException("NotFound", $"HMI software not found: {softwarePath}");

            HmiExchangePolicy.RequireClassic(softwareContainer.Software);

            var screen = HmiExactAccess.Screen(softwareContainer.Software, screenName);
            if (screen == null) throw new PlcSoftwareException("NotFound", $"HMI screen not found: {screenName}");

            var export = HmiFiles.Export(screen, exportPath, exportOptions);
            export["operationSuccess"] = export["success"]?.DeepClone();
            return export;
        }

        public HmiObject ExportHmiTagTable(string softwarePath, string tagTableName, string exportPath, string exportOptions = "WithDefaults")
        {
            if (_session.IsProjectNull()) throw new PlcSoftwareException("InvalidState", "Project is null");
            HmiExchangePolicy.ExportOption(exportOptions);
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PlcSoftwareException("NotFound", $"HMI software not found: {softwarePath}");

            HmiExchangePolicy.RequireClassic(softwareContainer.Software);

            var sw = softwareContainer.Software;
            var table = _session.TryFindHmiTagTable(sw, tagTableName);
            if (table == null) throw new PlcSoftwareException("NotFound", $"HMI tag table not found: {tagTableName}");

            var export = HmiFiles.Export(table, exportPath, exportOptions);
            export["operationSuccess"] = export["success"]?.DeepClone();
            return export;
        }

        public HmiObject ExportHmiConnection(string softwarePath, string connectionName, string exportPath, string exportOptions = "WithDefaults")
        {
            if (_session.IsProjectNull()) throw new PlcSoftwareException("InvalidState", "Project is null");
            HmiExchangePolicy.ExportOption(exportOptions);
            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PlcSoftwareException("NotFound", $"HMI software not found: {softwarePath}");

            HmiExchangePolicy.RequireClassic(softwareContainer.Software);

            var sw = softwareContainer.Software;
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) throw new PlcSoftwareException("NotFound", $"HMI Connections collection not found on '{softwarePath}'");

            var connection = _session.FindExistingByName(connections, connectionName);
            if (connection == null) throw new PlcSoftwareException("NotFound", $"HMI connection not found: {connectionName}");

            var export = HmiFiles.Export(connection, exportPath, exportOptions);
            export["operationSuccess"] = export["success"]?.DeepClone();
            return export;
        }

        public HmiExportProgram? ExportHmiProgram(string softwarePath, string exportDir, bool exportScreens = true, bool exportTagTables = true, string exportOptions = "WithDefaults")
        {
            if (_session.IsProjectNull()) return null;

            var exported = new List<string>();
            var failed = new List<string>();
            var items = new HmiArray();

            HmiExchangePolicy.ExportOption(exportOptions);
            HmiExchangePolicy.RequireClassic(_session.ResolveHmiSoftwareOrThrow(softwarePath));
            Directory.CreateDirectory(exportDir);

            if (exportScreens)
            {
                var screens = GetHmiScreens(softwarePath) ?? new List<string>();
                foreach (var s in screens)
                {
                    var safe = _session.MakeSafeFileName(s);
                    var outPath = Path.Combine(exportDir, $"screen_{safe}.xml");
                    try { var result = ExportHmiScreen(softwarePath, s, outPath, exportOptions); if (result["success"]!.GetValue<bool>()) exported.Add(outPath); else failed.Add(result.ToJsonString()); items.Add(new HmiObject { ["target"] = "screen:" + s, ["evidence"] = result.DeepClone() }); }
                    catch (PlcSoftwareException) { /* swallow(native-fallback): batch export records the failed object and continues with remaining entries */ failed.Add($"screen:{s}"); items.Add(new HmiObject { ["target"] = "screen:" + s, ["evidence"] = new HmiObject { ["operationSuccess"] = false } }); }
                }
            }

            if (exportTagTables)
            {
                var tables = GetHmiTagTables(softwarePath) ?? new List<string>();
                foreach (var t in tables)
                {
                    var safe = _session.MakeSafeFileName(t);
                    var outPath = Path.Combine(exportDir, $"tagtable_{safe}.xml");
                    try { var result = ExportHmiTagTable(softwarePath, t, outPath, exportOptions); if (result["success"]!.GetValue<bool>()) exported.Add(outPath); else failed.Add(result.ToJsonString()); items.Add(new HmiObject { ["target"] = "tagtable:" + t, ["evidence"] = result.DeepClone() }); }
                    catch (PlcSoftwareException) { /* swallow(native-fallback): batch export records the failed object and continues with remaining entries */ failed.Add($"tagtable:{t}"); items.Add(new HmiObject { ["target"] = "tagtable:" + t, ["evidence"] = new HmiObject { ["operationSuccess"] = false } }); }
                }
            }

            return new HmiExportProgram { Exported = exported, Failed = failed, Items = items };
        }

        public void ImportHmiScreen(string softwarePath, string folderPath, string importPath, bool overwrite = false)
        {
            if (_session.IsProjectNull()) throw new PlcSoftwareException("InvalidState", "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PlcSoftwareException("NotFound", $"HMI software not found: {softwarePath}");

            LastImportNotes = null;
            HmiExchangePolicy.RequireClassic(softwareContainer.Software);
            try
            {
                var sw = softwareContainer.Software;
                GuardClassicScreenSize(sw, importPath);

                object rootGroup = TryGetPropertyValue(sw, "ScreenFolder")
                    ?? throw new PlcSoftwareException("NotFound", "ScreenFolder unavailable.");
                var group = HmiExchangePolicy.Folder(rootGroup, folderPath);
                var screens = TryGetPropertyValue(group, "Screens")
                    ?? throw new PlcSoftwareException("NotFound", "Screens collection unavailable: " + folderPath);
                HmiExchangePolicy.BeforeImport(screens, importPath, "Hmi.Screen.Screen", overwrite);

                if (_session.TryImportEngineeringObjectIntoCollection(screens, importPath, out _, out var err, overwrite))
                    return;

                // A failed native call can already have modified the project. Its error text is not
                // proof of rollback: never strip attributes and replay an import after failure.
                throw new PlcSoftwareException("ImportFailed", (err ?? "ImportHmiScreen failed")
                    + " The project may have changed. No automatic retry was attempted; inspect the project before another import.");
            }
            catch (PlcSoftwareException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PlcSoftwareException("ImportFailed", ex.Message, null, ex);
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
#if HMI_UNIFIED
            if (hmiSoftware is global::Siemens.Engineering.HmiUnified.HmiSoftware) return;       // Unified screens scale; the crash is a classic-panel behaviour
#endif
            int? xmlWidth = null, xmlHeight = null;
            try
            {
                var document = XDocument.Load(importPath);
                var screen = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Screen.Screen");
                var attributes = screen?.Element("AttributeList");
                if (int.TryParse(attributes?.Element("Width")?.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var w)) xmlWidth = w;
                if (int.TryParse(attributes?.Element("Height")?.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var h)) xmlHeight = h;
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
                // carries the display ("7" TFT 鏄剧ず灞忥紝800 x 480 鍍忕礌").
                var resolution = TryReadPanelResolutionFromCatalog(hmiSoftware);
                if (resolution != null) { deviceWidth = resolution.Value.width; deviceHeight = resolution.Value.height; source = "hardware catalog description"; }
            }
            if (deviceWidth == null)
                throw new PlcSoftwareException("InvalidState", "Screen size " + xmlWidth + "x" + xmlHeight + " could not be checked against the panel (no existing screen, no display attribute, no catalog resolution) and a mismatch makes TIA Portal exit (real project, crash 10); import a screen of the panel's own size first or check the panel in TIA.");
            if (deviceWidth != xmlWidth || deviceHeight != xmlHeight)
                throw new PlcSoftwareException("InvalidParams", "Screen size " + xmlWidth + "x" + xmlHeight + " in the XML does not match the panel (" + deviceWidth + "x" + deviceHeight + " from " + source
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
                    if (match.Success) return (int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
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

        public void ImportHmiTagTable(string softwarePath, string folderPath, string importPath, bool overwrite = false)
        {
            if (_session.IsProjectNull()) throw new PlcSoftwareException("InvalidState", "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PlcSoftwareException("NotFound", $"HMI software not found: {softwarePath}");

            HmiExchangePolicy.RequireClassic(softwareContainer.Software);
            try
            {
                var sw = softwareContainer.Software;

                var tagRoot = _session.TryGetHmiTagRoot(sw);
                var group = HmiExchangePolicy.Folder(tagRoot, folderPath);
                var tables = TryGetPropertyValue(group, "TagTables")
                    ?? throw new PlcSoftwareException("NotFound", "TagTables collection unavailable: " + folderPath);
                HmiExchangePolicy.BeforeImport(tables, importPath, "Hmi.Tag.TagTable", overwrite);

                if (_session.TryImportEngineeringObjectIntoCollection(tables, importPath, out _, out var err, overwrite))
                    return;

                throw new PlcSoftwareException("ImportFailed", err ?? "ImportHmiTagTable failed");
            }
            catch (PlcSoftwareException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PlcSoftwareException("ImportFailed", ex.Message, null, ex);
            }
        }

        public void ImportHmiConnection(string softwarePath, string importPath, bool overwrite = false)
        {
            if (_session.IsProjectNull()) throw new PlcSoftwareException("InvalidState", "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

            var softwareContainer = _session.GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PlcSoftwareException("NotFound", $"HMI software not found: {softwarePath}");

            HmiExchangePolicy.RequireClassic(softwareContainer.Software);
            try
            {
                var sw = softwareContainer.Software;
                var connections = TryGetPropertyValue(sw, "Connections");
                if (connections == null)
                    throw new PlcSoftwareException("NotFound", $"Connections collection not found. swType={sw.GetType().FullName}");

                HmiExchangePolicy.BeforeImport(connections, importPath, "Hmi.Communication.Connection", overwrite);

                if (_session.TryImportEngineeringObjectIntoCollection(connections, importPath, out _, out var err, overwrite))
                    return;

                throw new PlcSoftwareException("ImportFailed", err ?? "ImportHmiConnection failed");
            }
            catch (PlcSoftwareException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PlcSoftwareException("ImportFailed", ex.Message, null, ex);
            }
        }

        private static HmiObject ImportItem(string target, bool success, Exception? exception = null, string? status = null)
        {
            var evidence = new HmiObject { ["operationSuccess"] = success };
            if (status != null) evidence["status"] = status;
            if (exception is PlcSoftwareException portal && (portal.Code == "InvalidState" || portal.Code == "NotFound"))
                evidence["status"] = portal.Code == "InvalidState" && portal.Message.StartsWith("HMI import target already exists:", StringComparison.Ordinal)
                    ? "ImportTargetExists" : portal.Code.ToString();
            if (exception != null) evidence["exceptionType"] = exception.GetType().Name;
            return new HmiObject { ["target"] = target, ["evidence"] = evidence };
        }

        public HmiImportBatch ImportHmiScreensFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = false)
        {
            if (!_session.IsProjectNull()) HmiExchangePolicy.RequireClassic(_session.ResolveHmiSoftwareOrThrow(softwarePath));
            var imported = new List<string>();
            var failed = new List<HmiImportFailure>();
            var items = new HmiArray();

            try
            {
                if (_session.IsProjectNull())
                {
                    failed.Add(new HmiImportFailure { Path = dir, Error = "Project is null" });
                    items.Add(ImportItem(dir, false, status: "InvalidState"));
                    return new HmiImportBatch { Imported = imported, Failed = failed, Meta = HmiMeta.Basic( failed.Count == 0, ("items", items)) };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new HmiImportFailure { Path = dir, Error = "Directory not found" });
                    items.Add(ImportItem(dir, false, status: "NotFound"));
                    return new HmiImportBatch { Imported = imported, Failed = failed, Meta = HmiMeta.Basic( failed.Count == 0, ("items", items)) };
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
                        ImportHmiScreen(softwarePath, folderPath, file, overwrite);
                        imported.Add(name);
                        items.Add(ImportItem(file, true));
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new HmiImportFailure { Path = file, Error = ex.ToString() + " Batch stopped; later matching files were not attempted." });
                        items.Add(ImportItem(file, false, ex));
                        ((HmiObject)items[items.Count - 1]!.Field("evidence")!)["batchStopped"] = true;
                        break;
                    }
                }

                return new HmiImportBatch { Imported = imported, Failed = failed, Meta = HmiMeta.Basic( failed.Count == 0, ("items", items)) };
            }
            catch (Exception ex)
            {
                failed.Add(new HmiImportFailure { Path = dir, Error = ex.ToString() });
                items.Add(ImportItem(dir, false, ex));
                return new HmiImportBatch { Imported = imported, Failed = failed, Meta = HmiMeta.Basic( failed.Count == 0, ("items", items)) };
            }
        }

        public HmiImportBatch ImportHmiTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = false)
        {
            if (!_session.IsProjectNull()) HmiExchangePolicy.RequireClassic(_session.ResolveHmiSoftwareOrThrow(softwarePath));
            var imported = new List<string>();
            var failed = new List<HmiImportFailure>();
            var items = new HmiArray();

            try
            {
                if (_session.IsProjectNull())
                {
                    failed.Add(new HmiImportFailure { Path = dir, Error = "Project is null" });
                    items.Add(ImportItem(dir, false, status: "InvalidState"));
                    return new HmiImportBatch { Imported = imported, Failed = failed, Meta = HmiMeta.Basic( failed.Count == 0, ("items", items)) };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new HmiImportFailure { Path = dir, Error = "Directory not found" });
                    items.Add(ImportItem(dir, false, status: "NotFound"));
                    return new HmiImportBatch { Imported = imported, Failed = failed, Meta = HmiMeta.Basic( failed.Count == 0, ("items", items)) };
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
                        ImportHmiTagTable(softwarePath, folderPath, file, overwrite);
                        imported.Add(name);
                        items.Add(ImportItem(file, true));
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new HmiImportFailure { Path = file, Error = ex.ToString() });
                        items.Add(ImportItem(file, false, ex));
                    }
                }

                return new HmiImportBatch { Imported = imported, Failed = failed, Meta = HmiMeta.Basic( failed.Count == 0, ("items", items)) };
            }
            catch (Exception ex)
            {
                failed.Add(new HmiImportFailure { Path = dir, Error = ex.ToString() });
                items.Add(ImportItem(dir, false, ex));
                return new HmiImportBatch { Imported = imported, Failed = failed, Meta = HmiMeta.Basic( failed.Count == 0, ("items", items)) };
            }
        }

        #endregion
    }
}
