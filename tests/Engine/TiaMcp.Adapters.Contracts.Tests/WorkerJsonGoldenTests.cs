using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcFoundation;
using TiaMcp.PlcWorker;
using Xunit;

public sealed class WorkerJsonGoldenTests
{
    public static IEnumerable<object[]> Dtos() => GoldenSamples.All().Concat(StudioGoldenSamples.All())
        .Select(s => new object[] { s.Name });

    private static IEnumerable<(string Name, object Value)> Samples() => GoldenSamples.All().Concat(StudioGoldenSamples.All());

    private static void EqualOnHost(object? value)
    {
        var oldWire = JsonConvert.SerializeObject(value);
        var newWire = WorkerJson.Serialize(value);
        // This is the production LegacyHost receive path (WorkerClient.Call), not
        // POCO deserialization, which would ignore the DTOs' internal setters.
        var oldRead = JsonNode.Parse(oldWire);
        var newRead = JsonNode.Parse(newWire);
        Assert.True(JsonNode.DeepEquals(oldRead, newRead), oldWire + "\n" + newWire);
        Assert.Equal(oldRead?.ToJsonString(McpJsonUtilities.DefaultOptions), newRead?.ToJsonString(McpJsonUtilities.DefaultOptions));
        Assert.Equal(oldRead?.ToJsonString(), newRead?.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(Dtos))]
    public void EveryDtoDecodesToEqualHostValuesAndResponseBytes(string name) => EqualOnHost(Samples().Single(s => s.Name == name).Value);

    [Fact]
    public void FoundationDtoInventoryIsComplete()
    {
        Assert.Equal(GoldenSamples.Types.OrderBy(t => t.Name), typeof(PlcObjectInfo).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == typeof(PlcObjectInfo).Namespace).OrderBy(t => t.Name));
        EqualOnHost(null);
        Assert.Equal("null", WorkerJson.Serialize(null));
    }

    // These two Foundation operations have not yet moved onto a shared facet.
    private interface FoundationHardware
    {
        PlcHardwareCatalogSearchResult SearchHardwareCatalog(string keyword, int limit=50);
        PlcDeviceAddResult AddDeviceWithFallback(string preferredMlfb,string preferredVersion,string deviceName,string family="S7-1500",bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="");
    }

    private static MethodInfo Method(string name) => new[] { typeof(IPortalSession), typeof(IPlcProgram), typeof(IPlcData), typeof(FoundationHardware) }
        .SelectMany(t => t.GetMethods()).Single(m => m.Name == name);

    public static IEnumerable<object[]> Methods() => WorkerOperations.Names.OrderBy(n => n).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Methods))]
    public void EveryWorkerMethodPreservesResultsAndArguments(string name)
    {
        var method = Method(name);
        var type = method.ReturnType;
        object Sample(Type t) => t == typeof(string) ? "PLC/生产线 <&> 😀" : GoldenSamples.All().Single(s => s.Name == t.Name + ".populated").Value;
        if (type.IsArray)
        {
            var populated = Array.CreateInstance(type.GetElementType()!, 1);
            populated.SetValue(Sample(type.GetElementType()!), 0);
            EqualOnHost(populated);
            EqualOnHost(Array.CreateInstance(type.GetElementType()!, 0));
        }
        else EqualOnHost(Sample(type));
        EqualOnHost(null);

        var parameters = method.GetParameters();
        object Argument(Type t) => t == typeof(string) ? "PLC/生产线 <&> 😀" : t == typeof(bool) ? true : t == typeof(int) ? 42 : new[] { "A", "生产线" };
        var values = parameters.ToDictionary(p => p.Name!, p => Argument(p.ParameterType));
        CompareArguments(parameters, JsonConvert.SerializeObject(values));
        foreach (var optional in parameters.Where(p => p.HasDefaultValue)) values.Remove(optional.Name!);
        CompareArguments(parameters, JsonConvert.SerializeObject(values));
    }

    // Frozen from Program.cs before P2-04b. Newtonsoft is test-only for this boundary.
    private static object?[] OldArguments(ParameterInfo[] parameters, string json)
    {
        var values = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (values.Properties().Any(p => !parameters.Any(a => a.Name == p.Name))) throw new ArgumentException("Unknown worker argument.");
        return parameters.Select(p =>
        {
            if (!values.TryGetValue(p.Name!, StringComparison.Ordinal, out var value))
            {
                if (p.HasDefaultValue) return p.DefaultValue;
                throw new ArgumentException("Missing worker argument: " + p.Name);
            }
            if ((p.ParameterType == typeof(string) && value.Type != JTokenType.String) ||
                (p.ParameterType == typeof(bool) && value.Type != JTokenType.Boolean) ||
                (p.ParameterType == typeof(int) && value.Type != JTokenType.Integer) ||
                (p.ParameterType == typeof(string[]) && (value.Type != JTokenType.Array || value.Count()>256 || value.Any(x => x.Type!=JTokenType.String))))
                throw new ArgumentException("Incorrect worker argument type: " + p.Name);
            return value.ToObject(p.ParameterType);
        }).ToArray();
    }

    private static void CompareArguments(ParameterInfo[] parameters, string json)
    {
        object?[]? oldValue = null, newValue = null;
        var oldError = Record.Exception(() => oldValue = OldArguments(parameters, json));
        var newError = Record.Exception(() => newValue = WorkerJson.Arguments(parameters, WorkerJson.ParseArguments(json)));
        if (oldError == null)
        {
            Assert.Null(newError);
            Assert.Equal(JsonConvert.SerializeObject(oldValue), JsonConvert.SerializeObject(newValue));
        }
        else
        {
            Assert.NotNull(newError);
            // JSON library exceptions keep their -32603 classification; argument
            // errors keep -32602 and the existing worker-owned diagnostic text.
            Assert.Equal(oldError is ArgumentException, newError is ArgumentException);
            if (oldError is ArgumentException) Assert.Equal(oldError.Message, newError.Message);
        }
    }

    private static void ParameterTypes(string text, bool flag, int count, string[] items) { }

    public static IEnumerable<object[]> Arguments()
    {
        foreach (var value in new[] { "null", "true", "false", "0", "-2147483648", "2147483647", "2147483648", "9223372036854775808", "1.0", "1e0", "\"\"", "\"42\"", "{}", "[]", "[\"A\",null]", "[\"A\",\"B\"]", "\"2026-01-02T03:04:05Z\"", "\"2026-01-02\"", "\"/Date(0)/\"" })
            foreach (var parameter in new[] { "text", "flag", "count", "items" }) yield return new object[] { parameter, value };
    }

    [Theory]
    [MemberData(nameof(Arguments))]
    public void ParameterAdmissionAndFailureSemanticsMatch(string name, string value)
    {
        var values = new Dictionary<string, string> { ["text"] = "\"PLC\"", ["flag"] = "true", ["count"] = "1", ["items"] = "[]" };
        values[name] = value;
        CompareArguments(typeof(WorkerJsonGoldenTests).GetMethod(nameof(ParameterTypes), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters(),
            "{" + string.Join(",", values.Select(p => "\"" + p.Key + "\":" + p.Value)) + "}");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":\"A\",\"text\":\"B\"}")]
    [InlineData("{\"Text\":\"A\"}")]
    [InlineData("{\"text\":{\"nested\":1,\"nested\":2}}")]
    public void MissingUnknownAndDuplicateArgumentsKeepFailureClassification(string json) =>
        CompareArguments(typeof(WorkerJsonGoldenTests).GetMethod(nameof(ParameterTypes), BindingFlags.NonPublic | BindingFlags.Static)!.GetParameters(), json);

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    public void StringArrayLimitIsUnchanged(int length) => CompareArguments(Method("ImportBlocksFromDocuments").GetParameters(),
        JsonConvert.SerializeObject(new { softwarePath="PLC",groupPath="",importPath="input",fileNamesWithoutExtension=Enumerable.Repeat("A", length).ToArray() }));

    [Fact]
    public void DateLookingStringsKeepLegacyAdmission()
    {
        var differences = new List<string>();
        foreach (var value in new[] { "2026-01-02T03:04:05", "2026-01-02T03:04:05.1234567Z", "2026-01-02T03:04:05.12345678Z",
            "2026-01-02T03:04:05+05:30", "2026-01-02T03:04:05+0530", "2026-01-02T03:04:05+05", "2026-01-02T03:04:05z", "2026-01-02T03:04:05+99:99", "2026-01-02T03:04:05+05:",
            "2026-01-02T24:00:00Z", "2026-01-02T24:01:00Z", "2026-02-30T03:04:05Z", "2026-01-02 03:04:05",
            "/Date(0)/", "/Date(-1)/", "/Date(0+0530)/", "/Date(0+05)/", "/Date(0+bad)/", "/Date( 0)/", "/Date(bad)/" })
        {
            var json = JsonConvert.SerializeObject(new { text=value,flag=true,count=1,items=new[] { value } });
            bool expected = JObject.Parse(json)["text"]!.Type == JTokenType.String;
            if (expected != WorkerJson.IsString(WorkerJson.ParseArguments(json)["text"])) differences.Add(value + ": string=" + expected);
        }
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void DatesKeepTicksKindAndWireText(DateTimeKind kind)
    {
        var date = new DateTime(2026, 1, 2, 3, 4, 5, kind).AddTicks(1234567);
        var oldWire = JsonConvert.SerializeObject(date);
        Assert.Equal(oldWire, WorkerJson.Serialize(date));
        var read = System.Text.Json.JsonSerializer.Deserialize<DateTime>(WorkerJson.Serialize(date));
        Assert.Equal(date.Ticks, read.Ticks);
        Assert.Equal(date.Kind, read.Kind);
        EqualOnHost(new PlcBlockDetails { ModifiedDate = date });
    }

    public static IEnumerable<object[]> Numbers() => new object[] { int.MinValue, int.MaxValue, long.MinValue, long.MaxValue, ulong.MaxValue,
        0d, -0d, 1d, 1.25d, double.Epsilon, double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
        0f, -0f, 1f, 1.25f, float.Epsilon, float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity,
        0m, 1m, 1.2300m, decimal.MinValue, decimal.MaxValue }.Select(n => new[] { n });

    [Theory]
    [MemberData(nameof(Numbers))]
    public void PolymorphicNumbersPreserveHostResponseBytes(object value) => EqualOnHost(new PlcAttributeValue { Name = "Value", Value = value });

    [Theory]
    [InlineData("inputSha256")]
    [InlineData("outputFile")]
    [InlineData("safetyCleanup")]
    [InlineData("unrelated")]
    public void FailureEvidenceRetainsExactBytesInHostErrorText(string trigger)
    {
        var cause = new InvalidOperationException("native failed");
        cause.Data[trigger] = "生产线 <&> 😀\u00a0\u0085\u2028\u2029\u001b\n\t\"\\";
        // Evidence always names the failure shape (shared host rule); data fields stay byte-exact.
        var expected = JsonConvert.SerializeObject(new {
            exceptionType = cause.GetType().Name, parameter = (string?)null, isArgument = (bool?)null,
            inputFile=cause.Data["inputFile"],inputSha256=cause.Data["inputSha256"],outputFile=cause.Data["outputFile"],stagedFile=cause.Data["stagedFile"],
            recoveryDirectory=cause.Data["recoveryDirectory"],exportPhase=cause.Data["exportPhase"],stagedSha256=cause.Data["stagedSha256"],safetyCleanup=cause.Data["safetyCleanup"] });
        Assert.Equal(expected, WorkerJson.Evidence(cause));
    }

    [Fact]
    public void FoundationEnumWireValuesRemainNumeric()
    {
        foreach (var type in GoldenSamples.Types.Where(t => t.IsEnum).Concat(new[] { typeof(AdapterCapabilities), typeof(AdapterOutcome), typeof(AdapterErrorCode) }))
            foreach (var value in Enum.GetValues(type))
            {
                Assert.Equal(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), WorkerJson.Serialize(value));
                EqualOnHost(value);
            }
    }
}
