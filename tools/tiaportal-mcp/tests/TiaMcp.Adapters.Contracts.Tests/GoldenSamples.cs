using System.Reflection;
using TiaMcp.PlcFoundation;

internal static class GoldenSamples
{
    // P4-B: frozen before moving the types, against the woven V20 adapter.
    internal static readonly Type[] Types =
    {
        typeof(BindingObservationState),
        typeof(BindingIdentityStrength),
        typeof(BindingObservation),
        typeof(BindingSnapshotObservation),
        typeof(BindingProcessObservation),
        typeof(BindingCachedFields),
        typeof(BindingProjectObservation),
        typeof(PlcBatchDocumentExportItem),
        typeof(PlcBatchDocumentExportResult),
        typeof(PlcBatchDocumentImportItem),
        typeof(PlcBatchDocumentImportResult),
        typeof(PlcBatchExportItem),
        typeof(PlcBatchExportResult),
        typeof(PlcBatchImportObject),
        typeof(PlcBatchImportItem),
        typeof(PlcBatchImportFailure),
        typeof(PlcBatchImportResult),
        typeof(PlcDiagnostic),
        typeof(PlcDeviceAddResult),
        typeof(PlcDisconnectResult),
        typeof(PlcDocumentExportResult),
        typeof(PlcDocumentImportResult),
        typeof(PlcExternalSourceDeleteResult),
        typeof(PlcExternalSourceImportPlan),
        typeof(PlcExternalSourceWorkflowResult),
        typeof(PlcExternalSourceImportResult),
        typeof(PlcExternalSourceObject),
        typeof(PlcExternalSourceGenerationResult),
        typeof(PlcObjectInfo),
        typeof(PlcMutationResult),
        typeof(PlcCompileResult),
        typeof(PlcHardwareCatalogCandidate),
        typeof(PlcHardwareCatalogSearchResult),
        typeof(PlcConnectionResult),
        typeof(PlcProjectDetails),
        typeof(PlcAttributeValue),
        typeof(PlcTypeDetails),
        typeof(PlcBlockDetails),
        typeof(PlcBlockHierarchy),
        typeof(PlcRuntimeState),
        typeof(PlcProcessSnapshot),
        typeof(PlcProcessQuery),
        typeof(PlcConnectReadiness),
        typeof(PlcSoftwareDetails),
        typeof(PlcSoftwareTreeDetails),
        typeof(PlcSpecialExportResult),
        typeof(PlcTechnologyReadRow),
        typeof(PlcSupplementaryReadResult),
    };

    internal static IEnumerable<(string Name, object Value)> All()
    {
        foreach(var type in Types)
        {
            yield return (type.Name + ".defaults", Create(type, false, 3)!);
            yield return (type.Name + ".populated", Create(type, true, 3)!);
        }
        // Nested results, polymorphic object values and both batch outcome branches.
        var batch = new PlcBatchImportResult();
        Set(batch, "Items", new[] { Item("imported"), Item("failed") });
        yield return ("nested.worker-envelope", new { id = 17, result = batch });
        yield return ("nested.binding-envelope", new { id = 18, result = Create(typeof(BindingSnapshotObservation), true, 3) });
        foreach(var state in Enum.GetValues<BindingObservationState>()) yield return ("enum.state." + state, state);
        foreach(var strength in Enum.GetValues<BindingIdentityStrength>()) yield return ("enum.strength." + strength, strength);
    }

    private static PlcBatchImportItem Item(string status)
    {
        var item = (PlcBatchImportItem)Create(typeof(PlcBatchImportItem), true, 3)!;
        Set(item, "Status", status);
        return item;
    }

    private static void Set(object instance, string property, object? value) =>
        instance.GetType().GetProperty(property)!.SetValue(instance, value);

    private static object? Create(Type type, bool populated, int depth)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if(nullable != null) return populated ? Create(nullable, true, depth) : null;
        if(type == typeof(string)) return populated ? "PLC/温度\"\\\n<sample>" : "";
        if(type == typeof(bool)) return populated;
        if(type == typeof(int)) return populated ? 42 : 0;
        if(type == typeof(long)) return 638500000000000000L;
        if(type == typeof(DateTime)) return populated ? new DateTime(2024, 5, 17, 12, 30, 45, DateTimeKind.Utc) : default(DateTime);
        if(type.IsEnum) return Enum.GetValues(type).GetValue(populated ? Enum.GetValues(type).Length - 1 : 0);
        if(type == typeof(object)) return populated ? new Dictionary<string, object?> { ["UserKey"] = "值", ["Null"] = null, ["Enum"] = BindingObservationState.Bound } : null;
        if(type == typeof(Dictionary<string, string>)) return populated ? new Dictionary<string, string> { ["Source"] = "exact-sdk", ["Empty"] = "" } : new Dictionary<string, string>();
        if(type == typeof(Dictionary<string, object>)) return populated ? new Dictionary<string, object> { ["Flag"] = true, ["Null"] = null! } : new Dictionary<string, object>();
        if(type.IsArray)
        {
            var array = Array.CreateInstance(type.GetElementType()!, populated && depth > 0 ? 1 : 0);
            if(array.Length != 0) array.SetValue(Create(type.GetElementType()!, true, depth - 1), 0);
            return array;
        }
        if(type == typeof(BindingProcessObservation)) return new BindingProcessObservation(42, 638500000000000000L);
        var ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderBy(c => c.GetParameters().Length).First();
        var instance = ctor.Invoke(ctor.GetParameters().Select(p => Create(p.ParameterType, populated, depth - 1)).ToArray());
        if(populated)
            foreach(var property in type.GetProperties().Where(p => p.GetSetMethod(true) != null))
                property.SetValue(instance, Create(property.PropertyType, true, depth - 1));
        return instance;
    }
}
