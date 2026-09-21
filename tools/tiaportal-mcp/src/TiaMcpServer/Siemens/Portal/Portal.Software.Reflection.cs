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

        private static string? BestEffortExtractFirstName(object? importReturnValue)
        {
            try
            {
                if (importReturnValue is IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        var name = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
            }
            catch { }
            return null;
        }

        private static object? TryGetPropertyValue(object obj, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                try
                {
                    var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null) continue;
                    var v = p.GetValue(obj);
                    if (v != null) return v;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> DescribeTypeMembers(Type type, bool includeNonPublic)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;

            var result = new List<string>();
            foreach (var p in type.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                result.Add($"Property:{p.Name}:{p.PropertyType.FullName ?? p.PropertyType.Name}");
            }

            foreach (var m in type.GetMethods(flags))
            {
                if (m.IsSpecialName) continue;
                var ps = string.Join(", ", m.GetParameters().Select(x => $"{x.ParameterType.Name} {x.Name}"));
                result.Add($"Method:{m.Name}({ps}) -> {m.ReturnType.FullName ?? m.ReturnType.Name}");
            }

            return result
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal);
        }

        private static IEnumerable<string> FormatEnumerableObjects(object? value, int limit)
        {
            var lines = new List<string>();
            if (value == null)
            {
                lines.Add("<null>");
                return lines;
            }

            if (value is not IEnumerable enumerable || value is string)
            {
                lines.Add(value.ToString() ?? "<null>");
                return lines;
            }

            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= Math.Max(1, limit)) break;
                if (item == null)
                {
                    lines.Add("<null>");
                    continue;
                }

                var type = item.GetType();
                var parts = new List<string> { type.FullName ?? type.Name };
                foreach (var propertyName in new[] { "Name", "CompositionName", "Type", "Description" })
                {
                    try
                    {
                        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        var propValue = prop?.GetValue(item);
                        if (propValue != null)
                        {
                            parts.Add($"{propertyName}={propValue}");
                        }
                    }
                    catch { }
                }
                lines.Add(string.Join(" | ", parts));
            }

            if (lines.Count == 0) lines.Add("<empty>");
            return lines;
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

        private static object? ConvertReflectionArgument(object? value, Type targetType)
        {
            if (value == null) return null;

            var nonNullable = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nonNullable.IsInstanceOfType(value)) return value;

            if (typeof(System.Collections.IDictionary).IsAssignableFrom(nonNullable))
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), typeof(object));
                var dict = Activator.CreateInstance(dictType);
                var addMethod = dictType.GetMethod("Add", new[] { typeof(string), typeof(object) });
                if (value is IEnumerable<KeyValuePair<string, object?>> kvps)
                {
                    foreach (var kv in kvps)
                    {
                        addMethod?.Invoke(dict, new object?[] { kv.Key, kv.Value });
                    }
                    return dict;
                }
            }

            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(nonNullable) && nonNullable != typeof(string))
            {
                var enumerableInterface = nonNullable.IsInterface && nonNullable.IsGenericType
                    ? nonNullable
                    : nonNullable.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                if (enumerableInterface != null)
                {
                    var elementType = enumerableInterface.GetGenericArguments()[0];
                    if (elementType.IsGenericType &&
                        elementType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) &&
                        elementType.GenericTypeArguments[0] == typeof(string))
                    {
                        var valueType = elementType.GenericTypeArguments[1];
                        var listType = typeof(List<>).MakeGenericType(elementType);
                        var list = (System.Collections.IList)Activator.CreateInstance(listType);
                        if (value is IEnumerable<KeyValuePair<string, object?>> kvps)
                        {
                            foreach (var kv in kvps)
                            {
                                var kvValue = kv.Value;
                                if (kvValue != null && valueType != typeof(object) && !valueType.IsInstanceOfType(kvValue))
                                {
                                    kvValue = Convert.ChangeType(kvValue, valueType);
                                }
                                var pair = Activator.CreateInstance(elementType, kv.Key, kvValue);
                                list.Add(pair);
                            }
                            return list;
                        }
                    }
                }
            }

            if (nonNullable.IsEnum)
            {
                return value is string s
                    ? Enum.Parse(nonNullable, s, ignoreCase: true)
                    : Enum.ToObject(nonNullable, value);
            }

            return Convert.ChangeType(value, nonNullable);
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
