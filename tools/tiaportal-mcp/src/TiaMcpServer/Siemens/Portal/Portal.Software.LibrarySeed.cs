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
        #region software - LibrarySeed

        public ModelContextProtocol.ResponseGlobalLibraryProbe ProbeGlobalLibrary(string libraryPath, int maxItems = 500)
        {
            var warnings = new List<string>();
            var raw = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["inputPath"] = libraryPath
            };

            try
            {
                if (_portal == null)
                {
                    return new ModelContextProtocol.ResponseGlobalLibraryProbe
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        Error = "TIA Portal is not connected. Call Connect first.",
                        Warnings = new[] { "This is a read-only probe and does not import library content." },
                        Raw = raw
                    };
                }

                var resolved = ResolveGlobalLibraryFile(libraryPath);
                raw["resolvedLibraryFile"] = resolved ?? "";
                if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                {
                    return new ModelContextProtocol.ResponseGlobalLibraryProbe
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        Error = "Global library .al file not found.",
                        Warnings = new[] { "Pass either the .al21 file path or its containing folder." },
                        Raw = raw
                    };
                }

                var globalLibraries = TryGetPropertyValue(_portal, "GlobalLibraries");
                raw["globalLibrariesType"] = globalLibraries?.GetType().FullName ?? "";
                if (globalLibraries == null)
                {
                    return new ModelContextProtocol.ResponseGlobalLibraryProbe
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        ResolvedLibraryFile = resolved,
                        Error = "TiaPortal.GlobalLibraries property not found.",
                        Warnings = new[] { "Installed Openness API may not expose global library access through this build." },
                        Raw = raw
                    };
                }

                // 2.7.46: a library the user (or ManageGlobalLibrary) already opened is reused and NOT closed afterwards - the probe
                // closed the maintainer's open library on the real project.
                bool wasAlreadyOpen = false;
                object? library = FindOpenGlobalLibraryByFile(globalLibraries, resolved!);
                string? openError = null;
                if (library != null) wasAlreadyOpen = true; else library = TryOpenGlobalLibrary(globalLibraries, resolved!, out openError);
                if (library == null)
                {
                    return new ModelContextProtocol.ResponseGlobalLibraryProbe
                    {
                        Ok = false,
                        LibraryPath = libraryPath,
                        ResolvedLibraryFile = resolved,
                        Error = openError ?? "Failed to open global library.",
                        Warnings = new[] { "No write operation was attempted." },
                        Raw = raw
                    };
                }

                var memberList = DescribeMembers(library, 300)
                    .Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")
                    .Take(Math.Max(10, Math.Min(1000, maxItems)))
                    .ToList();
                var masterCopies = ListLibraryNamesByHints(library, Math.Max(1, maxItems), "MasterCopies", "MasterCopyFolder", "MasterCopyFolders", "MasterCopyGroups", "Folders");
                var types = ListLibraryNamesByHints(library, Math.Max(1, maxItems), "Types", "TypeFolder", "TypeFolders", "LibraryTypes", "Folders");
                var folders = ListLibraryNamesByHints(library, Math.Max(1, maxItems), "Folders", "Groups", "MasterCopyFolders", "TypeFolders");

                raw["libraryType"] = library.GetType().FullName ?? library.GetType().Name;
                raw["memberCount"] = memberList.Count;
                raw["masterCopyCount"] = masterCopies.Count;
                raw["typeCount"] = types.Count;
                raw["folderCount"] = folders.Count;

                if (!wasAlreadyOpen) TryCloseOrDispose(library); else warnings.Add("Library was already open; it stays open.");

                warnings.Add("This probe only opens and lists library metadata; it does not import master copies or library types into a project.");
                if (masterCopies.Count == 0 && types.Count == 0)
                {
                    warnings.Add("No master copies/types were listed through public/reflection access; use DescribeObject/DescribeObjectProperty for deeper API discovery.");
                }

                return new ModelContextProtocol.ResponseGlobalLibraryProbe
                {
                    Ok = true,
                    Message = "Global library read-only probe completed",
                    LibraryPath = libraryPath,
                    ResolvedLibraryFile = resolved,
                    LibraryType = library.GetType().FullName ?? library.GetType().Name,
                    Members = memberList,
                    MasterCopies = masterCopies,
                    Types = types,
                    Folders = folders,
                    Warnings = warnings,
                    Raw = raw
                };
            }
            catch (Exception ex)
            {
                raw["error"] = ex.ToString();
                return new ModelContextProtocol.ResponseGlobalLibraryProbe
                {
                    Ok = false,
                    Message = ex.Message,
                    LibraryPath = libraryPath,
                    Error = ex.ToString(),
                    Warnings = warnings,
                    Raw = raw
                };
            }
        }

        public ModelContextProtocol.ResponseGlobalLibraryImport ImportMasterCopyFromGlobalLibrary(
            string libraryPath,
            string masterCopyName,
            string hmiSoftwarePath,
            string screenName,
            string importedItemName = "",
            int left = 0,
            int top = 0)
        {
            var attempts = new List<string>();
            var warnings = new List<string>();
            var raw = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("O"),
                ["inputPath"] = libraryPath,
                ["masterCopyName"] = masterCopyName,
                ["hmiSoftwarePath"] = hmiSoftwarePath,
                ["screenName"] = screenName,
                ["left"] = left,
                ["top"] = top
            };

            try
            {
                if (_portal == null)
                {
                    return GlobalLibraryImportFailure("TIA Portal is not connected. Call Connect first.");
                }

                if (IsProjectNull())
                {
                    return GlobalLibraryImportFailure("Project is null. Open or attach a temporary project first.");
                }

                if (string.IsNullOrWhiteSpace(masterCopyName))
                {
                    return GlobalLibraryImportFailure("masterCopyName is required and must come from ProbeGlobalLibrary readback.");
                }

                var resolved = ResolveGlobalLibraryFile(libraryPath);
                raw["resolvedLibraryFile"] = resolved ?? "";
                if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                {
                    return GlobalLibraryImportFailure("Global library .al file not found.");
                }

                var globalLibraries = TryGetPropertyValue(_portal, "GlobalLibraries");
                raw["globalLibrariesType"] = globalLibraries?.GetType().FullName ?? "";
                if (globalLibraries == null)
                {
                    return GlobalLibraryImportFailure("TiaPortal.GlobalLibraries property not found.");
                }

                var screen = ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
                var screenItems = TryGetPropertyValue(screen, "ScreenItems");
                if (screenItems == null)
                {
                    return GlobalLibraryImportFailure("Target screen has no ScreenItems collection.");
                }

                var before = ListNamedChildren(screenItems, 200);
                raw["screenItemsBefore"] = ToJsonArray(before);

                // 2.7.46: a library the user (or ManageGlobalLibrary) already opened is reused and NOT closed afterwards - the probe
                // closed the maintainer's open library on the real project.
                bool wasAlreadyOpen = false;
                object? library = FindOpenGlobalLibraryByFile(globalLibraries, resolved!);
                string? openError = null;
                if (library != null) wasAlreadyOpen = true; else library = TryOpenGlobalLibrary(globalLibraries, resolved!, out openError);
                if (library == null)
                {
                    return GlobalLibraryImportFailure(openError ?? "Failed to open global library.");
                }

                try
                {
                    var masterCopy = FindLibraryObjectByPathOrName(library, masterCopyName, attempts, "MasterCopies", "MasterCopyFolder", "MasterCopyFolders", "MasterCopyGroups", "Folders");
                    if (masterCopy == null)
                    {
                        raw["libraryMembers"] = string.Join(" | ", DescribeMembers(library, 160).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"));
                        return GlobalLibraryImportFailure("MasterCopy was not found by exact/suffix path or name.");
                    }

                    raw["masterCopyType"] = masterCopy.GetType().FullName ?? masterCopy.GetType().Name;
                    raw["masterCopyMembers"] = new JsonArray(DescribeMembers(masterCopy, 240)
                        .Select(m => JsonValue.Create($"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}"))
                        .ToArray());
                    raw["masterCopyAttributes"] = ToJsonArray(TryReadInterestingAttributes(masterCopy));
                    var expectedName = string.IsNullOrWhiteSpace(importedItemName)
                        ? LastPathSegment(masterCopyName)
                        : importedItemName.Trim();

                    object? imported = TryImportMasterCopyIntoScreen(screen, screenItems, masterCopy, expectedName, left, top, attempts);
                    if (imported != null)
                    {
                        TrySetProperty(imported, "Left", left);
                        TrySetProperty(imported, "Top", top);
                        if (!string.IsNullOrWhiteSpace(expectedName))
                            TrySetProperty(imported, "Name", expectedName);
                    }

                    var after = ListNamedChildren(screenItems, 500);
                    raw["screenItemsAfter"] = ToJsonArray(after);
                    var readbackName = ResolveImportedReadbackName(before, after, expectedName);
                    var ok = !string.IsNullOrWhiteSpace(readbackName);
                    if (!ok)
                    {
                        warnings.Add("Import attempts finished, but the target screen item was not visible in readback.");
                        if (!attempts.Any(x => x.StartsWith("Try ", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Try source ", StringComparison.OrdinalIgnoreCase)))
                        {
                            warnings.Add("No compatible public Openness import/copy method was found. The target ScreenItems collection exposed only create-style methods, and the MasterCopy object exposed no copy/instantiate method.");
                        }
                    }

                    return new ModelContextProtocol.ResponseGlobalLibraryImport
                    {
                        Ok = ok,
                        Message = ok
                            ? "Global library MasterCopy imported and read back from target screen"
                            : "Global library MasterCopy import did not produce screen-item readback evidence",
                        LibraryPath = libraryPath,
                        ResolvedLibraryFile = resolved,
                        MasterCopyName = masterCopyName,
                        HmiSoftwarePath = hmiSoftwarePath,
                        ScreenName = screenName,
                        ImportedItemName = readbackName ?? expectedName,
                        Attempts = attempts,
                        ReadbackItems = after,
                        Warnings = warnings,
                        Error = ok ? null : "No matching/new ScreenItems readback after import.",
                        Raw = raw
                    };
                }
                finally
                {
                    if (!wasAlreadyOpen) TryCloseOrDispose(library);
                }
            }
            catch (Exception ex)
            {
                return GlobalLibraryImportFailure(FormatExceptionDetail(ex));
            }

            ModelContextProtocol.ResponseGlobalLibraryImport GlobalLibraryImportFailure(string error)
            {
                return new ModelContextProtocol.ResponseGlobalLibraryImport
                {
                    Ok = false,
                    Message = "Global library MasterCopy import failed",
                    LibraryPath = libraryPath,
                    ResolvedLibraryFile = raw["resolvedLibraryFile"]?.ToString(),
                    MasterCopyName = masterCopyName,
                    HmiSoftwarePath = hmiSoftwarePath,
                    ScreenName = screenName,
                    ImportedItemName = importedItemName,
                    Attempts = attempts,
                    ReadbackItems = Array.Empty<string>(),
                    Warnings = warnings,
                    Error = error,
                    Raw = raw
                };
            }
        }

        public ResponseSeed SeedProjectFromReference(
            string plcSoftwarePath,
            string hmiSoftwarePath,
            string referenceDir,
            JsonObject? placeholders = null)
        {
            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            placeholders ??= new JsonObject();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = referenceDir, Error = "Project is null" });
                    return new ResponseSeed { Imported = imported, Failed = failed, Placeholders = placeholders };
                }

                if (string.IsNullOrWhiteSpace(referenceDir) || !Directory.Exists(referenceDir))
                {
                    failed.Add(new ImportFailure { Path = referenceDir, Error = "Reference directory not found" });
                    return new ResponseSeed { Imported = imported, Failed = failed, Placeholders = placeholders };
                }

                var manifestPath = Path.Combine(referenceDir, "manifest.json");
                JsonObject? manifest = null;
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new ImportFailure { Path = manifestPath, Error = $"Failed to parse manifest.json: {ex.Message}" });
                    }
                }

                string plcBlocksDir = Path.Combine(referenceDir, "plc", "blocks");
                string plcTypesDir = Path.Combine(referenceDir, "plc", "types");
                string hmiScreensDir = Path.Combine(referenceDir, "hmi", "screens");
                string hmiTagsDir = Path.Combine(referenceDir, "hmi", "tags");

                var plcBlockGroupPath = manifest?["plcBlockGroupPath"]?.ToString() ?? "";
                var plcTypeGroupPath = manifest?["plcTypeGroupPath"]?.ToString() ?? "";
                var hmiScreenFolderPath = manifest?["hmiScreenFolderPath"]?.ToString() ?? "";
                var hmiTagTableFolderPath = manifest?["hmiTagTableFolderPath"]?.ToString() ?? "";

                if (manifest?["plcBlocksDir"] != null) plcBlocksDir = Path.Combine(referenceDir, manifest["plcBlocksDir"]!.ToString());
                if (manifest?["plcTypesDir"] != null) plcTypesDir = Path.Combine(referenceDir, manifest["plcTypesDir"]!.ToString());
                if (manifest?["hmiScreensDir"] != null) hmiScreensDir = Path.Combine(referenceDir, manifest["hmiScreensDir"]!.ToString());
                if (manifest?["hmiTagTablesDir"] != null) hmiTagsDir = Path.Combine(referenceDir, manifest["hmiTagTablesDir"]!.ToString());

                var tempDir = Path.Combine(Path.GetTempPath(), "tia-seed-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                void CopyDirWithReplace(string srcDir, string dstDir)
                {
                    if (!Directory.Exists(srcDir)) return;
                    Directory.CreateDirectory(dstDir);

                    foreach (var file in Directory.EnumerateFiles(srcDir, "*.xml", SearchOption.TopDirectoryOnly))
                    {
                        var text = File.ReadAllText(file, Encoding.UTF8);
                        foreach (var kv in placeholders)
                        {
                            var k = kv.Key;
                            var v = kv.Value?.ToString() ?? "";
                            text = text.Replace("{{" + k + "}}", v);
                        }
                        var outPath = Path.Combine(dstDir, Path.GetFileName(file));
                        File.WriteAllText(outPath, text, Encoding.UTF8);
                    }
                }

                var tempPlcBlocks = Path.Combine(tempDir, "plc", "blocks");
                var tempPlcTypes = Path.Combine(tempDir, "plc", "types");
                var tempHmiScreens = Path.Combine(tempDir, "hmi", "screens");
                var tempHmiTags = Path.Combine(tempDir, "hmi", "tags");

                CopyDirWithReplace(plcBlocksDir, tempPlcBlocks);
                CopyDirWithReplace(plcTypesDir, tempPlcTypes);
                CopyDirWithReplace(hmiScreensDir, tempHmiScreens);
                CopyDirWithReplace(hmiTagsDir, tempHmiTags);

                // PLC blocks
                if (Directory.Exists(tempPlcBlocks))
                {
                    var r = ImportBlocksFromDirectory(plcSoftwarePath, plcBlockGroupPath, tempPlcBlocks, "", overwrite: true);
                    imported.AddRange(r.Imported?.Select(x => "plc:block:" + x) ?? Array.Empty<string>());
                    failed.AddRange(r.Failed?.Select(x => new ImportFailure { Path = x.Path, Error = "plc:block:" + x.Error }) ?? Array.Empty<ImportFailure>());
                }

                // PLC types (UDT)
                if (Directory.Exists(tempPlcTypes))
                {
                    foreach (var file in Directory.EnumerateFiles(tempPlcTypes, "*.xml", SearchOption.TopDirectoryOnly))
                    {
                        var ok = ImportType(plcSoftwarePath, plcTypeGroupPath, file);
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (ok) imported.Add("plc:type:" + name);
                        else failed.Add(new ImportFailure { Path = file, Error = "plc:type:Import failed" });
                    }
                }

                // HMI tag tables then screens
                if (Directory.Exists(tempHmiTags))
                {
                    var r = ImportHmiTagTablesFromDirectory(hmiSoftwarePath, hmiTagTableFolderPath, tempHmiTags);
                    imported.AddRange(r.Imported?.Select(x => "hmi:tagtable:" + x) ?? Array.Empty<string>());
                    failed.AddRange(r.Failed?.Select(x => new ImportFailure { Path = x.Path, Error = "hmi:tagtable:" + x.Error }) ?? Array.Empty<ImportFailure>());
                }

                if (Directory.Exists(tempHmiScreens))
                {
                    var r = ImportHmiScreensFromDirectory(hmiSoftwarePath, hmiScreenFolderPath, tempHmiScreens);
                    imported.AddRange(r.Imported?.Select(x => "hmi:screen:" + x) ?? Array.Empty<string>());
                    failed.AddRange(r.Failed?.Select(x => new ImportFailure { Path = x.Path, Error = "hmi:screen:" + x.Error }) ?? Array.Empty<ImportFailure>());
                }

                return new ResponseSeed
                {
                    Message = $"Seed applied from '{referenceDir}'",
                    Imported = imported,
                    Failed = failed,
                    Placeholders = placeholders,
                    TempDir = tempDir,
                };
            }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = referenceDir, Error = ex.ToString() });
                return new ResponseSeed { Imported = imported, Failed = failed, Placeholders = placeholders };
            }
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static string? ResolveGlobalLibraryFile(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath)) return null;
            var p = libraryPath.Trim().Trim('"');
            if (File.Exists(p)) return p;
            if (!Directory.Exists(p)) return null;

            var direct = Directory.EnumerateFiles(p, "*.al*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(x => x.EndsWith(".al21", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x)
                .FirstOrDefault();
            if (direct != null) return direct;

            return Directory.EnumerateFiles(p, "*.al*", SearchOption.AllDirectories)
                .OrderByDescending(x => x.EndsWith(".al21", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x)
                .FirstOrDefault();
        }

        private static object? FindOpenGlobalLibraryByFile(object globalLibraries, string libraryFile)
        {
            try
            {
                if (globalLibraries is not IEnumerable open) return null;
                foreach (var candidate in open)
                {
                    var path = TryGetPropertyValue(candidate, "Path");
                    var full = path is FileInfo fi ? fi.FullName : path?.ToString();
                    if (!string.IsNullOrEmpty(full) && string.Equals(Path.GetFullPath(full!), Path.GetFullPath(libraryFile), StringComparison.OrdinalIgnoreCase)) return candidate;
                }
            }
            catch { }
            return null;
        }

        private static object? TryOpenGlobalLibrary(object globalLibraries, string libraryFile, out string? error)
        {
            error = null;
            var fi = new FileInfo(libraryFile);
            try
            {
                var methods = globalLibraries.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => string.Equals(m.Name, "Open", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var m in methods)
                {
                    var ps = m.GetParameters();
                    try
                    {
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(FileInfo))
                            return m.Invoke(globalLibraries, new object[] { fi });

                        if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                            return m.Invoke(globalLibraries, new object[] { libraryFile });

                        if (ps.Length == 2 && ps[0].ParameterType == typeof(FileInfo))
                        {
                            var arg2 = BuildDefaultArgument(ps[1].ParameterType);
                            return m.Invoke(globalLibraries, new[] { (object)fi, arg2 });
                        }

                        if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                        {
                            var arg2 = BuildDefaultArgument(ps[1].ParameterType);
                            return m.Invoke(globalLibraries, new[] { (object)libraryFile, arg2 });
                        }
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        error = $"{m.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                    }
                    catch (Exception ex)
                    {
                        error = $"{m.Name}: {ex.GetType().FullName}: {ex.Message}";
                    }
                }

                error ??= "No supported GlobalLibraries.Open overload accepted FileInfo/string path.";
                return null;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return null;
            }
        }

        private static object? BuildDefaultArgument(Type type)
        {
            if (type == typeof(bool)) return false;
            if (type == typeof(int)) return 0;
            if (type == typeof(string)) return "";
            if (type.IsEnum) return Enum.ToObject(type, 0);
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static List<string> ListLibraryNamesByHints(object root, int limit, params string[] propertyHints)
        {
            var result = new List<string>();
            var seen = new HashSet<object>();

            void Visit(object? node, string path, int depth)
            {
                if (node == null || depth > 8 || result.Count >= limit) return;
                if (!seen.Add(node)) return;

                foreach (var hint in propertyHints)
                {
                    var value = TryGetPropertyValue(node, hint);
                    if (value == null) continue;

                    if (value is IEnumerable enumerable && value is not string)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item == null || result.Count >= limit) break;
                            var name = TryGetName(item);
                            var nextPath = string.IsNullOrWhiteSpace(path)
                                ? (name ?? item.ToString() ?? "")
                                : path + "/" + (name ?? item.ToString() ?? "");
                            if (!string.IsNullOrWhiteSpace(name) && !result.Contains(nextPath, StringComparer.OrdinalIgnoreCase))
                            {
                                result.Add(nextPath);
                            }
                            Visit(item, nextPath, depth + 1);
                        }
                    }
                    else
                    {
                        Visit(value, path, depth + 1);
                    }
                }
            }

            Visit(root, "", 0);
            return result;
        }

        private static object? FindLibraryObjectByPathOrName(object root, string wantedPathOrName, List<string> attempts, params string[] propertyHints)
        {
            var wanted = (wantedPathOrName ?? string.Empty).Trim();
            var wantedLeaf = LastPathSegment(wanted);
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

            object? Visit(object? node, string path, int depth)
            {
                if (node == null || depth > 10) return null;
                if (!seen.Add(node)) return null;

                var name = TryGetName(node) ?? TryGetPropertyValue(node, "Name")?.ToString() ?? string.Empty;
                var nodePath = string.IsNullOrWhiteSpace(path)
                    ? name
                    : string.IsNullOrWhiteSpace(name) ? path : path + "/" + name;

                if (!string.IsNullOrWhiteSpace(name) &&
                    (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(nodePath, wanted, StringComparison.OrdinalIgnoreCase) ||
                     nodePath.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, wantedLeaf, StringComparison.OrdinalIgnoreCase)))
                {
                    attempts.Add("Found MasterCopy candidate: " + nodePath + " type=" + (node.GetType().FullName ?? node.GetType().Name));
                    return node;
                }

                foreach (var hint in propertyHints)
                {
                    var value = TryGetPropertyValue(node, hint);
                    if (value == null) continue;
                    attempts.Add($"Scan {node.GetType().Name}.{hint}: {value.GetType().FullName}");

                    if (value is IEnumerable enumerable && value is not string)
                    {
                        foreach (var child in enumerable)
                        {
                            var found = Visit(child, nodePath, depth + 1);
                            if (found != null) return found;
                        }
                    }
                    else
                    {
                        var found = Visit(value, nodePath, depth + 1);
                        if (found != null) return found;
                    }
                }

                return null;
            }

            return Visit(root, "", 0);
        }

        private static object? TryImportMasterCopyIntoScreen(object screen, object screenItems, object masterCopy, string expectedName, int left, int top, List<string> attempts)
        {
            var targets = new[] { screenItems, screen }.Where(x => x != null).Distinct(ReferenceEqualityComparer.Instance).ToArray();
            var sourceArgs = new[] { masterCopy, TryGetPropertyValue(masterCopy, "Content"), TryGetPropertyValue(masterCopy, "Object") }
                .Where(x => x != null)
                .Distinct(ReferenceEqualityComparer.Instance!)
                .ToArray();

            foreach (var target in targets)
            {
                attempts.Add("Target candidate methods on " + target.GetType().FullName + ": " + string.Join(" | ", DescribeMasterCopyCandidateMethods(target).Take(80)));
                foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                             .Where(m => IsMasterCopyImportMethodName(m.Name)))
                {
                    var ps = method.GetParameters();
                    foreach (var source in sourceArgs)
                    {
                        if (!CanAssignParameter(ps, source))
                            continue;

                        var args = BuildMasterCopyImportArgs(ps, source!, expectedName, left, top);
                        if (args == null)
                        {
                            attempts.Add($"Skip {target.GetType().Name}.{method.Name}: unsupported parameters ({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                            continue;
                        }

                        try
                        {
                            attempts.Add($"Try {target.GetType().Name}.{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                            var result = method.Invoke(target, args);
                            attempts.Add($"OK {target.GetType().Name}.{method.Name}: result={(result == null ? "<null>" : result.GetType().FullName)}");
                            if (result != null) return result;
                            var readback = FindExistingByName(screenItems, expectedName);
                            if (readback != null) return readback;
                        }
                        catch (TargetInvocationException tie) when (tie.InnerException != null)
                        {
                            attempts.Add($"FAIL {target.GetType().Name}.{method.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}");
                        }
                        catch (Exception ex)
                        {
                            attempts.Add($"FAIL {target.GetType().Name}.{method.Name}: {ex.GetType().FullName}: {ex.Message}");
                        }
                    }
                }
            }

            var extensionImported = TryImportMasterCopyViaExtensionMethods(screen, screenItems, masterCopy, expectedName, left, top, attempts);
            if (extensionImported != null)
                return extensionImported;

            attempts.Add("Source candidate methods on " + masterCopy.GetType().FullName + ": " + string.Join(" | ", DescribeMasterCopyCandidateMethods(masterCopy).Take(120)));
            foreach (var method in masterCopy.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => IsMasterCopySourceMethodName(m.Name)))
            {
                var ps = method.GetParameters();
                foreach (var target in targets)
                {
                    if (!CanAssignParameter(ps, target))
                        continue;

                    var args = BuildMasterCopySourceArgs(ps, target, screen, screenItems, expectedName, left, top);
                    if (args == null)
                    {
                        attempts.Add($"Skip source {masterCopy.GetType().Name}.{method.Name}: unsupported parameters ({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                        continue;
                    }

                    try
                    {
                        attempts.Add($"Try source {masterCopy.GetType().Name}.{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                        var result = method.Invoke(masterCopy, args);
                        attempts.Add($"OK source {masterCopy.GetType().Name}.{method.Name}: result={(result == null ? "<null>" : result.GetType().FullName)}");
                        if (result != null) return result;
                        var readback = FindExistingByName(screenItems, expectedName);
                        if (readback != null) return readback;
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        attempts.Add($"FAIL source {masterCopy.GetType().Name}.{method.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}");
                    }
                    catch (Exception ex)
                    {
                        attempts.Add($"FAIL source {masterCopy.GetType().Name}.{method.Name}: {ex.GetType().FullName}: {ex.Message}");
                    }
                }
            }

            return null;
        }

        private static object? TryImportMasterCopyViaExtensionMethods(object screen, object screenItems, object masterCopy, string expectedName, int left, int top, List<string> attempts)
        {
            var targets = new[] { screenItems, screen }.Where(x => x != null).Distinct(ReferenceEqualityComparer.Instance).ToArray();
            var sources = new[] { masterCopy, TryGetPropertyValue(masterCopy, "Content"), TryGetPropertyValue(masterCopy, "Object") }
                .Where(x => x != null)
                .Distinct(ReferenceEqualityComparer.Instance!)
                .ToArray();
            var candidates = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => (a.GetName().Name ?? "").StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
                .SelectMany(GetLoadableTypes)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.IsDefined(typeof(ExtensionAttribute), false))
                .Where(m => IsMasterCopyImportMethodName(m.Name) || IsMasterCopySourceMethodName(m.Name))
                .Where(m => !m.ContainsGenericParameters)
                .Take(300)
                .ToList();

            attempts.Add("Extension candidate methods: " + string.Join(" | ", candidates.Select(m => m.DeclaringType?.FullName + "." + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) + ")").Take(120)));

            foreach (var method in candidates)
            {
                var ps = method.GetParameters();
                foreach (var target in targets)
                foreach (var source in sources)
                {
                    var args = BuildMasterCopyExtensionArgs(ps, target, screen, screenItems, source!, expectedName, left, top);
                    if (args == null)
                        continue;

                    try
                    {
                        attempts.Add($"Try extension {method.DeclaringType?.Name}.{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))})");
                        var result = method.Invoke(null, args);
                        attempts.Add($"OK extension {method.DeclaringType?.Name}.{method.Name}: result={(result == null ? "<null>" : result.GetType().FullName)}");
                        if (result != null) return result;
                        var readback = FindExistingByName(screenItems, expectedName);
                        if (readback != null) return readback;
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        attempts.Add($"FAIL extension {method.DeclaringType?.Name}.{method.Name}: {tie.InnerException.GetType().FullName}: {tie.InnerException.Message}");
                    }
                    catch (Exception ex)
                    {
                        attempts.Add($"FAIL extension {method.DeclaringType?.Name}.{method.Name}: {ex.GetType().FullName}: {ex.Message}");
                    }
                }
            }

            return null;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null)!;
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static IEnumerable<string> DescribeMasterCopyCandidateMethods(object target)
        {
            return target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => IsMasterCopyImportMethodName(m.Name) || IsMasterCopySourceMethodName(m.Name) || m.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(m => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)) + ") -> " + m.ReturnType.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsMasterCopyImportMethodName(string name)
        {
            return name.Equals("CreateFrom", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("CreateFromMasterCopy", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Import", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("ImportFrom", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Paste", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Insert", StringComparison.OrdinalIgnoreCase)
                   || name.IndexOf("MasterCopy", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsMasterCopySourceMethodName(string name)
        {
            return name.Equals("CopyTo", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Copy", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("PasteTo", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Instantiate", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("CreateInstance", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("InsertInto", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("ImportTo", StringComparison.OrdinalIgnoreCase)
                   || name.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Instantiate", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool CanAssignParameter(ParameterInfo[] parameters, object? source)
        {
            if (parameters.Length == 0 || source == null) return false;
            return parameters.Any(p => p.ParameterType.IsInstanceOfType(source));
        }

        private static object[]? BuildMasterCopyImportArgs(ParameterInfo[] parameters, object source, string expectedName, int left, int top)
        {
            var args = new object?[parameters.Length];
            var sourceUsed = false;

            for (var i = 0; i < parameters.Length; i++)
            {
                var t = parameters[i].ParameterType;
                var n = parameters[i].Name ?? string.Empty;

                if (!sourceUsed && t.IsInstanceOfType(source))
                {
                    args[i] = source;
                    sourceUsed = true;
                }
                else if (t == typeof(string))
                {
                    args[i] = expectedName;
                }
                else if (t == typeof(int))
                {
                    args[i] = n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left;
                }
                else if (t == typeof(uint))
                {
                    args[i] = (uint)Math.Max(0, n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(double))
                {
                    args[i] = (double)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(float))
                {
                    args[i] = (float)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(bool))
                {
                    args[i] = false;
                }
                else if (t.IsEnum)
                {
                    args[i] = Enum.ToObject(t, 0);
                }
                else if (t.IsValueType)
                {
                    args[i] = Activator.CreateInstance(t);
                }
                else if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
                else
                {
                    return null;
                }
            }

            return sourceUsed ? Array.ConvertAll(args!, a => a!) : null;
        }

        private static object[]? BuildMasterCopyExtensionArgs(ParameterInfo[] parameters, object target, object screen, object screenItems, object source, string expectedName, int left, int top)
        {
            var args = new object?[parameters.Length];
            var targetUsed = false;
            var sourceUsed = false;

            for (var i = 0; i < parameters.Length; i++)
            {
                var t = parameters[i].ParameterType;
                var n = parameters[i].Name ?? string.Empty;

                if (!targetUsed && t.IsInstanceOfType(target))
                {
                    args[i] = target;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screenItems))
                {
                    args[i] = screenItems;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screen))
                {
                    args[i] = screen;
                    targetUsed = true;
                }
                else if (!sourceUsed && t.IsInstanceOfType(source))
                {
                    args[i] = source;
                    sourceUsed = true;
                }
                else if (t == typeof(string))
                {
                    args[i] = expectedName;
                }
                else if (t == typeof(int))
                {
                    args[i] = n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left;
                }
                else if (t == typeof(uint))
                {
                    args[i] = (uint)Math.Max(0, n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(double))
                {
                    args[i] = (double)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(float))
                {
                    args[i] = (float)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(bool))
                {
                    args[i] = false;
                }
                else if (t.IsEnum)
                {
                    args[i] = Enum.ToObject(t, 0);
                }
                else if (t.IsValueType)
                {
                    args[i] = Activator.CreateInstance(t);
                }
                else if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
                else
                {
                    return null;
                }
            }

            return targetUsed && sourceUsed ? Array.ConvertAll(args!, a => a!) : null;
        }

        private static object[]? BuildMasterCopySourceArgs(ParameterInfo[] parameters, object target, object screen, object screenItems, string expectedName, int left, int top)
        {
            var args = new object?[parameters.Length];
            var targetUsed = false;

            for (var i = 0; i < parameters.Length; i++)
            {
                var t = parameters[i].ParameterType;
                var n = parameters[i].Name ?? string.Empty;

                if (!targetUsed && t.IsInstanceOfType(target))
                {
                    args[i] = target;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screenItems))
                {
                    args[i] = screenItems;
                    targetUsed = true;
                }
                else if (!targetUsed && t.IsInstanceOfType(screen))
                {
                    args[i] = screen;
                    targetUsed = true;
                }
                else if (t == typeof(string))
                {
                    args[i] = expectedName;
                }
                else if (t == typeof(int))
                {
                    args[i] = n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left;
                }
                else if (t == typeof(uint))
                {
                    args[i] = (uint)Math.Max(0, n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(double))
                {
                    args[i] = (double)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(float))
                {
                    args[i] = (float)(n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("y", StringComparison.OrdinalIgnoreCase) >= 0 ? top : left);
                }
                else if (t == typeof(bool))
                {
                    args[i] = false;
                }
                else if (t.IsEnum)
                {
                    args[i] = Enum.ToObject(t, 0);
                }
                else if (t.IsValueType)
                {
                    args[i] = Activator.CreateInstance(t);
                }
                else if (parameters[i].HasDefaultValue)
                {
                    args[i] = parameters[i].DefaultValue;
                }
                else
                {
                    return null;
                }
            }

            return targetUsed ? Array.ConvertAll(args!, a => a!) : null;
        }

        private static List<string> ListNamedChildren(object collection, int limit)
        {
            var result = new List<string>();
            if (collection is not IEnumerable enumerable || collection is string)
                return result;

            foreach (var item in enumerable)
            {
                if (item == null) continue;
                var name = TryGetName(item) ?? TryGetPropertyValue(item, "Name")?.ToString() ?? item.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                result.Add(name);
                if (result.Count >= Math.Max(1, limit)) break;
            }

            return result;
        }

        private static string? ResolveImportedReadbackName(List<string> before, List<string> after, string expectedName)
        {
            if (!string.IsNullOrWhiteSpace(expectedName) && after.Any(x => string.Equals(x, expectedName, StringComparison.OrdinalIgnoreCase)))
                return after.First(x => string.Equals(x, expectedName, StringComparison.OrdinalIgnoreCase));

            var added = after
                .Where(x => !before.Contains(x, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return added.Count == 1 ? added[0] : null;
        }

        private static string LastPathSegment(string path)
        {
            var value = (path ?? string.Empty).Trim().Trim('/', '\\');
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var parts = value.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? value : parts[^1];
        }

        private static void TryCloseOrDispose(object obj)
        {
            try
            {
                var close = obj.GetType().GetMethod("Close", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                close?.Invoke(obj, null);
            }
            catch { }

            try
            {
                if (obj is IDisposable d) d.Dispose();
            }
            catch { }
        }

        private static List<string> TryListNamesFromCollection(object root, string[] propertyHints, string finalCollectionNameHint)
        {
            var result = new List<string>();
            try
            {
                object? collection = null;
                var rootType = root.GetType();

                if (propertyHints.Length == 0 && root is System.Collections.IEnumerable)
                {
                    collection = root;
                }

                // try direct property matches
                foreach (var propName in propertyHints)
                {
                    var prop = rootType.GetProperty(propName);
                    if (prop == null) continue;

                    var v = prop.GetValue(root);
                    if (v == null) continue;

                    // tag tables can be under a folder object
                    if (propName.EndsWith("Folder", StringComparison.OrdinalIgnoreCase))
                    {
                        collection = v.GetType().GetProperty(finalCollectionNameHint)?.GetValue(v);
                    }
                    else
                    {
                        collection = v;
                    }

                    if (collection != null) break;
                }

                if (collection is System.Collections.IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            result.Add(name!);
                        }
                    }
                }
            }
            catch
            {
                // best-effort only
            }

            return result;
        }

        private static object? TryFindByNameInCollection(object root, string[] propertyHints, string wantedName)
        {
            try
            {
                var rootType = root.GetType();
                foreach (var propName in propertyHints)
                {
                    object? collection = null;

                    var prop = rootType.GetProperty(propName);
                    if (prop != null)
                    {
                        collection = prop.GetValue(root);
                    }
                    else if (propName.EndsWith("Folder", StringComparison.OrdinalIgnoreCase))
                    {
                        var folder = rootType.GetProperty(propName)?.GetValue(root);
                        if (folder != null)
                        {
                            collection = folder.GetType().GetProperty(propName.Replace("Folder", "s"))?.GetValue(folder);
                        }
                    }

                    if (collection is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item == null) continue;
                            var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                            if (string.Equals(name, wantedName, StringComparison.OrdinalIgnoreCase))
                            {
                                return item;
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        #endregion
    }
}
