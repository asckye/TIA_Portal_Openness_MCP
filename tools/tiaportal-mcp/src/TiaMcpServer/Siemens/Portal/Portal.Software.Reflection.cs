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

namespace TiaMcpServer.Siemens
{
    // Partial: software. Family file split out of Portal.Software.cs (2.8.0); behavior unchanged.
    public partial class Portal
    {
        #region software - Reflection

        private static bool TryExportEngineeringObject(object engineeringObject, string exportPath, out string? error)
        {
            var result = EngineeringExport.Export(engineeringObject, exportPath);
            error = result["success"]!.GetValue<bool>() ? null : result.ToJsonString();
            return result["success"]!.GetValue<bool>();
        }

        private static bool TryImportEngineeringObjectIntoCollection(object collection, string importPath, out string? importedName, out string? error)
        {
            importedName = null;
            error = null;

            try
            {
                var fi = new FileInfo(importPath);
                if (!fi.Exists)
                {
                    error = "File not found";
                    return false;
                }

                var t = collection.GetType();

                // Prefer Import(FileInfo, ImportOptions)
                var m2 = t.GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) });
                if (m2 != null)
                {
                    var list = m2.Invoke(collection, new object[] { fi, ImportOptions.Override });
                    importedName = BestEffortExtractFirstName(list) ?? Path.GetFileNameWithoutExtension(importPath);
                    return true;
                }

                // Import(FileInfo)
                var m1 = t.GetMethod("Import", new[] { typeof(FileInfo) });
                if (m1 != null)
                {
                    var list = m1.Invoke(collection, new object[] { fi });
                    importedName = BestEffortExtractFirstName(list) ?? Path.GetFileNameWithoutExtension(importPath);
                    return true;
                }

                error = $"No Import method found on collection type {t.FullName}";
                return false;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                error = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return false;
            }
        }

        // Technology imports require an explicit overwrite contract and complete returned identities.
        // Keep the legacy overload above unchanged for unrelated import families.
        private static bool TryImportEngineeringObjectIntoCollection(object collection, string importPath, bool overwrite, List<string> importedNames, out string? error)
        {
            error = null;
            var nativeEntered = false;
            var firstNameIndex = importedNames.Count;
            try
            {
                var file = new FileInfo(importPath);
                if (!file.Exists)
                {
                    error = "File not found";
                    return false;
                }

                var method = collection.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) });
                if (method == null)
                {
                    error = "Import(FileInfo, ImportOptions) is unavailable; no import was attempted.";
                    return false;
                }

                // Never fall back to an option-less overload, including after native entry.
                nativeEntered = true;
                var result = method.Invoke(collection, new object[] { file, overwrite ? ImportOptions.Override : ImportOptions.None });
                if (result is not IEnumerable items || result is string)
                    throw new InvalidOperationException("Import returned no enumerable object identities.");
                foreach (var item in items)
                {
                    var name = item?.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                        throw new InvalidOperationException("An imported object identity could not be read.");
                    importedNames.Add(name);
                }
                if (importedNames.Count == firstNameIndex)
                    throw new InvalidOperationException("Import returned no object identities.");
                return true;
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                error = cause.GetType().FullName + ": " + cause.Message;
                if (nativeEntered)
                {
                    error += " Import was entered; the project may have changed. No retry was attempted.";
                    if (importedNames.Count > firstNameIndex)
                        error += " Returned object identities read before failure: " + string.Join(", ", importedNames.Skip(firstNameIndex)) + ".";
                }
                return false;
            }
        }


        private static object? TryInvokeExplicitEngineeringMethod(object target, string methodShortName, object?[] args, out string? error)
        {
            error = null;
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var methods = target.GetType().GetMethods(flags)
                    .Where(m =>
                        !m.IsSpecialName &&
                        (string.Equals(m.Name, methodShortName, StringComparison.OrdinalIgnoreCase) ||
                         m.Name.EndsWith("." + methodShortName, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(m => m.GetParameters().Length)
                    .ToList();

                if (methods.Count == 0)
                {
                    error = "Method not found: " + methodShortName;
                    return null;
                }

                foreach (var method in methods)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != args.Length) continue;

                    try
                    {
                        var converted = new object?[parameters.Length];
                        for (var i = 0; i < parameters.Length; i++)
                        {
                            converted[i] = ConvertReflectionArgument(args[i], parameters[i].ParameterType);
                        }
                        return method.Invoke(target, converted);
                    }
                    catch (Exception ex)
                    {
                        error = FormatExceptionDetail(ex);
                    }
                }

                error ??= "No matching overload succeeded for " + methodShortName;
                return null;
            }
            catch (Exception ex)
            {
                error = FormatExceptionDetail(ex);
                return null;
            }
        }


        private static object? TryResolveChildGroupByPath(object rootGroup, string groupPath)
        {
            if (string.IsNullOrWhiteSpace(groupPath)) return rootGroup;

            var parts = groupPath.Trim().Trim('/').Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            object? current = rootGroup;
            foreach (var part in parts)
            {
                if (current == null) return null;

                // common group collections used by HMI objects
                var next = TryFindByNameInCollection(current, new[] { "Groups", "ScreenGroups", "TagTableGroups", "Folders" }, part);
                if (next == null)
                {
                    // Some shapes: current.ScreenGroups or current.Groups are nested under another property
                    var groupContainer = TryGetPropertyValue(current, "Groups", "ScreenGroups", "TagTableGroups");
                    if (groupContainer != null)
                    {
                        next = TryFindByNameInCollection(groupContainer, new[] { "Groups", "ScreenGroups", "TagTableGroups", "Folders" }, part);
                    }
                }

                current = next;
            }

            return current;
        }

        #endregion
    }
}
