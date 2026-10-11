using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Reflection
{
    internal sealed class OpennessReflection
    {
        private readonly ReflectionSession _session;

        public OpennessReflection(ReflectionSession session) => _session = session;

        private static object? GetPropertyPathValue(object root, string propertyPath)
        {
            var read = PropertyPathReader.Read(root, propertyPath);
            if (!read.Success) throw new PlcSoftwareException("InvalidParams",
                $"{read.Status}: segment='{read.Segment}', type='{read.ResolvedType}'. {read.Error}");
            return read.Value;
        }

        public OpennessObservation DescribeObject(string objectKind, string objectPath, string softwarePath = "", int maxMembers = 200)
        {
            var o = _session.ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PlcSoftwareException("NotFound",
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }

            return new OpennessObservation
            {
                Message = "OK",
                Meta = ReflectionSession.Meta(true),
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                TypeName = o.GetType().FullName ?? o.GetType().Name,
                Members = _session.DescribeMembers(o, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public OpennessObservation DescribeObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "", int maxMembers = 200)
        {
            var o = _session.ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PlcSoftwareException("NotFound",
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }

            var v = GetPropertyPathValue(o, propertyPath);
            if (v == null)
            {
                return new OpennessObservation
                {
                    Message = "Property value is null",
                    Meta = ReflectionSession.Meta(true, ("status", "Value"), ("valueIsNull", true)),
                    ObjectKind = objectKind,
                    ObjectPath = $"{objectPath}.{propertyPath}",
                    TypeName = null,
                    Members = Array.Empty<OpennessMember>()
                };
            }

            return new OpennessObservation
            {
                Message = "OK",
                Meta = ReflectionSession.Meta(true),
                ObjectKind = objectKind,
                ObjectPath = $"{objectPath}.{propertyPath}",
                TypeName = v.GetType().FullName ?? v.GetType().Name,
                Members = _session.DescribeMembers(v, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public OpennessObservation GetObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "")
        {
            var o = _session.ResolveObject(objectKind, objectPath, softwarePath);
            var read = PropertyPathReader.Read(o, propertyPath);
            if (!read.Success) return new OpennessObservation {
                Message = read.Status + (o == null ? _session.PlcLookupPathsSuffix ?? string.Empty : string.Empty), ObjectKind = objectKind, ObjectPath = objectPath, Meta = read.Metadata() };
            var v = read.Value;
            var vt = v?.GetType();

            object? outValue = v;
            if (v != null && v.GetType().Name == "MultilingualText")
            {
                outValue = ReflectionSession.Multilingual(v);
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

            return new OpennessObservation
            {
                Message = "OK",
                Meta = read.Metadata(),
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                ValueType = vt?.FullName ?? (v == null ? null : v.GetType().Name),
                Value = outValue
            };
        }

        public OpennessObservation ListObjectChildren(string objectKind, string objectPath, string collectionProperty, string softwarePath = "", int limit = 200)
        {
            var o = _session.ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PlcSoftwareException("NotFound",
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }

            var v = GetPropertyPathValue(o, collectionProperty);
            if (v is not IEnumerable enumerable || v is string)
            {
                return new OpennessObservation
                {
                    Message = "Collection not found or not enumerable",
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY")),
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

            return new OpennessObservation
            {
                Message = "OK",
                Meta = ReflectionSession.Meta(true),
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                Collection = collectionProperty,
                Items = items
            };
        }

        private OpennessObservation InvokeOnInstance(object instance, string resultKind, string resultPath, string methodName, HardwareScalar[]? args, bool allowWrite)
        {
            var hardDenyReason = GetHardDeniedReflectionReason(instance, resultKind, resultPath, methodName);
            if (!string.IsNullOrWhiteSpace(hardDenyReason))
            {
                return new OpennessObservation
                {
                    Message = hardDenyReason,
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "ACCESS_DENIED")),
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
                return new OpennessObservation
                {
                    Message = "Method not allowed (read-only mode)",
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "ACCESS_DENIED")),
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
                    argValues.Add(ReflectionSession.Argument(a));
                }
            }

            bool invocationAttempted = false;
            try
            {
                var t = instance.GetType();
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName && m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                MethodInfo? mi = methods.FirstOrDefault(m => m.GetParameters().Length == argValues.Count);
                if (mi == null)
                {
                    return new OpennessObservation
                    {
                        Message = "Method not found (signature mismatch)",
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "NOT_FOUND")),
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
                    if (pt == typeof(System.IO.DirectoryInfo) && av is string directory) { converted[i] = new System.IO.DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(directory)); continue; }
                    if (pt == typeof(System.IO.FileInfo) && av is string file) { converted[i] = new System.IO.FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(file)); continue; }
                    // Convert enum parameters by name (PlcProtectionAccessLevel, ImportOptions, ...) and SecureString parameters from a
                    // plain string (SetPassword / Protect / LoginToSafetyOfflineProgram) - the bridge could reach neither before.
                    if (pt.IsEnum && av is string enumName) { converted[i] = Enum.Parse(pt, enumName, true); continue; }
                    if (pt == typeof(SecureString) && av is string secret) { converted[i] = ReflectionSession.Secure(secret); continue; }
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

                invocationAttempted = true;
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

                return new OpennessObservation
                {
                    Message = "OK",
                    Meta = ReflectionSession.Meta(true, ("invocationAttempted", true)),
                    ObjectKind = resultKind,
                    ObjectPath = resultPath,
                    ValueType = result?.GetType().FullName ?? (result == null ? null : result.GetType().Name),
                    Value = outValue
                };
            }
            catch (TargetInvocationException tie)
            {
                return new OpennessObservation
                {
                    Message = tie.InnerException?.Message ?? tie.Message,
                    Meta = ReflectionSession.Meta(false, ("invocationAttempted", invocationAttempted),
                        ("v4Rejection", invocationAttempted ? null : "INVALID_ARGUMENT")),
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }
            catch (Exception ex)
            {
                return new OpennessObservation
                {
                    Message = ex.Message,
                    Meta = ReflectionSession.Meta(false, ("invocationAttempted", invocationAttempted),
                        ("v4Rejection", invocationAttempted ? null : "INVALID_ARGUMENT")),
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }
        }

        private static string? GetHardDeniedReflectionReason(object instance, string resultKind, string resultPath, string methodName)
        {
            var instanceType = instance.GetType().FullName ?? instance.GetType().Name;
            var crossReferenceRefusal = ReflectionSession.ReflectionRefusal(instanceType, methodName);
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

        public OpennessObservation InvokeObject(string objectKind, string objectPath, string methodName, HardwareScalar[]? args = null, string softwarePath = "", bool allowWrite = false)
        {
            _session.DenyCrossReferenceReflection(null, methodName);
            var o = _session.ResolveObject(objectKind, objectPath, softwarePath, allowWrite ? true : false);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PlcSoftwareException("NotFound",
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }
            return InvokeOnInstance(o, objectKind, objectPath, methodName, args, allowWrite);
        }

        public OpennessObservation DescribeService(string objectKind, string objectPath, string serviceTypeSuffix, string softwarePath = "", int maxMembers = 200)
        {
            _session.DenyCrossReferenceReflection(serviceTypeSuffix, null);
            if ((serviceTypeSuffix ?? string.Empty).IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new OpennessObservation
                {
                    Message = "Denied by safety policy: force-related services are not exposed through this MCP server.",
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "ACCESS_DENIED")),
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = null,
                    Members = Array.Empty<OpennessMember>()
                };
            }

            var o = _session.ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PlcSoftwareException("NotFound",
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }

            var st = _session.FindTypeBySuffix(serviceTypeSuffix!);
            if (st == null)
            {
                return new OpennessObservation
                {
                    Message = "Service type not found",
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "NOT_FOUND")),
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = null,
                    Members = Array.Empty<OpennessMember>()
                };
            }

            _session.DenyCrossReferenceReflection(st.FullName, null);
            var svc = _session.TryGetService(o, st);
            if (svc == null)
            {
                return new OpennessObservation
                {
                    Message = "GetService failed (service not available for this object)",
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY")),
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = st.FullName ?? st.Name,
                    Members = Array.Empty<OpennessMember>()
                };
            }

            return new OpennessObservation
            {
                Message = "OK",
                Meta = ReflectionSession.Meta(true),
                ObjectKind = "Service",
                ObjectPath = $"{objectKind}:{objectPath}::{serviceTypeSuffix}",
                TypeName = svc.GetType().FullName ?? svc.GetType().Name,
                Members = _session.DescribeMembers(svc, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public OpennessObservation InvokeService(string objectKind, string objectPath, string serviceTypeSuffix, string methodName, HardwareScalar[]? args = null, string softwarePath = "", bool allowWrite = false)
        {
            _session.DenyCrossReferenceReflection(serviceTypeSuffix, methodName);
            if ((serviceTypeSuffix ?? string.Empty).IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new OpennessObservation
                {
                    Message = "Denied by safety policy: force-related services are not exposed through this MCP server.",
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "ACCESS_DENIED")),
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var o = _session.ResolveObject(objectKind, objectPath, softwarePath, allowWrite ? true : false);
            if (o == null)
            {
                // 路径未解析必须失败，不能用空成员表表示一个不存在的对象。
                throw new PlcSoftwareException("NotFound",
                    $"{objectKind} '{objectPath}' not found. Resolve the exact path first "
                    + "(GetProjectTree / GetDeviceItemTree / GetSoftwareTree / GetPlcBlockHierarchy); "
                    + "for objectKind=Block/Type also pass softwarePath." + (_session.PlcLookupPathsSuffix ?? string.Empty));
            }

            var st = _session.FindTypeBySuffix(serviceTypeSuffix!);
            if (st == null)
            {
                return new OpennessObservation
                {
                    Message = "Service type not found",
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "NOT_FOUND")),
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            _session.DenyCrossReferenceReflection(st.FullName, null);
            var svc = _session.TryGetService(o, st);
            if (svc == null)
            {
                return new OpennessObservation
                {
                    Message = "GetService failed (service not available for this object)",
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "UNSUPPORTED_CAPABILITY")),
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var svcPath = $"{objectKind}:{objectPath}::{serviceTypeSuffix}";
            return InvokeOnInstance(svc, "Service", svcPath, methodName, args, allowWrite);
        }
    }
}
