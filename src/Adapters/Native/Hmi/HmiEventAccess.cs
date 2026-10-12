#if TIA_ADAPTER_INTERNAL_VERSIONING
using UnifiedObject = TiaMcp.Adapters.Native.Hmi.HmiObject;
using UnifiedArray = TiaMcp.Adapters.Native.Hmi.HmiArray;
using UnifiedNode = System.Object;
using UnifiedScalar = TiaMcp.Adapters.Native.Hmi.HmiScalar;
using UnifiedGroups = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;
using UnifiedValues = TiaMcp.Adapters.Native.Hmi.HmiValues;
using UnifiedAccess = TiaMcp.Adapters.Native.Hmi.UnifiedHmiAccess;
#endif
using System;
using System.Linq;
using System.Reflection;

#if !TIA_ADAPTER_INTERNAL_VERSIONING
namespace TiaMcpServer.Siemens
#else
namespace TiaMcp.Adapters.Native.Hmi
#endif
{
    internal static class HmiEventAccess
    {
        // Reads must never invoke Create, including when Find returns null.
        internal static object Resolve(object handlers, string eventType, bool createIfMissing = false)
        {
            var find = handlers.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == "Find" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum)
                ?? throw UnifiedAccess.Error("NotSupportedOnVersion", "Event Find(enum) is unavailable.");
            var enumType = find.GetParameters()[0].ParameterType;
            var name = Enum.GetNames(enumType).SingleOrDefault(n => string.Equals(n, eventType, StringComparison.OrdinalIgnoreCase))
                ?? throw UnifiedAccess.Error("InvalidParams", "Invalid event name: " + eventType + " (valid for " + enumType.Name + ": " + string.Join("/", Enum.GetNames(enumType)) + "; a WinCC Unified button click is 'Tapped').");
            var value = Enum.Parse(enumType, name);
            var handler = find.Invoke(handlers, new[] { value });
            if (handler != null) return handler;
            if (!createIfMissing) throw UnifiedAccess.Error("NotFound", "Event not found: " + name);
            var create = handlers.GetType().GetMethod("Create", new[] { enumType })
                ?? throw UnifiedAccess.Error("NotSupportedOnVersion", "Event Create(enum) is unavailable.");
            return create.Invoke(handlers, new[] { value })
                ?? throw UnifiedAccess.Error("OpennessError", "Event creation returned null.");
        }
    }
}
