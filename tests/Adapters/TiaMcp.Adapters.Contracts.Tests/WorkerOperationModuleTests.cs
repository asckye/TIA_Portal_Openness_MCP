using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.PlcWorker;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class WorkerOperationModuleTests
{
    private sealed class Facade
    {
        public string Read() => "read";
        public void Write() { }
        public void Unregistered() { }
        public static void Static() { }
    }

    private sealed class CompileFacade
    {
        public string CompileSoftware(string softwarePath, string password = "", bool dryRun = true) => "legacy";
        public string CompileSoftwareScoped(string softwarePath, string password = "", bool dryRun = true,
            string scope = "software", string scopePath = "", string unitName = "") => scope + ":" + scopePath + ":" + unitName;
    }

    [Fact]
    public void Compile_wire_name_uses_optional_scoped_signature_and_retains_legacy_defaults()
    {
        var methods = new WorkerOperationModule("", typeof(CompileFacade), new[] { "CompileSoftware" }).Methods;
        var method = Assert.Single(methods).Value;
        Assert.Equal("CompileSoftwareScoped", method.Name);
        Assert.Equal(new[] { "softwarePath", "password", "dryRun", "scope", "scopePath", "unitName" }, method.GetParameters().Select(p => p.Name));
        Assert.Equal(new object[] { "CPU", "", true, "software", "", "" },
            new object[] { "CPU" }.Concat(method.GetParameters().Skip(1).Select(p => p.DefaultValue)));
        Assert.Equal("block:Group/Main:Unit", method.Invoke(new CompileFacade(), new object[] { "CPU", "", false, "block", "Group/Main", "Unit" }));
        Assert.False(methods.ContainsKey("CompileSoftwareScoped"));
    }

    [Fact]
    public void LegacyAndFamilyModulesKeepExactWireNames()
    {
        var methods = WorkerOperationModule.Register(new[] {
            new WorkerOperationModule("", typeof(Facade), new[] { "Read" }),
            new WorkerOperationModule("hardware-addresses", typeof(Facade), new[] { "Write" })
        });
        Assert.Equal(new[] { "Read", "hardware-addresses.Write" }, methods.Keys);
        Assert.Equal("read", methods["Read"].Invoke(new Facade(), Array.Empty<object>()));
        Assert.False(methods.ContainsKey("Unregistered"));
        Assert.False(methods.ContainsKey("read"));
        Assert.False(methods.ContainsKey("Write"));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("read")]
    [InlineData("Static")]
    [InlineData("ToString")]
    public void MissingOrUnownedMethodsFailRegistration(string name)
        => Assert.Throws<InvalidOperationException>(() => new WorkerOperationModule("hardware", typeof(Facade), new[] { name }));

    [Fact]
    public void DuplicateWireNamesFailBeforeDispatch()
        => Assert.Throws<ArgumentException>(() => WorkerOperationModule.Register(new[] {
            new WorkerOperationModule("hardware", typeof(Facade), new[] { "Read" }),
            new WorkerOperationModule("hardware", typeof(Facade), new[] { "Read" })
        }));

    [Fact]
    public void ExistingRepliesExposeTheirOwnUnknownOutcome()
    {
        var device = new TiaMcp.Adapters.PlcDeviceAddResult();
        var documentBatch = new TiaMcp.Adapters.PlcBatchDocumentImportResult();
        Assert.False(((IWorkerOperationReply)device).MayHaveChanged);
        Assert.False(((IWorkerOperationReply)device).OutcomeUnknown);
        Assert.False(((IWorkerOperationReply)documentBatch).OutcomeUnknown);
    }
}
