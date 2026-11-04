using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;
#if PLC_WATCH_READ
using Siemens.Engineering.SW.WatchAndForceTables;
#endif
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using Logic = TiaMcp.Adapters.Native.Plc.PlcMotionPolicy;
using WatchNative = TiaMcp.Adapters.WatchTechnologyPrimitives;

namespace TiaMcp.Adapters.Native.Plc
{
    public sealed partial class PlcOrganisationAdapter
    {
#if PLC_WATCH_READ
        private static string? TryGetName(object? o)
        {
            if (o == null) return null;
            try
            {
                var t = o.GetType();
                var p = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                var v = p?.GetValue(o);
                return v?.ToString();
            }
            catch /* swallow(probe-optional): objects without a readable Name yield no name hint */
            {
                return null;
            }
        }

        private static IEnumerable<PlcApiMember> DescribeMembers(object o, int maxMembers)
        {
            var t = o.GetType();
            var list = new List<PlcApiMember>();

            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                list.Add(new PlcApiMember
                {
                    Kind = "Property",
                    Name = p.Name,
                    Type = p.PropertyType.FullName ?? p.PropertyType.Name
                });
                if (list.Count >= maxMembers) return list;
            }

            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsSpecialName) continue;
                var ps = m.GetParameters();
                var sig = $"{m.Name}({string.Join(", ", ps.Select(x => $"{x.ParameterType.Name} {x.Name}"))}) -> {m.ReturnType.Name}";
                list.Add(new PlcApiMember
                {
                    Kind = "Method",
                    Name = m.Name,
                    Type = m.ReturnType.FullName ?? m.ReturnType.Name,
                    Signature = sig
                });
                if (list.Count >= maxMembers) return list;
            }

            return list;
        }

        private static Type? FindTypeBySuffix(string typeSuffix)
        {
            if (string.IsNullOrWhiteSpace(typeSuffix)) return null;
            var suf = typeSuffix.Trim();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch /* swallow(probe-optional): assemblies with unloadable types cannot supply the optional service type */ { continue; }

                foreach (var t in types)
                {
                    var n = t.FullName ?? t.Name;
                    if (n.EndsWith(suf, StringComparison.OrdinalIgnoreCase) || t.Name.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }

            return null;
        }

        private static object? TryGetService(object target, Type serviceType)
        {
            try
            {
                var t = target.GetType();
                var mi = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (mi == null) return null;

                var g = mi.MakeGenericMethod(serviceType);
                return g.Invoke(target, null);
            }
            catch /* swallow(probe-optional): unsupported reflective GetService lookup returns no service */
            {
                return null;
            }
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
            catch /* swallow(enumerate-optional): unavailable library collections leave the collected name hints intact */
            {
                // best-effort only
            }

            return result;
        }

        private static object? WatchFindByNameInCollection(object root, string[] propertyHints, string wantedName)
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
            catch /* swallow(enumerate-optional): an unavailable candidate collection yields no matching library object */
            {
                // ignore
            }

            return null;
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

#endif
    }
}
