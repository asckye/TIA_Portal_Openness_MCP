using System.Reflection;
using System.Text.Json;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.PlcFoundation;
using Xunit;

public sealed class AdapterFacetTests
{
    private static readonly Type[] Facets = { typeof(IPortalSession), typeof(IPlcProgram), typeof(IPlcData) };

    public static IEnumerable<object[]> Operations() =>
        from facet in Facets
        from method in facet.GetMethods()
        where method.Name != "ReadSoftwareInfo" && method.Name != "ReadSoftwareTree"
        from defaults in new[] { false, true }
        from failure in new[] { false, true }
        select new object[] { facet, method.Name, defaults, failure };

    [Theory]
    [MemberData(nameof(Operations))]
    public void ForwardsOnceWithIdenticalArgumentsResultExceptionAndThread(Type facet, string name, bool defaults, bool failure)
    {
        var engine = new PlcFoundationEngine();
        var adapter = new OpennessAdapter(engine);
        var method = facet.GetMethod(name)!;
        var native = typeof(PlcFoundationEngine).GetMethod(name)!;
        Assert.Equal(native.ReturnType, method.ReturnType);
        var parameters = method.GetParameters();
        Assert.Equal(native.GetParameters().Select(p => (p.Name, p.ParameterType, p.DefaultValue)),
            parameters.Select(p => (p.Name, p.ParameterType, p.DefaultValue)));
        object Value(ParameterInfo p) => p.ParameterType == typeof(string) ? p.Name + "/A%2FB"
            : p.ParameterType == typeof(string[]) ? new[] { p.Name + "-second", p.Name + "-first" }
            : p.ParameterType == typeof(int) ? 37
            : p.ParameterType == typeof(bool) ? !(p.HasDefaultValue && (bool)p.DefaultValue!)
            : throw new InvalidOperationException("Unexpected contract parameter: " + p);
        var arguments = parameters.Select(p => defaults && p.HasDefaultValue ? p.DefaultValue : Value(p)).ToArray();
        engine.Result = method.ReturnType == typeof(void) ? null
            : method.ReturnType == typeof(string) ? "unchanged engine text"
            : method.ReturnType.IsArray ? Array.CreateInstance(method.ReturnType.GetElementType()!, 1)
            : Activator.CreateInstance(method.ReturnType);
        engine.Failure = failure ? new InvalidOperationException("unchanged engine failure") : null;
        engine.Failure?.Data.Add("evidence", "preserved");
        object surface = facet == typeof(IPortalSession) ? adapter.PortalSession
            : facet == typeof(IPlcProgram) ? adapter.PlcProgram : adapter.PlcData;
        if(failure)
        {
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(surface, arguments));
            Assert.Same(engine.Failure, error.InnerException);
            Assert.Equal("preserved", error.InnerException!.Data["evidence"]);
        }
        else Assert.Same(engine.Result, method.Invoke(surface, arguments));
        Assert.Equal(name, engine.Operation);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(Environment.CurrentManagedThreadId, engine.ThreadId);
        Assert.Equal(arguments, engine.Arguments);
        foreach(var pair in arguments.Zip(engine.Arguments))
            if(pair.First is string[]) Assert.Same(pair.First, pair.Second);
    }

    [Fact]
    public void SoftwareFacetsUseTheExistingNativeShapeImplementation()
    {
        var engine = new PlcFoundationEngine();
        engine.Software.Name = "PLC with escaped block";
        engine.Software.BlockGroup.Items.Add(new Siemens.Engineering.SW.Blocks.PlcBlock { Name = "A/B", ProgrammingLanguage = "SCL" });
        var facet = new OpennessAdapter(engine).PlcProgram;
        foreach(var path in new[] { "devices/D/CPU", "D", "CPU" })
        {
            Assert.Equal(JsonSerializer.Serialize(engine.ReadSoftwareInfo(path)), JsonSerializer.Serialize(facet.ReadSoftwareInfo(path)));
            Assert.Equal(JsonSerializer.Serialize(engine.ReadSoftwareTree(path)), JsonSerializer.Serialize(facet.ReadSoftwareTree(path)));
        }
        Assert.Equal(Assert.Throws<ArgumentException>(() => engine.ReadSoftwareInfo("missing")).Message,
            Assert.Throws<ArgumentException>(() => facet.ReadSoftwareInfo("missing")).Message);
        engine.Software.BlockGroup.FailEnumeration = true;
        Assert.Equal(Assert.Throws<IOException>(() => engine.ReadSoftwareTree("D")).Message,
            Assert.Throws<IOException>(() => facet.ReadSoftwareTree("D")).Message);
    }

    [Fact]
    public void IdentityAndCapabilitiesMatchTheCompiledRelease()
    {
        var engine = new PlcFoundationEngine();
        var adapter = new OpennessAdapter(engine);
        Assert.Equal(engine.ReleaseKey, adapter.ReleaseKey);
        var key = adapter.ReleaseKey;
        var identity = new AssemblyName(adapter.ApiIdentity);
        Assert.Equal(key == "21" ? "Siemens.Engineering.Base" : "Siemens.Engineering", identity.Name);
        Assert.Equal(new Version(key == "14sp1" ? "14.0.1.0" : key == "15.1" ? "15.1.0.0" : key + ".0.0.0"), identity.Version);
        Assert.Equal(key == "21" ? "29bfe5fdf4ba5d3b" : "d29ec89bac048f84", Convert.ToHexString(identity.GetPublicKeyToken()!).ToLowerInvariant());
        var expected = AdapterCapabilities.PortalSession | AdapterCapabilities.PlcProgram | AdapterCapabilities.PlcData;
        if(key != "14sp1") expected |= AdapterCapabilities.SourceGenerationResults | AdapterCapabilities.RedundantPlc | AdapterCapabilities.WatchTableRead;
        if(key != "14sp1" && key != "15.1") expected |= AdapterCapabilities.WatchTableExport;
        if(new[] { "16", "17", "18", "19", "20" }.Contains(key)) expected |= AdapterCapabilities.TechnologyObjectExport;
        if(new[] { "17", "18", "19", "20", "21" }.Contains(key)) expected |= AdapterCapabilities.Safety;
        if(new[] { "19", "20", "21" }.Contains(key)) expected |= AdapterCapabilities.TechnologyObjectGroups | AdapterCapabilities.HardwareCatalog;
        if(key == "20" || key == "21") expected |= AdapterCapabilities.PlcDocuments;
        Assert.Equal(expected, adapter.Capabilities);
        Assert.Null(adapter.Hardware);
        Assert.Null(adapter.VersionControl);
        Assert.Null(adapter.HmiExport);
        Assert.Same(adapter.PortalSession, adapter.PortalSession);
        Assert.Same(adapter.PlcProgram, adapter.PlcProgram);
        Assert.Same(adapter.PlcData, adapter.PlcData);
        Assert.Equal(0, engine.Calls);
        Assert.Throws<ArgumentNullException>(() => new OpennessAdapter(null!));
    }
}
