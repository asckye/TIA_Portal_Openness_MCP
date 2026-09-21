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
        #region software - UnifiedHmiHelpers

        private static bool TrySetProperty(object target, string propName, object? value)
        {
            try
            {
                var p = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return false;

                object? v = CoerceReflectionValue(value, p.PropertyType);

                p.SetValue(target, v);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private object ResolveHmiSoftwareOrThrow(string hmiSoftwarePath)
        {
            var sc = GetSoftwareContainer(hmiSoftwarePath);
            var software = sc?.Software;
            if (software == null)
            {
                throw new InvalidOperationException($"HMI software not found at '{hmiSoftwarePath}'.");
            }

            return software;
        }

        private object ResolveHmiScreenOrThrow(string hmiSoftwarePath, string screenName)
        {
            var sw = ResolveHmiSoftwareOrThrow(hmiSoftwarePath);
            var screen = HmiExactAccess.Screen(sw, screenName);
            if (screen == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"HMI screen '{screenName}' not found.");
            }

            return screen;
        }

        private object ResolveHmiScreenItemOrThrow(string hmiSoftwarePath, string screenName, string itemName)
        {
            var screen = ResolveHmiScreenOrThrow(hmiSoftwarePath, screenName);
            var items = TryGetPropertyValue(screen, "ScreenItems");
            if (items == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"ScreenItems collection not found on screen '{screenName}'.");
            }

            var item = HmiExactAccess.Named(items, itemName);
            if (item == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"Screen item '{itemName}' not found on screen '{screenName}'.");
            }

            return item;
        }

        private object ResolveHmiButtonEventHandlerOrThrow(string hmiSoftwarePath, string screenName, string buttonName, string eventType, bool createIfMissing = true)
        {
            var button = ResolveHmiScreenItemOrThrow(hmiSoftwarePath, screenName, buttonName);
            var handlers = button.GetType().GetProperty("EventHandlers")?.GetValue(button)
                ?? throw new PortalException(PortalErrorCode.NotFound, "EventHandlers not found on " + buttonName);
            return HmiEventAccess.Resolve(handlers, eventType, createIfMissing);
        }

        private object EnsureHmiTagTableObject(object hmiSoftware, string tagTableName)
        {
            var tagRoot = TryGetHmiTagRoot(hmiSoftware);
            var table = TryFindHmiTagTable(hmiSoftware, tagTableName);
            if (table != null) return table;

            var tables = TryGetHmiTagTablesCollection(hmiSoftware);
            if (tables == null)
            {
                throw new InvalidOperationException($"HMI TagTables collection not found. hmiType={hmiSoftware.GetType().FullName}; tagRootType={tagRoot.GetType().FullName}; tagRootMembers={string.Join(" | ", DescribeMembers(tagRoot, 80).Select(m => $"{m.Kind}:{m.Name}:{m.Type}"))}");
            }

            table = TryCreateNamedEngineeringObject(tables, tagTableName, out var createError);
            if (table == null)
            {
                throw new InvalidOperationException(createError ?? $"Failed to create HMI tag table '{tagTableName}'.");
            }

            return table;
        }

        private static object? TryCreateNamedEngineeringObject(object collection, string name, out string? error)
        {
            error = null;
            var attempts = new List<string>();

            foreach (var method in collection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => string.Equals(m.Name, "Create", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(m => m.GetParameters().Length))
            {
                var ps = method.GetParameters();
                var sig = $"{method.Name}({string.Join(", ", ps.Select(p => p.ParameterType.FullName + " " + p.Name))})";

                object?[]? args = null;
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                {
                    args = new object?[] { name };
                }
                else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                {
                    args = new object?[] { name, name };
                }
                else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType.IsEnum)
                {
                    args = new object?[] { name, Enum.ToObject(ps[1].ParameterType, 0) };
                }
                else if (ps.Length == 2 && ps[0].ParameterType.IsEnum && ps[1].ParameterType == typeof(string))
                {
                    args = new object?[] { Enum.ToObject(ps[0].ParameterType, 0), name };
                }
                else
                {
                    attempts.Add($"SKIP {sig}");
                    continue;
                }

                try
                {
                    var created = method.Invoke(collection, args);
                    if (created != null) return created;
                    attempts.Add($"NULL {sig}");
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                    attempts.Add($"ERR {sig}: {msg}");
                    if (msg.IndexOf("ValueIsNotUnique", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var existing = FindExistingByName(collection, name);
                        if (existing != null) return existing;
                    }
                }
                catch (Exception ex)
                {
                    attempts.Add($"ERR {sig}: {ex.Message}");
                }
            }

            error = $"No supported Create overload succeeded on {collection.GetType().FullName}. Attempts: {string.Join(" | ", attempts)}";
            return null;
        }

        private static object TryGetHmiTagRoot(object hmiSoftware)
        {
            return TryGetPropertyValue(hmiSoftware,
                       "TagTableFolder",
                       "TagFolder",
                       "HmiTagTableFolder",
                       "HmiTagFolder",
                       "TagTableGroup",
                       "HmiTagTableGroup")
                   ?? hmiSoftware;
        }

        private static object? TryGetHmiTagTablesCollection(object hmiSoftware)
        {
            var root = TryGetHmiTagRoot(hmiSoftware);
            return TryGetPropertyValue(root,
                       "TagTables",
                       "HmiTagTables",
                       "Tables")
                   ?? TryGetPropertyValue(hmiSoftware,
                       "TagTables",
                       "HmiTagTables",
                       "Tables");
        }

        private static object? TryFindHmiTagTable(object hmiSoftware, string tagTableName)
        {
            var root = TryGetHmiTagRoot(hmiSoftware);
            return TryFindByNameInCollection(root, new[] { "TagTables", "HmiTagTables", "Tables" }, tagTableName)
                   ?? TryFindByNameInCollection(hmiSoftware, new[] { "TagTables", "HmiTagTables", "Tables" }, tagTableName)
                   ?? FindExistingByName(TryGetHmiTagTablesCollection(hmiSoftware) ?? root, tagTableName)
                   ?? (root == null ? null : EnumerateHmiTagTablesRecursive(root).FirstOrDefault(t => string.Equals(TryGetName(t)?.Trim(), tagTableName, StringComparison.OrdinalIgnoreCase)));
        }

        // 2.7.46: tag tables inside user folders (TagUserFolder.Folders, nested) - the root-only lookup answered "not found" for a
        // table just imported into a folder on the real project (classic TP700, ImportHmiTagTable into MCP_TagFolder).
        private static IEnumerable<object> EnumerateHmiTagTablesRecursive(object folder, int depth = 0)
        {
            if (depth > 32) yield break;
            if (TryGetPropertyValue(folder, "TagTables") is IEnumerable tables)
                foreach (var table in tables) if (table != null) yield return table;
            if (TryGetPropertyValue(folder, "Folders") is IEnumerable folders)
                foreach (var child in folders)
                    if (child != null) foreach (var table in EnumerateHmiTagTablesRecursive(child, depth + 1)) yield return table;
        }

        private static object? FindExistingByName(object compositionOrEnumerable, string name)
        {
            try
            {
                if (compositionOrEnumerable is IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        var n = TryGetName(it);
                        if (!string.IsNullOrWhiteSpace(n) &&
                            string.Equals(n!.Trim(), name, StringComparison.OrdinalIgnoreCase))
                        {
                            return it;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private static object? InvokeCreate(MethodInfo method, object target, object[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                var msg = $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}";
                throw new InvalidOperationException(msg, tie.InnerException);
            }
        }

        private static Type? ResolveUnifiedScreenItemType(string itemType)
        {
            var key = (itemType ?? string.Empty).Trim();
            var candidates = key.Equals("Button", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiButton", StringComparison.OrdinalIgnoreCase)
                ? new[] { "Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton" }
                : key.Equals("Text", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiText", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "Siemens.Engineering.HmiUnified.UI.Shapes.HmiText" }
                : key.Equals("Rectangle", StringComparison.OrdinalIgnoreCase) || key.Equals("Lamp", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiRectangle", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle", "Siemens.Engineering.HmiUnified.UI.Widgets.HmiRectangle" }
                    : key.Equals("IOField", StringComparison.OrdinalIgnoreCase) || key.Equals("HmiIOField", StringComparison.OrdinalIgnoreCase)
                        ? new[] { "Siemens.Engineering.HmiUnified.UI.Widgets.HmiIOField" }
                        : new[] { key };

            foreach (var name in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(name, throwOnError: false, ignoreCase: false);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }

            return null;
        }

        private static IEnumerable<Type> ResolveUnifiedHmiDynamizationTypes(string dynamizationType)
        {
            var filter = (dynamizationType ?? string.Empty).Trim();
            var preferredNames = string.IsNullOrWhiteSpace(filter)
                ? new[]
                {
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization",
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.DiscreteDynamization",
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.RangeDynamization",
                    "Siemens.Engineering.HmiUnified.UI.Dynamization.ScriptDynamization"
                }
                : filter.Contains(".")
                    ? new[] { filter }
                    : new[]
                    {
                        $"Siemens.Engineering.HmiUnified.UI.Dynamization.{filter}",
                        $"Siemens.Engineering.HmiUnified.UI.Dynamization.{filter}Dynamization",
                        filter
                    };

            foreach (var name in preferredNames)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? t = null;
                    try { t = asm.GetType(name, throwOnError: false, ignoreCase: false); } catch { }
                    if (t != null) yield return t;
                }
            }

            if (!string.IsNullOrWhiteSpace(filter) && !filter.Contains("."))
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t.FullName == null) continue;
                        if (!t.FullName.StartsWith("Siemens.Engineering.HmiUnified.UI.Dynamization.", StringComparison.Ordinal)) continue;
                        if (t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) yield return t;
                    }
                }
            }
        }

        private static object? CreateUnifiedScreenItem(object items, string itemName, Type? itemClrType, string itemTypeHint)
        {
            var methods = items.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => string.Equals(m.Name, "Create", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                if (itemClrType != null && m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType == typeof(string))
                {
                    var created = m.MakeGenericMethod(itemClrType).Invoke(items, new object[] { itemName });
                    if (created != null) return created;
                }

                if (!m.IsGenericMethodDefinition && ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                {
                    foreach (var args in new[] { new object[] { itemName, itemTypeHint }, new object[] { itemTypeHint, itemName } })
                    {
                        try
                        {
                            var created = m.Invoke(items, args);
                            if (created != null) return created;
                        }
                        catch { }
                    }
                }
            }

            throw new InvalidOperationException($"Unable to create screen item '{itemName}' as '{itemTypeHint}'. ResolvedType={itemClrType?.FullName ?? "null"}.");
        }

        private static object? FindPressedStateTag(object pressedStateTags, string tagName)
        {
            try
            {
                if (pressedStateTags is IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        foreach (var propName in new[] { "Tag", "TagName", "HmiTag", "HmiTagName", "Name", "TagPath" })
                        {
                            var value = TryGetPropertyValue(it, propName)?.ToString();
                            if (!string.IsNullOrWhiteSpace(value) &&
                                string.Equals(value!.Trim(), tagName, StringComparison.OrdinalIgnoreCase))
                            {
                                return it;
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private static bool TrySetAnyProperty(object target, string value, params string[] propertyNames)
        {
            foreach (var propName in propertyNames)
            {
                if (TrySetProperty(target, propName, value)) return true;
            }

            return false;
        }

        private static bool TrySetAnyPropertyOrAttribute(object target, object? value, params string[] propertyNames)
        {
            var any = false;
            foreach (var propName in propertyNames)
            {
                any = TrySetProperty(target, propName, value) || any;
                any = TrySetEngineeringAttribute(target, propName, value) || any;
            }

            return any;
        }

        private static bool TrySetAnyEnumCandidatePropertyOrAttribute(object target, IEnumerable<string> valueCandidates, params string[] propertyNames)
        {
            var any = false;
            foreach (var propName in propertyNames)
            {
                var prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite && prop.PropertyType.IsEnum)
                {
                    foreach (var candidate in valueCandidates)
                    {
                        try
                        {
                            var enumValue = Enum.Parse(prop.PropertyType, candidate, ignoreCase: true);
                            prop.SetValue(target, enumValue);
                            any = true;
                            break;
                        }
                        catch { }
                    }
                }

                var oldValue = TryGetEngineeringAttribute(target, propName);
                if (oldValue != null && oldValue.GetType().IsEnum)
                {
                    foreach (var candidate in valueCandidates)
                    {
                        try
                        {
                            var enumValue = Enum.Parse(oldValue.GetType(), candidate, ignoreCase: true);
                            if (TrySetEngineeringAttribute(target, propName, enumValue))
                            {
                                any = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            return any;
        }

        private static string DescribeWritableEnumProperties(object target, params string[] propertyNames)
        {
            var parts = new List<string>();
            foreach (var name in propertyNames)
            {
                var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType.IsEnum)
                {
                    parts.Add($"{name}:{prop.PropertyType.FullName}=[{string.Join(",", Enum.GetNames(prop.PropertyType))}]");
                    continue;
                }

                var oldValue = TryGetEngineeringAttribute(target, name);
                if (oldValue != null && oldValue.GetType().IsEnum)
                {
                    parts.Add($"{name}:attr:{oldValue.GetType().FullName}=[{string.Join(",", Enum.GetNames(oldValue.GetType()))}]");
                }
            }

            return string.Join(" | ", parts);
        }

        private static object? TryGetEngineeringAttribute(object target, string attributeName)
        {
            try
            {
                var get = target.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                return get?.Invoke(target, new object[] { attributeName });
            }
            catch
            {
                return null;
            }
        }

        private static string SummarizeHmiObjectReadback(object target, params string[] names)
        {
            var parts = new List<string>();
            foreach (var name in names)
            {
                object? value = null;
                var got = false;
                try
                {
                    var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && prop.CanRead)
                    {
                        value = prop.GetValue(target);
                        got = true;
                    }
                }
                catch { }

                if (!got)
                {
                    value = TryGetEngineeringAttribute(target, name);
                    got = value != null;
                }

                if (got)
                    parts.Add($"{name}={value ?? ""}");
            }

            return string.Join("; ", parts);
        }

        private sealed class UnifiedHmiTagBindingReadback
        {
            public string Status { get; set; } = "Failed";
            public bool Verified { get; set; }
            public string Readback { get; set; } = string.Empty;
            public string Guidance { get; set; } = string.Empty;
        }

        private static UnifiedHmiTagBindingReadback ClassifyUnifiedHmiTagBinding(object tag, string connectionName, string plcTag, string address)
        {
            string Attr(params string[] names)
            {
                foreach (var name in names)
                {
                    try
                    {
                        var prop = tag.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null && prop.CanRead)
                        {
                            var v = prop.GetValue(tag)?.ToString();
                            if (!string.IsNullOrWhiteSpace(v)) return v!;
                        }
                    }
                    catch { }

                    try
                    {
                        var v = TryGetEngineeringAttribute(tag, name)?.ToString();
                        if (!string.IsNullOrWhiteSpace(v)) return v!;
                    }
                    catch { }
                }

                return string.Empty;
            }

            var readback = SummarizeHmiObjectReadback(tag, "Connection", "AccessMode", "AddressAccessMode", "TagType", "PlcName", "ControllerName", "Station", "PlcTag", "ControllerTag", "ControllerTagName", "Address", "LogicalAddress", "ProcessValueAddress", "RuntimeAddress", "ControllerAddress", "ControllerTagAddress", "ExternalAddress", "PlcAddress", "PLCAddress", "TagAddress", "AbsoluteAddress", "DataType", "HmiDataType");
            var connection = Attr("Connection", "ConnectionName");
            var accessMode = Attr("AccessMode", "AddressAccessMode");
            var symbol = Attr("PlcTag", "ControllerTag", "ControllerTagName");
            var runtimeAddress = Attr("Address", "LogicalAddress", "ProcessValueAddress", "RuntimeAddress", "ControllerAddress", "ControllerTagAddress", "ExternalAddress", "PlcAddress", "PLCAddress", "TagAddress", "AbsoluteAddress");
            var requestedAddress = (address ?? string.Empty).Trim();
            var requestedSymbol = NormalizeControllerTagName(plcTag ?? string.Empty);
            var expectedConnection = (connectionName ?? string.Empty).Trim();

            var connectionOk = string.IsNullOrWhiteSpace(expectedConnection) ||
                string.Equals(connection, expectedConnection, StringComparison.OrdinalIgnoreCase);
            var absoluteOk = !string.IsNullOrWhiteSpace(requestedAddress) &&
                connectionOk &&
                string.Equals(runtimeAddress, requestedAddress, StringComparison.OrdinalIgnoreCase);
            var symbolicOk = !string.IsNullOrWhiteSpace(requestedSymbol) &&
                connectionOk &&
                string.Equals(NormalizeControllerTagName(symbol), requestedSymbol, StringComparison.OrdinalIgnoreCase) &&
                accessMode.IndexOf("Symbol", StringComparison.OrdinalIgnoreCase) >= 0;

            if (symbolicOk)
            {
                return new UnifiedHmiTagBindingReadback
                {
                    Status = "SymbolicVerified",
                    Verified = true,
                    Readback = readback,
                    Guidance = "PLC symbolic HMI tag binding read back successfully."
                };
            }

            if (absoluteOk)
            {
                return new UnifiedHmiTagBindingReadback
                {
                    Status = "AbsoluteVerified",
                    Verified = true,
                    Readback = readback,
                    Guidance = "Absolute-address HMI tag binding read back successfully."
                };
            }

            var status = string.IsNullOrWhiteSpace(connection) || connection.IndexOf("internal", StringComparison.OrdinalIgnoreCase) >= 0 || connection.IndexOf("内部", StringComparison.OrdinalIgnoreCase) >= 0
                ? "InternalOnly"
                : "Unverified";
            return new UnifiedHmiTagBindingReadback
            {
                Status = status,
                Verified = false,
                Readback = readback,
                Guidance = "Pass connectionName plus a verified PLC symbol or absolute address, then read back Connection/AccessMode/PlcTag/Address. Internal HMI tags are not accepted by the stable project-generation path."
            };
        }

        private static string NormalizeControllerTagName(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return string.Empty;
            return tagName.Replace("\"", string.Empty);
        }

        private sealed class UnifiedHmiPlcPartnerInfo
        {
            public string SoftwarePath { get; set; } = string.Empty;
            public string DeviceName { get; set; } = string.Empty;
            public string StationName { get; set; } = string.Empty;
            public string NodeName { get; set; } = string.Empty;
            public string InitialAddress { get; set; } = string.Empty;
            public string Family { get; set; } = "UNKNOWN";

            public string Summary =>
                $"SoftwarePath={SoftwarePath}; DeviceName={DeviceName}; StationName={StationName}; NodeName={NodeName}; InitialAddress={InitialAddress}; Family={Family}";
        }

        private UnifiedHmiPlcPartnerInfo ResolveUnifiedHmiPlcPartner(string plcSoftwarePath)
        {
            plcSoftwarePath ??= string.Empty;
            var info = new UnifiedHmiPlcPartnerInfo
            {
                SoftwarePath = plcSoftwarePath,
                DeviceName = FirstPathSegment(plcSoftwarePath),
                StationName = FirstPathSegment(plcSoftwarePath),
                Family = InferUnifiedPlcFamilyFromSoftwarePath(plcSoftwarePath)
            };

            try
            {
                var sc = GetSoftwareContainer(plcSoftwarePath);
                var di = sc?.Parent as DeviceItem;
                if (di != null)
                {
                    info.StationName = TryGetName(di) ?? di.Name ?? info.StationName;
                    var root = GetTopDeviceItem(di);
                    if (root != null)
                    {
                        info.DeviceName = TryGetName(root) ?? root.Name ?? info.DeviceName;
                        info.StationName = TryGetName(root) ?? root.Name ?? info.StationName;
                        FillUnifiedHmiPartnerNetworkInfo(root, info);
                    }
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(info.NodeName))
            {
                try
                {
                    var root = GetDeviceItemByPath(info.DeviceName);
                    if (root != null) FillUnifiedHmiPartnerNetworkInfo(root, info);
                }
                catch
                {
                }
            }

            if (string.IsNullOrWhiteSpace(info.DeviceName)) info.DeviceName = FirstPathSegment(plcSoftwarePath);
            if (string.IsNullOrWhiteSpace(info.StationName)) info.StationName = info.DeviceName;
            return info;
        }

        private static string FirstPathSegment(string path)
        {
            return (path ?? string.Empty).Trim()
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
        }

        private static DeviceItem? GetTopDeviceItem(DeviceItem item)
        {
            var current = item;
            while (current.Parent is DeviceItem parent)
            {
                current = parent;
            }

            return current;
        }

        private static void FillUnifiedHmiPartnerNetworkInfo(DeviceItem root, UnifiedHmiPlcPartnerInfo info)
        {
            var plcNode = FindNetworkNodes(root).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            if (plcNode.Node == null) return;

            info.NodeName = TryGetName(plcNode.Node)
                ?? TryGetPropertyValue(plcNode.Node, "Name")?.ToString()
                ?? plcNode.Item.Name
                ?? string.Empty;

            var address = TryGetPropertyValue(plcNode.Node, "Address")?.ToString()
                ?? TryGetPropertyValue(plcNode.Node, "IpAddress")?.ToString()
                ?? TryGetPropertyValue(plcNode.Node, "IPAddress")?.ToString()
                ?? TryGetEngineeringAttribute(plcNode.Node, "Address")?.ToString()
                ?? TryGetEngineeringAttribute(plcNode.Node, "IpAddress")?.ToString()
                ?? string.Empty;
            info.InitialAddress = address;
        }

        private static void TryConfigureUnifiedHmiConnectionPartner(object connection, UnifiedHmiPlcPartnerInfo partner)
        {
            var deviceName = string.IsNullOrWhiteSpace(partner.DeviceName) ? partner.SoftwarePath : partner.DeviceName;
            var stationName = string.IsNullOrWhiteSpace(partner.StationName) ? deviceName : partner.StationName;

            TrySetAnyPropertyOrAttribute(connection, deviceName, "Partner", "PartnerName", "DeviceName", "PlcName", "ControllerName");
            TrySetAnyPropertyOrAttribute(connection, stationName, "Station", "StationName", "ControllerStation");
            TrySetAnyPropertyOrAttribute(connection, deviceName, "Controller", "Device", "Plc", "Target");

            if (!string.IsNullOrWhiteSpace(partner.NodeName))
            {
                TrySetAnyPropertyOrAttribute(connection, partner.NodeName, "Node", "PartnerNode", "Interface", "NetworkNode", "AccessPoint");
            }

            if (!string.IsNullOrWhiteSpace(partner.InitialAddress))
            {
                TrySetAnyPropertyOrAttribute(connection, partner.InitialAddress, "InitialAddress", "Address", "IpAddress", "IPAddress", "PartnerAddress");
            }
        }

        /// <summary>
        /// Infer PLC CPU family from the PLC software path (device TypeIdentifier / order number).
        /// </summary>
        private string InferUnifiedPlcFamilyFromSoftwarePath(string plcSoftwarePath)
        {
            try
            {
                var sc = GetSoftwareContainer(plcSoftwarePath);
                var di = sc?.Parent as DeviceItem;
                while (di != null)
                {
                    var tid = TryGetPropertyValue(di, "TypeIdentifier")?.ToString() ?? string.Empty;
                    var t = tid.ToUpperInvariant();
                    // Catalog MLFB often contains spaces (e.g. "OrderNumber:6ES7 211-1BE40-0XB0/...").
                    // Old checks used "6ES721" which fails after "6ES7 " + "211" — driver fell back to S7-300/400.
                    var tCompact = string.Concat(t.Where(ch => !char.IsWhiteSpace(ch)));
                    if (t.IndexOf("S7-1200", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71200", StringComparison.OrdinalIgnoreCase) >= 0
                        || tCompact.IndexOf("6ES721", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES722", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S71200";
                    if (t.IndexOf("S7-1500", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71500", StringComparison.OrdinalIgnoreCase) >= 0
                        || tCompact.IndexOf("6ES751", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES752", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S71500";
                    if (t.IndexOf("S7-300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES731", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S7300";
                    if (t.IndexOf("S7-400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES741", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "S7400";
                    di = di.Parent as DeviceItem;
                }

                var fromDevices = TryInferPlcFamilyFromProjectDevices(plcSoftwarePath);
                if (!string.IsNullOrEmpty(fromDevices)) return fromDevices;
            }
            catch
            {
            }

            return "UNKNOWN";
        }

        /// <summary>
        /// When <see cref="SoftwareContainer.Parent"/> is not a <see cref="DeviceItem"/>, CPU TypeIdentifier may still
        /// exist on nested rack/CPU items under the PLC device — walk the device tree by PLC software path head name.
        /// </summary>
        private string TryInferPlcFamilyFromProjectDevices(string plcSoftwarePath)
        {
            try
            {
                if (_project?.Devices == null) return string.Empty;
                var head = (plcSoftwarePath ?? string.Empty).Trim()
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(head)) return string.Empty;

                foreach (var device in _project.Devices)
                {
                    if (!device.Name.Equals(head, StringComparison.OrdinalIgnoreCase)) continue;
                    var stack = new Stack<DeviceItem>(device.DeviceItems ?? Enumerable.Empty<DeviceItem>());
                    while (stack.Count > 0)
                    {
                        var di = stack.Pop();
                        if (di == null) continue;
                        if (di.DeviceItems != null)
                        {
                            foreach (var ch in di.DeviceItems) stack.Push(ch);
                        }

                        var tid = TryGetPropertyValue(di, "TypeIdentifier")?.ToString() ?? string.Empty;
                        var t = tid.ToUpperInvariant();
                        var tCompact = string.Concat(t.Where(ch => !char.IsWhiteSpace(ch)));
                        if (t.IndexOf("S7-1200", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71200", StringComparison.OrdinalIgnoreCase) >= 0
                            || tCompact.IndexOf("6ES721", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES722", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S71200";
                        if (t.IndexOf("S7-1500", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S71500", StringComparison.OrdinalIgnoreCase) >= 0
                            || tCompact.IndexOf("6ES751", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES752", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S71500";
                        if (t.IndexOf("S7-300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7300", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES731", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S7300";
                        if (t.IndexOf("S7-400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("S7400", StringComparison.OrdinalIgnoreCase) >= 0 || tCompact.IndexOf("6ES741", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "S7400";
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static object? SelectCommunicationDriverEnumValue(Type enumType, string plcFamily)
        {
            object? best = null;
            var bestScore = -1;
            foreach (var name in Enum.GetNames(enumType))
            {
                var u = name.ToUpperInvariant();
                var score = 0;
                if (plcFamily == "S71200" || plcFamily == "S71500" || plcFamily == "UNKNOWN")
                {
                    if (u.Contains("300") && !u.Contains("1500")) continue;
                    if (u.Contains("400") && !u.Contains("1500")) continue;
                    if (u.Contains("318") || u.Contains("319")) continue;
                    if (u.Contains("1200") || u.Contains("1500") || u.Contains("S712") || u.Contains("S715") || u.Contains("PLUS"))
                        score += 10;
                    if (u.Contains("UNIFIED") || u.Contains("PLUS")) score += 2;
                }
                else if (plcFamily == "S7300")
                {
                    if (u.Contains("300") || u.Contains("318") || u.Contains("319")) score += 10;
                }
                else if (plcFamily == "S7400")
                {
                    if (u.Contains("400") || u.Contains("414") || u.Contains("416")) score += 10;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = Enum.Parse(enumType, name);
                }
            }

            return bestScore > 0 ? best : null;
        }

        private static void TryConfigureUnifiedDriverProperties(object connection, string plcFamily)
        {
            try
            {
                var dps = TryGetPropertyValue(connection, "DriverProperties");
                if (dps is not IEnumerable en) return;

                foreach (var dp in en)
                {
                    if (dp == null) continue;
                    var n = TryGetPropertyValue(dp, "Name")?.ToString() ?? TryGetName(dp) ?? string.Empty;
                    var nu = n.ToUpperInvariant();
                    if (nu.Contains("DRIVER") || nu.Contains("FAMILY") || nu.Contains("CPU") || nu.Contains("CONTROLLER"))
                    {
                        if (plcFamily == "S71200" || plcFamily == "S71500" || plcFamily == "UNKNOWN")
                        {
                            TrySetProperty(dp, "Value", "SIMATIC S7-1200/1500");
                            TrySetEngineeringAttribute(dp, "Value", "SIMATIC S7-1200/1500");
                        }
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// WinCC Unified HMI connection: pick CommunicationDriver enum / attribute that matches the PLC hardware.
        /// </summary>
        private void TryConfigureUnifiedHmiCommunicationDriver(object connection, string plcSoftwarePath)
        {
            var plcFamily = InferUnifiedPlcFamilyFromSoftwarePath(plcSoftwarePath);

            try
            {
                var prop = connection.GetType().GetProperty("CommunicationDriver", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite && prop.PropertyType.IsEnum)
                {
                    var ev = SelectCommunicationDriverEnumValue(prop.PropertyType, plcFamily);
                    if (ev != null)
                    {
                        prop.SetValue(connection, ev);
                        return;
                    }
                }
            }
            catch
            {
            }

            // CommunicationDriver is commonly exposed as an engineering attribute typed as an enum; string writes fail.
            if (TrySetUnifiedHmiCommunicationDriverEnum(connection, plcFamily))
            {
                return;
            }

            var driverCandidates = plcFamily switch
            {
                "S7300" => new[] { "SIMATIC S7 300/400", "SIMATIC S7-300/400", "SIMATIC S7 300", "SIMATIC S7-300" },
                "S7400" => new[] { "SIMATIC S7 400", "SIMATIC S7-400", "SIMATIC S7 300/400", "SIMATIC S7-300/400" },
                _ => new[]
                {
                    "SIMATIC S7-1200/1500",
                    "SIMATIC S7 1200/1500",
                    "SIMATIC S7-1200",
                    "SIMATIC S7 1200",
                    "SIMATIC S7-1500",
                    "SIMATIC S7 1500",
                    "S7-1200/1500",
                    "S7-1200",
                    "S7-1500",
                    "S71200",
                    "S71500"
                }
            };

            foreach (var driver in driverCandidates)
            {
                if (TrySetProperty(connection, "CommunicationDriver", driver) ||
                    TrySetEngineeringAttribute(connection, "CommunicationDriver", driver))
                {
                    return;
                }
            }

            TrySetCommunicationDriverFromAttributeInfos(connection, driverCandidates);

            TryConfigureUnifiedDriverProperties(connection, plcFamily);
        }

        private static void ValidateUnifiedHmiCommunicationDriver(object connection, string plcFamily)
        {
            var driver = ReadUnifiedHmiCommunicationDriver(connection);
            var normalized = (driver ?? string.Empty).ToUpperInvariant().Replace("-", "").Replace(" ", "");
            if (plcFamily == "S7300" || plcFamily == "S7400") return;

            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new InvalidOperationException("HMI connection CommunicationDriver did not read back. S7-1200/S7-1500 projects must read back a 1200/1500 driver before HMI tags are created.");
            }

            if (normalized.Contains("300/400") || normalized.Contains("S7300") || normalized.Contains("S7400"))
            {
                throw new InvalidOperationException($"HMI connection CommunicationDriver read back as '{driver}', but the PLC family is {plcFamily}. Use SIMATIC S7-1200/1500 for S7-1200/S7-1500 projects.");
            }
        }

        private static string ReadUnifiedHmiCommunicationDriver(object connection)
        {
            foreach (var name in new[] { "CommunicationDriver", "Driver", "Protocol" })
            {
                try
                {
                    var prop = connection.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && prop.CanRead)
                    {
                        var value = prop.GetValue(connection)?.ToString();
                        if (!string.IsNullOrWhiteSpace(value)) return value!;
                    }
                }
                catch
                {
                }

                var attr = TryGetEngineeringAttribute(connection, name)?.ToString();
                if (!string.IsNullOrWhiteSpace(attr)) return attr!;
            }

            return string.Empty;
        }

        /// <summary>
        /// Some Unified builds expose the driver only under a localized or version-specific engineering attribute name.
        /// </summary>
        private static void TrySetCommunicationDriverFromAttributeInfos(object connection, string[] driverCandidates)
        {
            try
            {
                var getInfos = connection.GetType().GetMethod("GetAttributeInfos", Type.EmptyTypes);
                if (getInfos == null) return;
                var infos = getInfos.Invoke(connection, null) as System.Collections.IEnumerable;
                if (infos == null) return;
                foreach (var info in infos)
                {
                    if (info == null) continue;
                    var n = TryGetPropertyValue(info, "Name")?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    var nu = n.ToUpperInvariant();
                    if (!nu.Contains("COMMUNICATIONDRIVER") && !nu.Contains("DRIVER") && !n.Contains("通信")) continue;
                    foreach (var driver in driverCandidates)
                    {
                        try
                        {
                            if (TrySetEngineeringAttribute(connection, n, driver)) return;
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void ApplyJsonProperties(object target, JsonObject props, JsonArray failed, string path, string typeHint = "")
        {
            foreach (var kv in props)
            {
                var schemaError = ValidateUnifiedHmiDesignProperty(typeHint, kv.Key);
                if (!string.IsNullOrEmpty(schemaError))
                {
                    failed.Add($"{path}.{kv.Key}: {schemaError}");
                    continue;
                }

                var value = JsonObjectValue(kv.Value);
                if (TrySetProperty(target, kv.Key, value)) continue;
                if (TrySetEngineeringAttribute(target, kv.Key, value)) continue;
                failed.Add($"{path}.{kv.Key}: property/attribute write failed");
            }
        }

        private static string ValidateUnifiedHmiDesignProperty(string typeHint, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName)) return "property name is empty";
            var type = (typeHint ?? string.Empty).Trim();
            var prop = propertyName.Trim();

            if (type.Equals("Rectangle", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("Lamp", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("HmiRectangle", StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Equals("ForeColor", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Text", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Font", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Content", StringComparison.OrdinalIgnoreCase) ||
                    prop.Equals("Padding", StringComparison.OrdinalIgnoreCase))
                {
                    return "unsupported on Rectangle. Use a separate HmiText item for text/foreground/font, and keep Rectangle for BackColor/BorderColor/BorderWidth.";
                }
            }

            if (type.Equals("IOField", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("HmiIOField", StringComparison.OrdinalIgnoreCase))
            {
                var stable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "BackColor", "ForeColor", "BorderColor", "BorderWidth", "Visible", "Enabled", "Name"
                };
                if (!stable.Contains(prop))
                    return "not in the stable IOField property set for generated screens. Bind runtime values with BindUnifiedHmiTagDynamization instead of ad-hoc ProcessValue properties.";
            }

            return string.Empty;
        }

        private static bool TrySetEngineeringAttribute(object target, string attributeName, object? value)
        {
            try
            {
                var get = target.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                var set = target.GetType().GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
                if (set == null) return false;

                object? oldValue = null;
                try { oldValue = get?.Invoke(target, new object[] { attributeName }); } catch { }
                var typed = oldValue == null ? value : CoerceReflectionValue(value, oldValue.GetType());
                set.Invoke(target, new[] { attributeName, typed });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? JsonString(JsonObject obj, string propertyName)
        {
            var node = obj[propertyName];
            if (node == null) return null;
            if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            return node.ToJsonString();
        }

        private static object? JsonObjectValue(JsonNode? node)
        {
            if (node == null) return null;
            if (node is JsonValue value)
            {
                if (value.TryGetValue<string>(out var s)) return s;
                if (value.TryGetValue<bool>(out var b)) return b;
                if (value.TryGetValue<int>(out var i)) return i;
                if (value.TryGetValue<long>(out var l)) return l;
                if (value.TryGetValue<double>(out var d)) return d;
                return value.ToJsonString();
            }

            return node.ToJsonString();
        }

        private static bool? IsAttributeWritable(object attributeInfo)
        {
            var names = new[] { "AccessMode", "Access", "Mode" };
            foreach (var name in names)
            {
                var value = TryGetPropertyValue(attributeInfo, name)?.ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (value!.IndexOf("ReadWrite", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (value.IndexOf("Write", StringComparison.OrdinalIgnoreCase) >= 0 && value.IndexOf("ReadOnly", StringComparison.OrdinalIgnoreCase) < 0) return true;
                if (value.IndexOf("ReadOnly", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            return null;
        }

        private static object CoerceAttributeValue(string value, object? oldValue, object attributeInfo)
        {
            if (oldValue != null)
            {
                var oldType = oldValue.GetType();
                if (oldType == typeof(string)) return value;
                if (oldType == typeof(bool)) return bool.Parse(value);
                if (oldType == typeof(int)) return int.Parse(value);
                if (oldType == typeof(uint)) return uint.Parse(value);
                if (oldType == typeof(short)) return short.Parse(value);
                if (oldType == typeof(ushort)) return ushort.Parse(value);
                if (oldType == typeof(long)) return long.Parse(value);
                if (oldType == typeof(ulong)) return ulong.Parse(value);
                if (oldType == typeof(float)) return float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                if (oldType == typeof(double)) return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                if (oldType.IsEnum) return Enum.Parse(oldType, value, ignoreCase: true);
            }

            var dataType = TryGetPropertyValue(attributeInfo, "DataType", "Type")?.ToString() ?? string.Empty;
            if (dataType.IndexOf("Boolean", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("Bool", StringComparison.OrdinalIgnoreCase)) return bool.Parse(value);
            if (dataType.IndexOf("Int32", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("Int", StringComparison.OrdinalIgnoreCase)) return int.Parse(value);
            if (dataType.IndexOf("UInt32", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("UInt", StringComparison.OrdinalIgnoreCase)) return uint.Parse(value);
            if (dataType.IndexOf("Double", StringComparison.OrdinalIgnoreCase) >= 0 || dataType.Equals("Real", StringComparison.OrdinalIgnoreCase)) return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

            return value;
        }

        private static object? CoerceReflectionValue(object? value, Type targetType)
        {
            if (value == null) return null;

            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null) targetType = nullableType;
            if (targetType.IsInstanceOfType(value)) return value;

            if (targetType == typeof(string)) return value.ToString();
            if (targetType.IsEnum) return value is string enumText
                ? Enum.Parse(targetType, enumText, ignoreCase: true)
                : Enum.ToObject(targetType, value);
            if (targetType == typeof(bool)) return value is string boolText ? bool.Parse(boolText) : Convert.ToBoolean(value);
            if (targetType == typeof(byte)) return value is string byteText ? byte.Parse(byteText) : Convert.ToByte(value);
            if (targetType == typeof(short)) return value is string shortText ? short.Parse(shortText) : Convert.ToInt16(value);
            if (targetType == typeof(ushort)) return value is string ushortText ? ushort.Parse(ushortText) : Convert.ToUInt16(value);
            if (targetType == typeof(int)) return value is string intText ? int.Parse(intText) : Convert.ToInt32(value);
            if (targetType == typeof(uint)) return value is string uintText ? uint.Parse(uintText) : Convert.ToUInt32(value);
            if (targetType == typeof(long)) return value is string longText ? long.Parse(longText) : Convert.ToInt64(value);
            if (targetType == typeof(ulong)) return value is string ulongText ? ulong.Parse(ulongText) : Convert.ToUInt64(value);
            if (targetType == typeof(float)) return value is string floatText ? float.Parse(floatText, System.Globalization.CultureInfo.InvariantCulture) : Convert.ToSingle(value);
            if (targetType == typeof(double)) return value is string doubleText ? double.Parse(doubleText, System.Globalization.CultureInfo.InvariantCulture) : Convert.ToDouble(value);
            if (targetType == typeof(Color)) return CoerceColor(value);

            return value;
        }

        private static Color CoerceColor(object value)
        {
            if (value is Color c) return c;

            if (value is string s)
            {
                var text = s.Trim();
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return Color.FromArgb(unchecked((int)Convert.ToUInt32(text.Substring(2), 16)));
                }
                if (text.StartsWith("#", StringComparison.Ordinal))
                {
                    return ColorTranslator.FromHtml(text);
                }
                if (Regex.IsMatch(text, "^[0-9A-Fa-f]{8}$"))
                {
                    return Color.FromArgb(unchecked((int)Convert.ToUInt32(text, 16)));
                }
                return ColorTranslator.FromHtml(text);
            }

            if (value is long l) return Color.FromArgb(unchecked((int)l));
            if (value is int i) return Color.FromArgb(i);
            if (value is uint ui) return Color.FromArgb(unchecked((int)ui));

            return Color.FromArgb(Convert.ToInt32(value));
        }

        private static IEnumerable<string> TryGetEnumerableStrings(object target, string propertyName)
        {
            try
            {
                var value = TryGetPropertyValue(target, propertyName);
                if (value is IEnumerable en)
                {
                    foreach (var item in en)
                    {
                        if (item != null) yield return item.ToString() ?? string.Empty;
                    }
                }
            }
            finally
            {
            }
        }

        private static JsonArray ToJsonArray(IEnumerable<string> values)
        {
            var arr = new JsonArray();
            foreach (var value in values)
            {
                arr.Add(value);
            }
            return arr;
        }

        private static string FormatExceptionDetail(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
            {
                return $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}\n{tie.InnerException}";
            }

            if (ex.InnerException != null)
            {
                return $"{ex.GetType().FullName}: {ex.Message}\nInner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex}";
            }

            return $"{ex.GetType().FullName}: {ex.Message}\n{ex}";
        }

        #endregion
    }
}
