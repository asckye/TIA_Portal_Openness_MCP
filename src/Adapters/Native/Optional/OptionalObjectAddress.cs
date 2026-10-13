using System;
using System.Linq;
using System.Reflection;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Hmi;
using EngineeringGroupOperations = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;
using EngineeringScalarProperties = TiaMcp.Adapters.Native.Optional.OptionalScalars;
using EngineeringObjectAddress = TiaMcp.Adapters.Native.Optional.OptionalObjectAddress;
using NativeFileOutput = TiaMcp.Adapters.Native.Safety.SafetyFileOutput;
using PlcBlockServicesLogic = TiaMcp.Adapters.Native.Safety.SafetyPrimitives;
using HardwareServicesLogic = TiaMcp.Adapters.Native.Optional.OptionalRules;
using ArgumentRules = TiaMcp.Adapters.Native.Optional.OptionalRules;
using EngineeringPath = TiaMcp.Adapters.Hardware.HardwarePath;

namespace TiaMcp.Adapters.Native.Optional
{
    // Property-only, bounded addressing. No methods, parent traversal or fuzzy lookup.
    internal static class OptionalObjectAddress
    {
        private static readonly string[] BackLinks = { "Parent", "Owner", "Project", "Portal", "Site", "Container", "Device", "AssignedHmiDevice" };
        internal static HmiArray Parse(string json)
        {
            if (json == null || json.Length > 32768) throw new ArgumentException("Object path exceeds 32 KiB.");
            var path = OptionalInputs.Parse(json) as HmiArray ?? throw new ArgumentException("Path must be a JSON array.");
            if (path.Count > 24) throw new ArgumentException("At most 24 property steps.");
            foreach (var node in path)
            {
                var step = node as HmiObject ?? throw new ArgumentException("Each step must be {property,name?}.");
                if (step.Any(p => p.Key != "property" && p.Key != "name")) throw new ArgumentException("Unknown object-path key.");
                var property = step["property"]?.OptionalValue<string>();
                if (string.IsNullOrWhiteSpace(property) || !property!.All(c => char.IsLetterOrDigit(c) || c == '_') || BackLinks.Contains(property, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("Invalid or backward property step: " + property);
                if (step.ContainsKey("name") && string.IsNullOrWhiteSpace(step["name"]?.OptionalValue<string>())) throw new ArgumentException("Exact nonempty name required.");
            }
            return path;
        }
        internal static object Resolve(object root, string json)
        {
            object target = root;
            foreach (var node in Parse(json))
            {
                var step = (HmiObject)node!;
                var property = target.GetType().GetProperty(step["property"]!.OptionalValue<string>(), BindingFlags.Instance | BindingFlags.Public);
                if (property?.GetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0) throw new NotSupportedException("Public property not available: " + step["property"]);
                target = property.GetValue(target) ?? throw new InvalidOperationException("Null object at " + step);
                if (step.ContainsKey("name")) target = EngineeringGroupOperations.Find(target, step["name"]!.OptionalValue<string>()) ?? throw new InvalidOperationException("Exact named object not found at " + step);
            }
            return target;
        }
        internal static HmiObject Read(object target)
        {
            var result = EngineeringScalarProperties.Read(target);
            var properties = new HmiArray();
            foreach (var p in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0))
                properties.Add(new HmiObject { ["name"] = p.Name, ["type"] = p.PropertyType.FullName,
                    ["readable"] = p.GetMethod?.IsPublic == true, ["writable"] = p.SetMethod?.IsPublic == true,
                    ["backlink"] = BackLinks.Contains(p.Name, StringComparer.OrdinalIgnoreCase) });
            result["propertySchema"] = properties;
            return result;
        }
    }
}
