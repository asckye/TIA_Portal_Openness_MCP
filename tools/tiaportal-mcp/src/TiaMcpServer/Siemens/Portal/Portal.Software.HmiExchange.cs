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
        #region software - HmiExchange

        public List<string>? GetHmiScreens(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;
            return HmiScreenTraversal.ListNames(softwareContainer.Software);
        }

        public List<string>? GetHmiTagTables(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;
            var sw = softwareContainer.Software;
            var tables = TryGetHmiTagTablesCollection(sw);
            if (tables == null) return new List<string>();
            var names = TryListNamesFromCollection(tables, Array.Empty<string>(), "TagTables");
            // 2.7.46: tables inside user folders are listed too (name only; ManageClassicHmiFolder read shows the folder).
            var root = TryGetHmiTagRoot(sw);
            if (root != null)
                foreach (var table in EnumerateHmiTagTablesRecursive(root))
                {
                    var name = TryGetName(table);
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name!, StringComparer.Ordinal)) names.Add(name!);
                }
            return names;
        }

        public List<string>? GetHmiTags(string softwarePath, string tagTableName = "")
        {
            if (IsProjectNull()) return null;
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;

            var sw = softwareContainer.Software;
            var tagRoot = TryGetHmiTagRoot(sw);
            object? tagTable = string.IsNullOrWhiteSpace(tagTableName)
                ? null
                : TryFindHmiTagTable(sw, tagTableName);
            // 2.7.46: an unknown table answered "0 tags, success" - that is a lookup failure, not an empty table.
            if (!string.IsNullOrWhiteSpace(tagTableName) && tagTable == null)
                throw new PortalException(PortalErrorCode.NotFound, "HMI tag table not found: " + tagTableName + " (tables: " + string.Join(", ", GetHmiTagTables(softwarePath) ?? new List<string>()) + ").");

            var root = tagTable ?? tagRoot;
            return TryListNamesFromCollection(root, new[] { "Tags" }, "Tags");
        }

        public List<string>? GetHmiConnections(string softwarePath)
        {
            if (IsProjectNull()) return null;
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return null;
            var sw = softwareContainer.Software;
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) return new List<string>();
            return TryListNamesFromCollection(connections, Array.Empty<string>(), "Connections");
        }

        public JsonObject ExportHmiScreen(string softwarePath, string screenName, string exportPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project is null");
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            var screen = HmiExactAccess.Screen(softwareContainer.Software, screenName);
            if (screen == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI screen not found: {screenName}");

            var export = EngineeringExport.Export(screen, exportPath);
            export["operationSuccess"] = export["success"]?.DeepClone();
            return export;
        }

        public JsonObject ExportHmiTagTable(string softwarePath, string tagTableName, string exportPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project is null");
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            var sw = softwareContainer.Software;
            var table = TryFindHmiTagTable(sw, tagTableName);
            if (table == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI tag table not found: {tagTableName}");

            var export = EngineeringExport.Export(table, exportPath);
            export["operationSuccess"] = export["success"]?.DeepClone();
            return export;
        }

        public JsonObject ExportHmiConnection(string softwarePath, string connectionName, string exportPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "Project is null");
            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            var sw = softwareContainer.Software;
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI Connections collection not found on '{softwarePath}'");

            // 去掉 ?? TryFindByNameInCollection(connections, Array.Empty<string>(), ...)：空 hints 恒返回 null。
            var connection = FindExistingByName(connections, connectionName);
            if (connection == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI connection not found: {connectionName}");

            var export = EngineeringExport.Export(connection, exportPath);
            export["operationSuccess"] = export["success"]?.DeepClone();
            return export;
        }

        public string ProbeClassicHmiConnectionCreation(string softwarePath, string connectionName, string exportPath)
        {
            var sb = new StringBuilder();
            if (IsProjectNull()) return "Project is null";

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return "HMI software not found: " + softwarePath;

            var sw = softwareContainer.Software;
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) return "Connections collection not found. swType=" + sw.GetType().FullName;

            sb.AppendLine("SoftwareType=" + (sw.GetType().FullName ?? sw.GetType().Name));
            sb.AppendLine("ConnectionsType=" + (connections.GetType().FullName ?? connections.GetType().Name));
            sb.AppendLine("ConnectionsPublicMembers:");
            foreach (var line in DescribeTypeMembers(connections.GetType(), false).Take(120))
            {
                sb.AppendLine("  " + line);
            }
            sb.AppendLine("ConnectionsExplicitMembers:");
            foreach (var line in DescribeTypeMembers(connections.GetType(), true).Take(160))
            {
                sb.AppendLine("  " + line);
            }

            var connectionType = FindTypeBySuffix("Siemens.Engineering.Hmi.Communication.Connection")
                ?? FindTypeBySuffix("Hmi.Communication.Connection")
                ?? FindTypeBySuffix("Communication.Connection");
            sb.AppendLine("ConnectionType=" + (connectionType?.FullName ?? "<not found>"));

            // 去掉 ?? TryFindByNameInCollection(connections, Array.Empty<string>(), ...)：空 hints 恒返回 null。
            var existing = FindExistingByName(connections, connectionName);
            if (existing != null)
            {
                sb.AppendLine("ExistingConnection=" + connectionName);
                if (TryExportEngineeringObject(existing, exportPath, out var existingExportErr))
                {
                    sb.AppendLine("ExportExisting=OK :: " + exportPath);
                }
                else
                {
                    sb.AppendLine("ExportExisting=FAIL :: " + existingExportErr);
                }
                return sb.ToString();
            }

            if (connectionType == null)
            {
                sb.AppendLine("Create=SKIP :: connection type not found");
                return sb.ToString();
            }

            sb.AppendLine("CreationInfos:");
            var creationInfos = TryInvokeExplicitEngineeringMethod(connections, "GetCreationInfos", Array.Empty<object?>(), out var creationInfoErr);
            if (creationInfos == null && !string.IsNullOrWhiteSpace(creationInfoErr))
            {
                sb.AppendLine("  GetCreationInfos(\"\") failed: " + creationInfoErr);
            }
            else
            {
                foreach (var line in FormatEnumerableObjects(creationInfos, 80))
                {
                    sb.AppendLine("  " + line);
                }
            }

            object? created = null;
            string? createErr = null;
            var attempts = new[]
            {
                new { Description = "Name only", Parameters = new Dictionary<string, object?> { ["Name"] = connectionName } }
            };

            foreach (var attempt in attempts)
            {
                try
                {
                    sb.AppendLine($"CreateAttempt {attempt.Description}");
                    created = TryInvokeExplicitEngineeringMethod(
                        connections,
                        "Create",
                        new object?[] { connectionType, attempt.Parameters },
                        out createErr);
                    if (created != null)
                    {
                        sb.AppendLine("Create=OK :: type=" + (created.GetType().FullName ?? created.GetType().Name));
                        break;
                    }
                    sb.AppendLine("Create=FAIL :: " + (createErr ?? "<null result>"));
                }
                catch (Exception ex)
                {
                    createErr = FormatExceptionDetail(ex);
                    sb.AppendLine("Create=ERR :: " + createErr);
                }
            }

            // 去掉 ?? TryFindByNameInCollection(connections, Array.Empty<string>(), ...)：空 hints 恒返回 null。
            created ??= FindExistingByName(connections, connectionName);
            if (created == null)
            {
                sb.AppendLine("Readback=FAIL :: connection not found after create attempts");
                return sb.ToString();
            }

            sb.AppendLine("Readback=OK :: " + (TryGetName(created) ?? connectionName));
            if (TryExportEngineeringObject(created, exportPath, out var exportErr))
            {
                sb.AppendLine("ExportCreated=OK :: " + exportPath);
            }
            else
            {
                sb.AppendLine("ExportCreated=FAIL :: " + exportErr);
            }

            return sb.ToString();
        }

        public (List<string> Exported, List<string> Failed)? ExportHmiProgram(string softwarePath, string exportDir, bool exportScreens = true, bool exportTagTables = true)
        {
            if (IsProjectNull()) return null;

            var exported = new List<string>();
            var failed = new List<string>();

            Directory.CreateDirectory(exportDir);

            if (exportScreens)
            {
                var screens = GetHmiScreens(softwarePath) ?? new List<string>();
                foreach (var s in screens)
                {
                    var safe = MakeSafeFileName(s);
                    var outPath = Path.Combine(exportDir, $"screen_{safe}.xml");
                    try { var result = ExportHmiScreen(softwarePath, s, outPath); if (result["success"]!.GetValue<bool>()) exported.Add(outPath); else failed.Add(result.ToJsonString()); }
                    catch (PortalException) { failed.Add($"screen:{s}"); }
                }
            }

            if (exportTagTables)
            {
                var tables = GetHmiTagTables(softwarePath) ?? new List<string>();
                foreach (var t in tables)
                {
                    var safe = MakeSafeFileName(t);
                    var outPath = Path.Combine(exportDir, $"tagtable_{safe}.xml");
                    try { var result = ExportHmiTagTable(softwarePath, t, outPath); if (result["success"]!.GetValue<bool>()) exported.Add(outPath); else failed.Add(result.ToJsonString()); }
                    catch (PortalException) { failed.Add($"tagtable:{t}"); }
                }
            }

            return (exported, failed);
        }

        public void ImportHmiScreen(string softwarePath, string folderPath, string importPath)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            LastImportNotes = null;
            try
            {
                var sw = softwareContainer.Software;
                GuardClassicScreenSize(sw, importPath);

                // Resolve screen folder then groups by folderPath
                object rootGroup = TryGetPropertyValue(sw, "ScreenFolder") ?? sw;
                var group = TryResolveChildGroupByPath(rootGroup, folderPath) ?? rootGroup;

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

                if (TryImportEngineeringObjectIntoCollection(screens, importPath, out _, out var err))
                    return;

                // 2.7.46: panel versions differ in the attributes their screen items accept (real project: a TP700 Comfort V17.0
                // refused the builder's <Visible> on Button with "'set_Visible' is not supported by type '...Button'"). The named
                // attribute is stripped for that item type from a temp copy and the import retried, up to five times; what was
                // stripped is reported in the error text of a final failure and in LastImportNotes on success.
                var stripped = new List<string>();
                var currentPath = importPath;
                for (int attempt = 0; attempt < 5 && err != null; attempt++)
                {
                    var match = Regex.Match(err, @"'set_(\w+)' is not supported by type '([\w.]+)'");
                    if (!match.Success) break;
                    var attribute = match.Groups[1].Value; var typeName = match.Groups[2].Value.Split('.').Last();
                    var document = XDocument.Load(currentPath);
                    var removed = document.Descendants().Where(e => e.Name.LocalName.EndsWith("." + typeName, StringComparison.Ordinal) || e.Name.LocalName == typeName)
                        .SelectMany(e => e.Elements("AttributeList").Elements(attribute)).ToList();
                    if (removed.Count == 0) break;
                    removed.ForEach(e => e.Remove());
                    currentPath = Path.Combine(Path.GetTempPath(), "tia_mcp_hmi_screen_" + Guid.NewGuid().ToString("N") + ".xml");
                    document.Save(currentPath);
                    stripped.Add(typeName + "." + attribute);
                    if (TryImportEngineeringObjectIntoCollection(screens, currentPath, out _, out err)) { LastImportNotes = "Imported after stripping unsupported attributes: " + string.Join(", ", stripped); return; }
                }

                throw new PortalException(PortalErrorCode.ImportFailed, (err ?? "ImportHmiScreen failed") + (stripped.Count > 0 ? " (after stripping " + string.Join(", ", stripped) + ")" : ""));
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

        // 2.7.46: notes of the last ImportHmiScreen (e.g. attributes stripped for the panel version), consumed by the tool wrapper.
        public string? LastImportNotes { get; private set; }

        // 2.7.48 (crash ⑩): importing a classic screen whose Width / Height differ from the panel's display made TIA Portal V21
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
            catch (Exception) { return; }                                              // not a screen document we understand - TIA reports its own error
            if (xmlWidth == null || xmlHeight == null) return;

            int? deviceWidth = null, deviceHeight = null; string source = "";
            try
            {
                var root = TryGetPropertyValue(hmiSoftware, "ScreenFolder");
                var existing = root == null ? null : EnumerateClassicScreens(root).FirstOrDefault();
                if (existing is IEngineeringObject engineeringScreen)
                {
                    deviceWidth = Convert.ToInt32(engineeringScreen.GetAttribute("Width")); deviceHeight = Convert.ToInt32(engineeringScreen.GetAttribute("Height"));
                    source = "existing screen '" + TryGetName(existing) + "'";
                }
            }
            catch (Exception) { deviceWidth = null; }
            if (deviceWidth == null && hmiSoftware is IEngineeringObject software)
            {
                foreach (var pair in new[] { ("ScreenWidth", "ScreenHeight"), ("DisplayWidth", "DisplayHeight"), ("ResolutionWidth", "ResolutionHeight") })
                {
                    try { deviceWidth = Convert.ToInt32(software.GetAttribute(pair.Item1)); deviceHeight = Convert.ToInt32(software.GetAttribute(pair.Item2)); source = "HMI attributes " + pair.Item1 + "/" + pair.Item2; break; }
                    catch (Exception) { deviceWidth = null; }
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
                if (string.IsNullOrEmpty(typeIdentifier) || _portal == null) return null;
                var catalog = TryGetPropertyValue(_portal, "HardwareCatalog");
                if (catalog == null) return null;
                var orderNumber = typeIdentifier!.Replace("OrderNumber:", "").Split('/')[0].Trim();
                foreach (var entry in FindHardwareCatalogEntries(catalog, orderNumber))
                {
                    var entryIdentifier = TryGetPropertyValue(entry, "TypeIdentifier")?.ToString();
                    if (!string.Equals(entryIdentifier, typeIdentifier, StringComparison.OrdinalIgnoreCase)) continue;
                    var description = TryGetPropertyValue(entry, "Description")?.ToString() ?? "";
                    var match = Regex.Match(description, @"(\d{3,4})\s*[x×X]\s*(\d{3,4})");
                    if (match.Success) return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
                }
            }
            catch (Exception) { }
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
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            try
            {
                var sw = softwareContainer.Software;

                var tagRoot = TryGetHmiTagRoot(sw);

                var group = TryResolveChildGroupByPath(tagRoot, folderPath) ?? tagRoot;

                var tables = TryGetPropertyValue(group, "TagTables");
                if (tables == null)
                {
                    tables = TryGetHmiTagTablesCollection(sw);
                }

                if (tables == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"TagTables collection not found. swType={sw.GetType().FullName} groupType={group.GetType().FullName}");

                if (TryImportEngineeringObjectIntoCollection(tables, importPath, out _, out var err))
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
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) throw new PortalException(PortalErrorCode.NotFound, $"HMI software not found: {softwarePath}");

            try
            {
                var sw = softwareContainer.Software;
                var connections = TryGetPropertyValue(sw, "Connections");
                if (connections == null)
                    throw new PortalException(PortalErrorCode.NotFound, $"Connections collection not found. swType={sw.GetType().FullName}");

                if (TryImportEngineeringObjectIntoCollection(connections, importPath, out _, out var err))
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
                if (IsProjectNull())
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

        public ResponseImportBatch ImportHmiTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (IsProjectNull())
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
