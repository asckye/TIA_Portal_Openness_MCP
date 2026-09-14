using System;
using System.Linq;
using System.Reflection;

namespace TiaMcpServer.Siemens
{
    internal static class HmiEventAccess
    {
        // Reads must never invoke Create, including when Find returns null.
        internal static object Resolve(object handlers, string eventType, bool createIfMissing = false)
        {
            var find = handlers.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == "Find" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum)
                ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "Event Find(enum) is unavailable.");
            var enumType = find.GetParameters()[0].ParameterType;
            var name = Enum.GetNames(enumType).SingleOrDefault(n => string.Equals(n, eventType, StringComparison.OrdinalIgnoreCase))
                ?? throw new PortalException(PortalErrorCode.InvalidParams, "Invalid event name: " + eventType);
            var value = Enum.Parse(enumType, name);
            var handler = find.Invoke(handlers, new[] { value });
            if (handler != null) return handler;
            if (!createIfMissing) throw new PortalException(PortalErrorCode.NotFound, "Event not found: " + name);
            var create = handlers.GetType().GetMethod("Create", new[] { enumType })
                ?? throw new PortalException(PortalErrorCode.NotSupportedOnVersion, "Event Create(enum) is unavailable.");
            return create.Invoke(handlers, new[] { value })
                ?? throw new PortalException(PortalErrorCode.OpennessError, "Event creation returned null.");
        }
    }
}
