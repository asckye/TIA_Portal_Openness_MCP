using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Reflection
{
    internal sealed class OpennessReflection
    {
        private readonly ReflectionSession _session;

        public OpennessReflection(ReflectionSession session) => _session = session;

        private static object? GetPropertyPathValue(object root, string propertyPath, string? parameter = null)
        {
            var read = PropertyPathReader.Read(root, propertyPath);
            if (!read.Success) throw new PlcSoftwareException("InvalidParams",
                parameter == "collectionProperty"
                    ? $"The collection path stopped at '{read.Segment}' ({read.Status}). Inspect its public properties with DescribeObject."
                    : $"{read.Status}: segment='{read.Segment}', type='{read.ResolvedType}'. {read.Error}")
                { Data = { ["parameter"] = parameter } };
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
                Meta = ReflectionSession.Meta(true, ("totalMembers", _session.CountMembers(o)),
                    ("truncated", _session.CountMembers(o) > MemberListCap.Effective(maxMembers))),
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                TypeName = o.GetType().FullName ?? o.GetType().Name,
                Members = _session.DescribeMembers(o, MemberListCap.Effective(maxMembers)).ToList()
            };
        }

        public OpennessObservation DescribeObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "", int maxMembers =
                200)
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
                Meta = ReflectionSession.Meta(true, ("totalMembers", _session.CountMembers(v)),
                    ("truncated", _session.CountMembers(v) > MemberListCap.Effective(maxMembers))),
                ObjectKind = objectKind,
                ObjectPath = $"{objectPath}.{propertyPath}",
                TypeName = v.GetType().FullName ?? v.GetType().Name,
                Members = _session.DescribeMembers(v, MemberListCap.Effective(maxMembers)).ToList()
            };
        }

        public OpennessObservation GetObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "")
        {
            var o = _session.ResolveObject(objectKind, objectPath, softwarePath);
            var read = PropertyPathReader.Read(o, propertyPath);
            if (!read.Success) return new OpennessObservation {
                Message = read.Status + (o == null ? _session.PlcLookupPathsSuffix ?? string.Empty : string.Empty), ObjectKind = objectKind, ObjectPath =
                    objectPath, Meta = read.Metadata() };
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

        public OpennessObservation ListObjectChildren(string objectKind, string objectPath, string collectionProperty, string softwarePath = "", int limit =
                200)
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

            var v = GetPropertyPathValue(o, collectionProperty, nameof(collectionProperty));
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

        private static Dictionary<string, string>? AttributeModes(object target)
        {
            var method = typeof(Siemens.Engineering.IEngineeringObject).IsInstanceOfType(target)
                ? typeof(Siemens.Engineering.IEngineeringObject).GetMethod("GetAttributeInfos", Type.EmptyTypes) : null;
            method ??= target.GetType().GetMethod("GetAttributeInfos", Type.EmptyTypes);
            if (method == null) return null;
            var infos = (IEnumerable)method.Invoke(target, null)!;
            return infos.Cast<object>().ToDictionary(info => (string)info.GetType().GetProperty("Name")!.GetValue(info)!,
                info => info.GetType().GetProperty("AccessMode")!.GetValue(info)!.ToString()!, StringComparer.Ordinal);
        }

        private static OpennessObservation AttributeAdmission(string kind, string path, string attribute, Dictionary<string, string>? modes)
        {
            bool readOnly = modes != null && modes.TryGetValue(attribute, out var mode) && mode != "ReadWrite" && mode != "Write";
            string message = readOnly ? "GetAttributeInfos reports that " + attribute + " is not writable." : "Attribute write preview.";
            return new OpennessObservation { ObjectKind = kind, ObjectPath = path, Message = message, Meta = readOnly
                ? ReflectionSession.Meta(false, ("status", "PreconditionFailed"), ("precondition", "attribute-not-writable"),
                    ("parameter", "args"), ("refusal", message), ("mayHaveChanged", false), ("invocationAttempted", false))
                : ReflectionSession.Meta(true, ("mayHaveChanged", false), ("invocationAttempted", false)) };
        }

        internal OpennessObservation PreviewSetAttribute(string kind, string path, string software, string attribute)
        {
            var target = _session.ResolveObject(kind, path, software) ?? throw new PlcSoftwareException("NotFound", "Reflection target not found.");
            return AttributeAdmission(kind, path, attribute, AttributeModes(target));
        }

        private static object?[]? AttributeFingerprint(Siemens.Engineering.IEngineeringObject target, string attribute,
            Dictionary<string, string>? modes, Func<string, object?> read)
        {
            if (modes != null && (!modes.TryGetValue(attribute, out var mode) || mode is not ("Read" or "ReadWrite"))) return null;
            var names = (modes ?? new Dictionary<string, string>()).Where(p => p.Value is "Read" or "ReadWrite").Select(p => p.Key)
                .Concat(new[] { attribute }).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal);
            var values = new List<object?> { target.GetType().FullName, modes != null };
            foreach (string name in names)
            {
                object? value = read(name);
                values.Add(name);
                if (value is Array array)
                {
                    values.Add(array.GetType());
                    for (int dim = 0; dim < array.Rank; dim++) { values.Add(array.GetLowerBound(dim)); values.Add(array.GetLength(dim)); }
                    foreach (object? item in array)
                    {
                        if (!AttributeScalar(item)) return null;
                        values.Add(item);
                    }
                }
                else
                {
                    if (!AttributeScalar(value)) return null;
                    values.Add(value);
                }
            }
            return values.ToArray();
        }

        private static bool AttributeScalar(object? value) => value == null || value is string or decimal or TimeSpan or Guid or DateTime
            or DateTimeOffset or Version || value.GetType().IsPrimitive || value.GetType().IsEnum;

        private OpennessObservation InvokeOnInstance(object instance, string resultKind, string resultPath, string methodName, HardwareScalar[]? args,
                bool allowWrite, Func<object?>? resolve = null)
        {
            var hardDenyReason = GetHardDeniedReflectionReason(instance, resultKind, resultPath, methodName, args?.Length ?? 0);
            if (!string.IsNullOrWhiteSpace(hardDenyReason))
            {
                return new OpennessObservation
                {
                    Message = hardDenyReason,
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "ACCESS_DENIED"), ("invocationAttempted", false),
                        ("mayHaveChanged", false), ("policyRefusal", hardDenyReason)),
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

            var proof = new Dictionary<string, object?>();
            bool invocationAttempted = false;
            try
            {
                Dictionary<string, string>? modes = null;
                if (resolve != null && instance is Siemens.Engineering.IEngineeringObject
                    && methodName.Equals("SetAttribute", StringComparison.OrdinalIgnoreCase) && argValues.Count == 2 && argValues[0] is string name)
                {
                    modes = AttributeModes(instance);
                    var admission = AttributeAdmission(resultKind, resultPath, name, modes);
                    if (Equals(admission.Meta["success"], false)) return admission;
                }
                using var binding = _session.BindMethod(instance, methodName, argValues);
                if (binding == null)
                {
                    return new OpennessObservation
                    {
                        Message = "Method not found (signature mismatch)",
                    Meta = ReflectionSession.Meta(false, ("v4Rejection", "NOT_FOUND")),
                        ObjectKind = resultKind,
                        ObjectPath = resultPath
                    };
                }

                object? result = null;
                void Invoke() { invocationAttempted = true; result = binding.Method.Invoke(instance, binding.Arguments); }
                if (resolve != null && instance is Siemens.Engineering.IEngineeringObject target && binding.Method.Name == "SetAttribute"
                    && binding.Arguments.Length == 2 && binding.Arguments[0] is string attribute
                    && binding.Method.GetParameters()[0].ParameterType == typeof(string)
                    && binding.Method.GetParameters()[1].ParameterType == typeof(object))
                {
                    object?[]? Fingerprint()
                    {
                        if (!invocationAttempted)
                            return AttributeFingerprint(target, attribute, modes,
                                key => key == attribute ? binding.OriginalAttribute : target.GetAttribute(key));
                        if (resolve() is not Siemens.Engineering.IEngineeringObject fresh) return null;
                        return AttributeFingerprint(fresh, attribute, AttributeModes(fresh), key => fresh.GetAttribute(key));
                    }
                    NativeRefusalProbe.Run(proof, Fingerprint, Invoke);
                }
                else Invoke();

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
            catch (Exception error)
            {
                var reason = NativeFailureText.Compose(error.GetBaseException().Message, null, Array.Empty<string>());
                var meta = ReflectionSession.Meta(false, ("invocationAttempted", invocationAttempted), ("nativeMessage", reason),
                    ("phase", "invoke"), ("v4Rejection", invocationAttempted ? null : "INVALID_ARGUMENT"));
                foreach (var pair in proof) meta[pair.Key] = pair.Value;
                var message = error is TargetInvocationException ? reason : NativeFailureText.Compose(error.Message, null, Array.Empty<string>());
                return new OpennessObservation { Message = message, Meta = meta, ObjectKind = resultKind, ObjectPath = resultPath };
            }
        }

        private static string? GetHardDeniedReflectionReason(object instance, string resultKind, string resultPath, string methodName, int argumentCount)
        {
            var instanceType = instance.GetType().FullName ?? instance.GetType().Name;
            for (var type = instance.GetType(); type != null; type = type.BaseType)
            {
                var refusal = OpennessReflectionPolicy.GuardedWriteRefusal(type.FullName ?? type.Name, methodName, argumentCount);
                if (refusal != null) return refusal;
            }
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
                return "Denied by safety policy: online/watch/monitor surfaces are read-only. The MCP server may read current status only and must not " +
                    "modify watch-table objects or PLC values.";
            }

            return null;
        }

        public OpennessObservation InvokeObject(string objectKind, string objectPath, string methodName, HardwareScalar[]? args = null, string softwarePath =
                "", bool allowWrite = false)
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
            return InvokeOnInstance(o, objectKind, objectPath, methodName, args, allowWrite,
                () => _session.ResolveAfterWrite(objectKind, objectPath, softwarePath, allowWrite));
        }

        public OpennessObservation DescribeService(string objectKind, string objectPath, string serviceTypeSuffix, string softwarePath = "", int maxMembers =
                200)
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
                Meta = ReflectionSession.Meta(true, ("totalMembers", _session.CountMembers(svc)),
                    ("truncated", _session.CountMembers(svc) > MemberListCap.Effective(maxMembers))),
                ObjectKind = "Service",
                ObjectPath = $"{objectKind}:{objectPath}::{serviceTypeSuffix}",
                TypeName = svc.GetType().FullName ?? svc.GetType().Name,
                Members = _session.DescribeMembers(svc, MemberListCap.Effective(maxMembers)).ToList()
            };
        }

        public OpennessObservation InvokeService(string objectKind, string objectPath, string serviceTypeSuffix, string methodName, HardwareScalar[]? args =
                null, string softwarePath = "", bool allowWrite = false)
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
