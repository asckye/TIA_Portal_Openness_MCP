using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Official IEngineeringObject metadata access (GetAttributeInfos/GetAttribute/SetAttribute/GetCompositionInfos/
    // GetComposition/GetCreationInfos/Create/GetInvocationInfos/Invoke), resolved by reflection so it also runs offline.
    internal static class EngineeringDynamicAccess
    {
        private static readonly string[] Interfaces = { "Siemens.Engineering.IEngineeringObject", "Siemens.Engineering.IEngineeringComposition" };
        internal static MethodInfo? Method(object target, string name, params Type[] signature)
            => target.GetType().GetMethod(name, signature)
               ?? target.GetType().GetInterfaces().Where(i => Interfaces.Contains(i.FullName)).Select(i => i.GetMethod(name, signature)).FirstOrDefault(m => m != null);
        private static MethodInfo Require(object target, string name, params Type[] signature)
            => Method(target, name, signature) ?? throw new NotSupportedException("Official " + name + " is unavailable on " + target.GetType().FullName + ".");
        private static object? Invoke(object target, MethodInfo method, params object?[] args)
        {
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
        }
        private static object? Prop(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value);
        private static IEnumerable<object> Descriptors(object target, string method, int bound)
        {
            var result = Invoke(target, Require(target, method)) as IEnumerable ?? throw new NotSupportedException(method + " returned no descriptors.");
            int count = 0;
            foreach (var info in result)
            {
                if (++count > bound) throw new NotSupportedException(method + " exceeds " + bound + " descriptors.");
                if (info != null) yield return info;
            }
        }
        internal static List<(string Name, string Access)> Infos(object target)
            => Descriptors(target, "GetAttributeInfos", 512).Select(i => (Prop(i, "Name")?.ToString() ?? "", Prop(i, "AccessMode")?.ToString() ?? "")).Where(x => x.Item1 != "").ToList();
        internal static JsonObject Read(object target)
        {
            var values = new JsonObject(); var excluded = new JsonArray(); var failures = new JsonArray(); var schema = new JsonArray();
            var get = Method(target, "GetAttribute", typeof(string));
            foreach (var (name, access) in Infos(target))
            {
                schema.Add(new JsonObject { ["name"] = name, ["accessMode"] = access });
                if (get == null || access.IndexOf("Read", StringComparison.OrdinalIgnoreCase) < 0) { excluded.Add(name); continue; }
                try
                {
                    var value = Invoke(target, get, name);
                    if (value != null && !EngineeringScalarProperties.Scalar(value.GetType())) { excluded.Add(name); continue; }
                    values[name] = EngineeringScalarProperties.Json(value);
                }
                catch (Exception ex) { failures.Add(new JsonObject { ["attribute"] = name, ["error"] = ex.GetBaseException().Message }); if (HmiReadSafety.ConnectionUnavailable(ex)) throw; }
            }
            return new JsonObject { ["type"] = target.GetType().FullName, ["scope"] = "IEngineeringObject.GetAttributeInfos/GetAttribute scalar values only",
                ["values"] = values, ["schema"] = schema, ["excludedComplexAttributes"] = excluded, ["failures"] = failures, ["dataComplete"] = failures.Count == 0 };
        }
        internal static List<(string Name, object? Value)> Prepare(object target, JsonObject changes)
        {
            var infos = Infos(target); var get = Require(target, "GetAttribute", typeof(string)); Require(target, "SetAttribute", typeof(string), typeof(object));
            var prepared = new List<(string, object?)>();
            foreach (var change in changes)
            {
                var info = infos.FirstOrDefault(i => string.Equals(i.Name, change.Key, StringComparison.Ordinal));
                if (info.Name == null) throw new NotSupportedException("Attribute not advertised by GetAttributeInfos: " + change.Key);
                if (info.Access.IndexOf("Write", StringComparison.OrdinalIgnoreCase) < 0) throw new NotSupportedException("Attribute is not writable: " + change.Key);
                var existing = Invoke(target, get, change.Key);
                var type = existing != null && EngineeringScalarProperties.Scalar(existing.GetType()) ? existing.GetType() : typeof(object);
                prepared.Add((change.Key, EngineeringScalarProperties.ConvertValue(change.Value, type)));
            }
            return prepared;
        }
        internal static void Apply(object target, List<(string Name, object? Value)> changes, JsonObject meta)
        {
            var set = Require(target, "SetAttribute", typeof(string), typeof(object)); var get = Require(target, "GetAttribute", typeof(string));
            var applied = new JsonArray(); meta["appliedAttributes"] = applied;
            foreach (var (name, value) in changes)
            {
                meta["mayHaveChanged"] = true; meta["lastAttemptedAttribute"] = name;
                Invoke(target, set, name, value);
                applied.Add(name);
                var readback = Invoke(target, get, name);
                if (!Equals(readback, value) && !string.Equals(Convert.ToString(readback, CultureInfo.InvariantCulture), Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    throw new InvalidOperationException("Attribute readback differs: " + name + ". Changes are not rolled back.");
            }
        }
        internal static List<string> CompositionNames(object target)
            => Descriptors(target, "GetCompositionInfos", 128).Select(i => Prop(i, "Name")?.ToString() ?? "").Where(n => n != "").ToList();
        internal static object Composition(object target, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Exact compositionName required.");
            if (!CompositionNames(target).Contains(name, StringComparer.Ordinal)) throw new NotSupportedException("Composition not advertised by GetCompositionInfos: " + name);
            return Invoke(target, Require(target, "GetComposition", typeof(string)), name) ?? throw new NotSupportedException("GetComposition returned null: " + name);
        }
        internal static List<Type> CreationTypes(object composition)
            => Descriptors(composition, "GetCreationInfos", 128).Select(i => Prop(i, "Type") as Type).Where(t => t != null).Select(t => t!).ToList();
        internal static Type CreationType(object composition, string typeName)
        {
            var types = CreationTypes(composition);
            var matches = types.Where(t => t.FullName == typeName || t.Name == typeName).ToList();
            if (matches.Count != 1) throw new NotSupportedException("typeName must be exactly one type advertised by GetCreationInfos: " + string.Join(", ", types.Select(t => t.FullName)));
            return matches[0];
        }
        internal static object Create(object composition, Type type, JsonObject attributes)
        {
            var method = Require(composition, "Create", typeof(Type), typeof(IEnumerable<KeyValuePair<string, object>>));
            var list = attributes.Select(kv => new KeyValuePair<string, object>(kv.Key, EngineeringScalarProperties.ConvertValue(kv.Value, typeof(object))!)).ToList();
            return Invoke(composition, method, type, list) ?? throw new InvalidOperationException("Native Create returned null.");
        }
        internal static string? Name(object item)
        {
            var property = item.GetType().GetProperty("Name");
            if (property?.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0) return property.GetValue(item)?.ToString();
            var get = Method(item, "GetAttribute", typeof(string));
            if (get == null || !Infos(item).Any(i => i.Name == "Name")) return null;
            return Invoke(item, get, "Name")?.ToString();
        }
        internal static object FindByName(object composition, string name)
        {
            var matches = EngineeringGroupOperations.Items(composition).Where(x => string.Equals(Name(x), name, StringComparison.Ordinal)).Take(2).ToList();
            if (matches.Count > 1) throw new InvalidOperationException("Ambiguous exact entry name: " + name);
            return matches.SingleOrDefault() ?? throw new InvalidOperationException("Exact entry not found: " + name);
        }
        internal static bool CanDelete(object item)
        {
            if (item.GetType().GetMethod("Delete", Type.EmptyTypes) != null) return true;
            var infos = Method(item, "GetInvocationInfos");
            return infos != null && Descriptors(item, "GetInvocationInfos", 256).Any(i => Prop(i, "Name")?.ToString() == "Delete" && (Prop(i, "ParameterInfos") as IEnumerable)?.Cast<object>().Any() != true);
        }
        internal static void Delete(object item)
        {
            if (!CanDelete(item)) throw new NotSupportedException("Native Delete is neither a public method nor an advertised invocation on " + item.GetType().FullName + ".");
            var direct = item.GetType().GetMethod("Delete", Type.EmptyTypes);
            if (direct != null) { Invoke(item, direct); return; }
            Invoke(item, Require(item, "Invoke", typeof(string), typeof(IEnumerable<KeyValuePair<Type, object>>)), "Delete", new List<KeyValuePair<Type, object>>());
        }
    }
}
