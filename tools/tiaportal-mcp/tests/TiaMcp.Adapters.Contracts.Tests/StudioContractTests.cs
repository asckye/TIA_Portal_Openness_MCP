using System.Reflection;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Studio;
using Xunit;

public sealed class StudioContractTests
{
    public static IEnumerable<object[]> GoldenCases() => StudioGoldenSamples.All().Select(s => new object[] { s.Name });
    public static IEnumerable<object[]> DtoTypes() => StudioGoldenSamples.Types.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void StudioJsonMatchesFrozenLegacyBytes(string name)
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "StudioGoldenWire.json")));
        var expected = golden.RootElement.GetProperty(name).GetString();
        Assert.Equal(expected, StudioGoldenSamples.Serialize(StudioGoldenSamples.All().Single(s => s.Name == name).Value));
        Assert.Equal(expected, StudioGoldenSamples.Serialize(StudioGoldenSamples.All(legacy: true).Single(s => s.Name == name).Value));
    }

    [Theory]
    [MemberData(nameof(DtoTypes))]
    public void StudioDtosPreserveMemberOrderTypesAndEnumValues(Type type)
    {
        var legacy = StudioGoldenSamples.Legacy(type);
        Assert.Same(typeof(IOpennessAdapter).Assembly, type.Assembly);
        if (type.IsEnum)
        {
            Assert.Equal(Enum.GetNames(legacy), Enum.GetNames(type));
            Assert.Equal(Enum.GetValues(legacy).Cast<object>().Select(Convert.ToInt32), Enum.GetValues(type).Cast<object>().Select(Convert.ToInt32));
        }
        else
        {
            string Shape(Type t) => t.IsGenericType
                ? t.GetGenericTypeDefinition().Name + "<" + string.Join(",", t.GetGenericArguments().Select(Shape)) + ">" : t.Name;
            string[] Members(Type t) => t.GetProperties().Select(p => p.Name + ":" + Shape(p.PropertyType) + ":" + p.CanWrite).ToArray();
            Assert.Equal(Members(legacy), Members(type));
            Assert.Equal(typeof(object), type.BaseType);
            Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        }
    }

    [Fact]
    public void EveryStudioValueHasGoldenCoverageAndNoHostDependency()
    {
        var assembly = typeof(IOpennessAdapter).Assembly;
        Assert.Equal(StudioGoldenSamples.Types.OrderBy(t => t.Name), assembly.GetExportedTypes()
            .Where(t => t.Namespace == typeof(SessionState).Namespace && t != typeof(ProgressCallback)).OrderBy(t => t.Name));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("TiaOpenness", StringComparison.Ordinal));
    }

    [Fact]
    public void NewCapabilitiesDoNotReuseExistingBits()
    {
        var values = Enum.GetValues<AdapterCapabilities>().Where(v => v != AdapterCapabilities.None).Select(v => (int)v).ToArray();
        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.All(values, v => Assert.Equal(0, v & (v - 1)));
        Assert.Equal(16384, (int)AdapterCapabilities.PlcDocuments);
        Assert.Equal(typeof(IStudioSession), typeof(IOpennessAdapter).GetProperty("StudioSession")!.PropertyType);
    }
}
