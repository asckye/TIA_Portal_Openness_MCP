using System;

namespace TiaMcp.Adapters.Native.Hardware;

internal static class HardwarePublicType
{
    internal static string Name(object value)
    {
        for (Type? type = value.GetType(); type != null; type = type.BaseType)
            if (IsPublicEngineeringType(type)) return type.FullName!;
        foreach (var type in value.GetType().GetInterfaces())
            if (IsPublicEngineeringType(type)) return type.FullName!;
        return "<unknown public engineering type>";
    }

    private static bool IsPublicEngineeringType(Type type)
        => type.IsVisible && (type.Namespace == "Siemens.Engineering"
            || type.Namespace?.StartsWith("Siemens.Engineering.", StringComparison.Ordinal) == true)
            && type.Namespace.IndexOf(".Internal", StringComparison.Ordinal) < 0;
}
