using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Bounded exact-path operations shared by the explicitly supported group families.
    internal static class EngineeringGroupOperations
    {
        internal static object Get(object target, string property) => target.GetType().GetProperty(property)?.GetValue(target)
            ?? throw new NotSupportedException(target.GetType().FullName + "." + property + " is unavailable.");
        internal static IEnumerable<object> Items(object collection)
        {
            if (collection is not IEnumerable sequence || collection is string) throw new NotSupportedException("Expected an engineering collection.");
            int count = 0;
            foreach (var item in sequence)
            {
                if (++count > 10000) throw new InvalidOperationException("Collection exceeds 10000 objects; operation refused, not silently truncated.");
                if (item != null) yield return item;
            }
        }
        internal static object? Find(object collection, string name)
        {
            var matches = Items(collection).Where(x => string.Equals(Get(x, "Name").ToString(), name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Ambiguous exact name: " + name);
            return matches.SingleOrDefault();
        }
        internal static string[] Parts(string path, bool rootAllowed = false)
        {
            if (rootAllowed && string.IsNullOrEmpty(path)) return Array.Empty<string>();
            var parts = (path ?? "").Replace('\\', '/').Split('/');
            if (parts.Length > 64 || parts.Any(x => string.IsNullOrWhiteSpace(x) || x == "." || x == ".."))
                throw new ArgumentException("Use a relative exact path with 1-64 nonempty segments, without '.' or '..'.");
            return parts;
        }
        internal static object Call(object target, string method, Type[] types, params object[] args)
        {
            var info = target.GetType().GetMethod(method, types)
                ?? target.GetType().GetInterfaces().Where(i => i.FullName == "Siemens.Engineering.IEngineeringObject").Select(i => i.GetMethod(method, types)).FirstOrDefault(m => m != null)
                ?? throw new NotSupportedException(target.GetType().FullName + "." + method + " signature is unavailable.");
            try { return info.Invoke(target, args)!; }
            catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
        }
        internal static object Group(object root, string path, string firstGroups = "Groups")
        {
            object current = root;
            bool first = true;
            foreach (var part in Parts(path, true))
            {
                current = Find(Get(current, first ? firstGroups : "Groups"), part)
                    ?? throw new PortalException(PortalErrorCode.NotFound, "Group not found: " + path);
                first = false;
            }
            return current;
        }
        internal static JsonObject Manage(object root, string path, string action, string newName, bool dryRun,
            string contents, string firstGroups = "Groups")
        {
            if (action != "create" && action != "rename" && action != "deleteEmpty") throw new ArgumentException("action must be create, rename or deleteEmpty.");
            var parts = Parts(path);
            if (action == "create")
            {
                var collection = Get(root, firstGroups);
                if (collection.GetType().GetMethod("Create", new[] { typeof(string) }) == null)
                    throw new NotSupportedException("Native group Create(string) unavailable.");
                // Wrap only the root, whose group property can differ for Unified HMI.
                return PlcTypeGroupCreation.Execute<object>(root, path, dryRun,
                    g => Items(Get(g, ReferenceEquals(g, root) ? firstGroups : "Groups")),
                    g => Get(g, "Name").ToString()!,
                    (g, name) => Call(Get(g, ReferenceEquals(g, root) ? firstGroups : "Groups"), "Create", new[] { typeof(string) }, name), false);
            }
            var parentPath = string.Join("/", parts.Take(parts.Length - 1));
            var parent = Group(root, parentPath, firstGroups);
            var siblings = Get(parent, parts.Length == 1 ? firstGroups : "Groups");
            var target = Find(siblings, parts.Last()) ?? throw new PortalException(PortalErrorCode.NotFound, "Group not found: " + path);
            var result = new JsonObject { ["groupPath"] = path, ["action"] = action, ["dryRun"] = dryRun, ["changed"] = false };
            if (action == "rename")
            {
                if (Parts(newName).Length != 1) throw new ArgumentException("newName must be one path segment.");
                var conflict = Find(siblings, newName);
                if (conflict != null && !string.Equals(parts.Last(), newName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Destination group exists: " + newName);
                var property = target.GetType().GetProperty("Name");
                bool dynamicName = property?.CanWrite != true;
                if (dynamicName && !target.GetType().GetInterfaces().Any(i => i.FullName == "Siemens.Engineering.IEngineeringObject"))
                    throw new NotSupportedException("Group Name is not writable.");
                result["newName"] = newName;
                if (!dryRun)
                {
                    result["mayHaveChanged"] = true;
                    if (dynamicName) Call(target, "SetAttribute", new[] { typeof(string), typeof(object) }, "Name", newName);
                    else property!.SetValue(target, newName);
                    if (!string.Equals(Get(target, "Name").ToString(), newName, StringComparison.Ordinal)) throw new InvalidOperationException("Rename returned but readback differs.");
                    result["changed"] = true;
                }
            }
            else
            {
                int childCount = Items(Get(target, "Groups")).Count(), itemCount = Items(Get(target, contents)).Count();
                result["childCount"] = childCount; result["itemCount"] = itemCount;
                if (childCount != 0 || itemCount != 0) throw new InvalidOperationException("Only empty user groups can be deleted.");
                if (target.GetType().GetMethod("Delete", Type.EmptyTypes) == null) throw new NotSupportedException("Native Delete unavailable.");
                if (!dryRun)
                {
                    result["mayHaveChanged"] = true;
                    Call(target, "Delete", Type.EmptyTypes);
                    if (Find(siblings, parts.Last()) != null) throw new InvalidOperationException("Delete returned but target is still present; do not blindly retry.");
                    result["changed"] = true;
                }
            }
            result["success"] = true;
            return result;
        }
    }
}
