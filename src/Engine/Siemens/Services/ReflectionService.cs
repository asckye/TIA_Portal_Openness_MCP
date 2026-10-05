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
    internal sealed class ReflectionService
    {
        private readonly IEngineeringSession _session;

        public ReflectionService(IEngineeringSession session) => _session = session;

        private static object? GetPropertyPathValue(object root, string propertyPath)
        {
            var read = PropertyPathReader.Read(root, propertyPath);
            if (!read.Success) throw new PortalException(PortalErrorCode.InvalidParams,
                $"{read.Status}: segment='{read.Segment}', type='{read.ResolvedType}'. {read.Error}");
            return read.Value;
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeObject(string objectKind, string objectPath, string softwarePath = "", int maxMembers = 200)
        {
            var o = _session.ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                TypeName = o.GetType().FullName ?? o.GetType().Name,
                Members = _session.DescribeMembers(o, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "", int maxMembers = 200)
        {
            var o = _session.ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }

            var v = GetPropertyPathValue(o, propertyPath);
            if (v == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Property value is null",
                    Meta = ResponseMeta.Unstamped(true, ("status", "Value"), ("valueIsNull", true)),
                    ObjectKind = objectKind,
                    ObjectPath = $"{objectPath}.{propertyPath}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = objectKind,
                ObjectPath = $"{objectPath}.{propertyPath}",
                TypeName = v.GetType().FullName ?? v.GetType().Name,
                Members = _session.DescribeMembers(v, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectValue GetObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "")
        {
            var o = _session.ResolveObject(objectKind, objectPath, softwarePath);
            var read = PropertyPathReader.Read(o, propertyPath);
            if (!read.Success) return new ModelContextProtocol.ResponseObjectValue {
                Message = read.Status + (o == null ? _session.PlcLookupPathsSuffix ?? string.Empty : string.Empty), ObjectKind = objectKind, ObjectPath = objectPath, Meta = read.Metadata() };
            var v = read.Value;
            var vt = v?.GetType();

            object? outValue = v;
            if (v != null && v.GetType().Name == "MultilingualText")
            {
                outValue = UnifiedMultilingualText.Read(v);
            }
            else if (v is IEnumerable enumerable && v is not string)
            {
                var items = new List<string>();
                foreach (var it in enumerable)
                {
                    if (it == null) continue;
                    items.Add(_session.TryGetName(it) ?? it.ToString() ?? "");
                    if (items.Count >= 200) break;
                }
                outValue = items;
            }

            return new ModelContextProtocol.ResponseObjectValue
            {
                Message = "OK",
                Meta = read.Metadata(),
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                ValueType = vt?.FullName ?? (v == null ? null : v.GetType().Name),
                Value = outValue
            };
        }

        public ModelContextProtocol.ResponseObjectChildren ListObjectChildren(string objectKind, string objectPath, string collectionProperty, string softwarePath = "", int limit = 200)
        {
            var o = _session.ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }

            var v = GetPropertyPathValue(o, collectionProperty);
            if (v is not IEnumerable enumerable || v is string)
            {
                return new ModelContextProtocol.ResponseObjectChildren
                {
                    Message = "Collection not found or not enumerable",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    Collection = collectionProperty,
                    Items = Array.Empty<string>()
                };
            }

            var items = new List<string>();
            foreach (var it in enumerable)
            {
                if (it == null) continue;
                items.Add(_session.TryGetName(it) ?? it.ToString() ?? "");
                if (items.Count >= Math.Max(1, Math.Min(2000, limit))) break;
            }

            return new ModelContextProtocol.ResponseObjectChildren
            {
                Message = "OK",
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                Collection = collectionProperty,
                Items = items
            };
        }

        private ModelContextProtocol.ResponseObjectValue InvokeOnInstance(object instance, string resultKind, string resultPath, string methodName, JsonArray? args, bool allowWrite)
        {
            var hardDenyReason = GetHardDeniedReflectionReason(instance, resultKind, resultPath, methodName);
            if (!string.IsNullOrWhiteSpace(hardDenyReason))
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = hardDenyReason,
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }

            var safe = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ToString",
                "GetAttribute",
                "GetAttributeInfos"
            };

            if (!allowWrite && !safe.Contains(methodName))
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Method not allowed (read-only mode)",
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }

            var argValues = new List<object?>();
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (a == null) { argValues.Add(null); continue; }
                    if (a is JsonValue jv)
                    {
                        if (jv.TryGetValue<string>(out var s)) { argValues.Add(s); continue; }
                        if (jv.TryGetValue<int>(out var i)) { argValues.Add(i); continue; }
                        if (jv.TryGetValue<long>(out var l)) { argValues.Add(l); continue; }
                        if (jv.TryGetValue<double>(out var d)) { argValues.Add(d); continue; }
                        if (jv.TryGetValue<bool>(out var b)) { argValues.Add(b); continue; }
                        argValues.Add(jv.ToString());
                        continue;
                    }
                    argValues.Add(a.ToString());
                }
            }

            try
            {
                var t = instance.GetType();
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName && m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                MethodInfo? mi = methods.FirstOrDefault(m => m.GetParameters().Length == argValues.Count);
                if (mi == null)
                {
                    return new ModelContextProtocol.ResponseObjectValue
                    {
                        Message = "Method not found (signature mismatch)",
                        ObjectKind = resultKind,
                        ObjectPath = resultPath
                    };
                }

                var ps = mi.GetParameters();
                var converted = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var av = argValues[i];
                    if (av == null) { converted[i] = null; continue; }
                    var pt = ps[i].ParameterType;
                    if (pt == typeof(string)) { converted[i] = av.ToString(); continue; }
                    if (pt == typeof(int)) { converted[i] = Convert.ToInt32(av); continue; }
                    if (pt == typeof(long)) { converted[i] = Convert.ToInt64(av); continue; }
                    if (pt == typeof(double)) { converted[i] = Convert.ToDouble(av); continue; }
                    if (pt == typeof(bool)) { converted[i] = Convert.ToBoolean(av); continue; }
                    if (pt == typeof(System.IO.DirectoryInfo) && av is string directory) { converted[i] = new System.IO.DirectoryInfo(directory); continue; }
                    if (pt == typeof(System.IO.FileInfo) && av is string file) { converted[i] = new System.IO.FileInfo(file); continue; }
                    // Convert enum parameters by name (PlcProtectionAccessLevel, ImportOptions, ...) and SecureString parameters from a
                    // plain string (SetPassword / Protect / LoginToSafetyOfflineProgram) - the bridge could reach neither before.
                    if (pt.IsEnum && av is string enumName) { converted[i] = Enum.Parse(pt, enumName, true); continue; }
                    if (pt == typeof(SecureString) && av is string secret) { converted[i] = ProjectSecurityLogic.Secure(secret); continue; }
                    if (pt == typeof(object)
                        && methodName.Equals("SetAttribute", StringComparison.OrdinalIgnoreCase)
                        && i == 1
                        && argValues.Count >= 2
                        && argValues[0] is string attrName)
                    {
                        var oldValue = instance.GetType()
                            .GetMethod("GetAttribute", new[] { typeof(string) })
                            ?.Invoke(instance, new object[] { attrName });
                        converted[i] = oldValue == null ? av : _session.CoerceReflectionValue(av, oldValue.GetType());
                        continue;
                    }
                    converted[i] = av;
                }

                var result = mi.Invoke(instance, converted);

                object? outValue = result;
                if (result is IEnumerable enumerable && result is not string)
                {
                    var items = new List<string>();
                    foreach (var it in enumerable)
                    {
                        if (it == null) continue;
                        items.Add(_session.TryGetName(it) ?? it.ToString() ?? "");
                        if (items.Count >= 200) break;
                    }
                    outValue = items;
                }

                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "OK",
                    ObjectKind = resultKind,
                    ObjectPath = resultPath,
                    ValueType = result?.GetType().FullName ?? (result == null ? null : result.GetType().Name),
                    Value = outValue
                };
            }
            catch (TargetInvocationException tie)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = tie.InnerException?.Message ?? tie.Message,
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }
            catch (Exception ex)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = ex.Message,
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }
        }

        private static string? GetHardDeniedReflectionReason(object instance, string resultKind, string resultPath, string methodName)
        {
            var instanceType = instance.GetType().FullName ?? instance.GetType().Name;
            var crossReferenceRefusal = CrossReferenceGuardLogic.ReflectionRefusal(instanceType, methodName);
            if (crossReferenceRefusal != null) return crossReferenceRefusal;
            var haystack = string.Join(" ", instanceType, resultKind ?? "", resultPath ?? "", methodName ?? "");

            if (haystack.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Denied by safety policy: force-table and force-related operations are not exposed through this MCP server.";
            }

            var isOnlineMonitorSurface =
                haystack.IndexOf("Online", StringComparison.OrdinalIgnoreCase) >= 0 ||
                haystack.IndexOf("Monitor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                haystack.IndexOf("Watch", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isOnlineMonitorSurface)
            {
                return null;
            }

            var mutatingPrefixes = new[]
            {
                "Set",
                "Write",
                "Create",
                "Delete",
                "Remove",
                "Import",
                "Add",
                "Insert",
                "Update",
                "Modify",
                "GoOnline",
                "GoOffline",
                "Download",
                "Activate",
                "Start",
                "Stop"
            };

            if (mutatingPrefixes.Any(p => (methodName ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                return "Denied by safety policy: online/watch/monitor surfaces are read-only. The MCP server may read current status only and must not modify watch-table objects or PLC values.";
            }

            return null;
        }

        public ModelContextProtocol.ResponseObjectValue InvokeObject(string objectKind, string objectPath, string methodName, JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
        {
            _session.DenyCrossReferenceReflection(null, methodName);
            var o = _session.ResolveObject(objectKind, objectPath, softwarePath, allowWrite ? PlcAccess.Write : PlcAccess.Read);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }
            return InvokeOnInstance(o, objectKind, objectPath, methodName, args, allowWrite);
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeService(string objectKind, string objectPath, string serviceTypeSuffix, string softwarePath = "", int maxMembers = 200)
        {
            _session.DenyCrossReferenceReflection(serviceTypeSuffix, null);
            if ((serviceTypeSuffix ?? string.Empty).IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Denied by safety policy: force-related services are not exposed through this MCP server.",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var o = _session.ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }

            var st = _session.FindTypeBySuffix(serviceTypeSuffix!);
            if (st == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Service type not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            _session.DenyCrossReferenceReflection(st.FullName, null);
            var svc = _session.TryGetService(o, st);
            if (svc == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "GetService failed (service not available for this object)",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = st.FullName ?? st.Name,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "Service",
                ObjectPath = $"{objectKind}:{objectPath}::{serviceTypeSuffix}",
                TypeName = svc.GetType().FullName ?? svc.GetType().Name,
                Members = _session.DescribeMembers(svc, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectValue InvokeService(string objectKind, string objectPath, string serviceTypeSuffix, string methodName, JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
        {
            _session.DenyCrossReferenceReflection(serviceTypeSuffix, methodName);
            if ((serviceTypeSuffix ?? string.Empty).IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Denied by safety policy: force-related services are not exposed through this MCP server.",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var o = _session.ResolveObject(objectKind, objectPath, softwarePath, allowWrite ? PlcAccess.Write : PlcAccess.Read);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PortalException(PortalErrorCode.NotFound,
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }

            var st = _session.FindTypeBySuffix(serviceTypeSuffix!);
            if (st == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Service type not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            _session.DenyCrossReferenceReflection(st.FullName, null);
            var svc = _session.TryGetService(o, st);
            if (svc == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "GetService failed (service not available for this object)",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var svcPath = $"{objectKind}:{objectPath}::{serviceTypeSuffix}";
            return InvokeOnInstance(svc, "Service", svcPath, methodName, args, allowWrite);
        }
    }
}
