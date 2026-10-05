using System.Collections;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using TiaMcp.Adapters.Contracts.Studio;

internal static class StudioGoldenSamples
{
    internal static readonly Type[] Types =
    {
        typeof(SessionState), typeof(ProjectInfo), typeof(DeviceInfo), typeof(BlockInfo),
        typeof(TagTableInfo), typeof(TagInfo), typeof(CompileMessage), typeof(CompileResult),
        typeof(ExportedItem), typeof(ExportResult), typeof(BlockKind), typeof(ExportFormat),
        typeof(CompileSeverity), typeof(SessionMode), typeof(SyncDirection), typeof(VcCompareState),
        typeof(WorkspaceInfo), typeof(MappedObjectInfo), typeof(WorkspaceStatusReport),
        typeof(MappingItem), typeof(MappingResult), typeof(SyncItem), typeof(SyncResult)
    };

    // Matches the existing bridge/client's BridgeJson settings, including string enums
    // and explicit nulls. The fixture was captured from the unchanged Studio DTO assembly.
    internal static string Serialize(object value) => JsonConvert.SerializeObject(value, new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Include,
        DateParseHandling = DateParseHandling.DateTimeOffset,
        Converters = { new StringEnumConverter() },
        Formatting = Formatting.None
    });

    internal static Type Legacy(Type type) => typeof(TiaOpenness.Contracts.Models.SessionState).Assembly
        .GetType("TiaOpenness.Contracts.Models." + type.Name, throwOnError: true)!;

    internal static IEnumerable<(string Name, object Value)> All(bool legacy = false)
    {
        foreach (var contract in Types)
        {
            var type = legacy ? Legacy(contract) : contract;
            yield return (type.Name + ".defaults", Create(type, false, 3)!);
            yield return (type.Name + ".populated", Create(type, true, 3)!);
            if (type.IsEnum)
                foreach (var value in Enum.GetValues(type)) yield return (type.Name + "." + value, value);
        }
    }

    private static object? Create(Type type, bool populated, int depth)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable != null) return populated ? Create(nullable, true, depth) : null;
        if (type == typeof(string)) return populated ? "PLC/温度\"\\\n<sample>" : null;
        if (type == typeof(bool)) return populated;
        if (type == typeof(int)) return populated ? 42 : 0;
        if (type == typeof(DateTimeOffset)) return populated ? new DateTimeOffset(2024, 5, 17, 12, 30, 45, TimeSpan.FromHours(8)) : default(DateTimeOffset);
        if (type == typeof(TimeSpan)) return populated ? TimeSpan.FromMilliseconds(1234) : TimeSpan.Zero;
        if (type.IsEnum) return Enum.GetValues(type).GetValue(populated ? Enum.GetValues(type).Length - 1 : 0);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(type)!;
            if (populated && depth > 0) list.Add(Create(type.GetGenericArguments()[0], true, depth - 1));
            return list;
        }
        if (depth < 0) return null;
        var instance = Activator.CreateInstance(type)!;
        if (populated)
            foreach (var property in type.GetProperties().Where(p => p.CanWrite))
                property.SetValue(instance, Create(property.PropertyType, true, depth - 1));
        return instance;
    }
}
