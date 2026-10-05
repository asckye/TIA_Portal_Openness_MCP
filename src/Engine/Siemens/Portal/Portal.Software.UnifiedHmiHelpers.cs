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
    public partial class Portal
    {

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
            catch /* swallow(native-fallback): Unsupported property conversion or assignment reports false to the existing caller fallback. */
            {
                return false;
            }
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

        // Tag tables in nested user folders (TagUserFolder.Folders) must participate in lookup. A real classic TP700
        // import into MCP_TagFolder was missed by root-only lookup; TIA version and test date were not recorded.
        // Evidence index: docs/reference/real-machine-ledger.md.
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
            catch /* swallow(enumerate-optional): Unavailable collection enumeration preserves the missing-item fallback. */ { }

            return null;
        }

        private static object? TryGetEngineeringAttribute(object target, string attributeName)
        {
            try
            {
                var get = target.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                return get?.Invoke(target, new object[] { attributeName });
            }
            catch /* swallow(probe-optional): An unavailable engineering attribute is represented by null for the existing fallback. */
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
                catch /* swallow(probe-optional): Unreadable scalar properties fall through to engineering attribute readback. */ { }

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

        internal sealed class UnifiedHmiPlcPartnerInfo
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
    }
}
