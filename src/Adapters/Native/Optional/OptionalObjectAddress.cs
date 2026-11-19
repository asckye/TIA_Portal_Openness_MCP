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
            var path = OptionalInputs.Parse(json);
            EngineeringInputValidation.Path(json, path, optional: true);
            return (HmiArray)path!;
        }
        internal static object Resolve(object root, string json, string parameter = "objectPath")
        {
            object target = root;
            foreach (var node in Parse(json))
            {
                var step = (HmiObject)node!;
                var property = target.GetType().GetProperty(step["property"]!.OptionalValue<string>(), BindingFlags.Instance | BindingFlags.Public);
                if (property?.GetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0)
                    throw new NotSupportedException("Public property not available: " + step["property"]);
                target = property.GetValue(target) ?? throw new PlcSoftwareException("NotFound", "Null object at " + property.Name)
                    { Data = { ["parameter"] = parameter } };
                if (step.ContainsKey("name"))
                {
                    var name = step["name"]!.OptionalValue<string>();
                    target = EngineeringGroupOperations.Find(target, name)
                        ?? throw new PlcSoftwareException("NotFound", "Exact named object not found at " + property.Name + "/" + name)
                            { Data = { ["parameter"] = parameter } };
                }
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
