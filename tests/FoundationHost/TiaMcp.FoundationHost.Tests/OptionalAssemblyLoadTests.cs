using System.Reflection;
using System.Xml.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Optional;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.PlcWorker;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class OptionalAssemblyLoadTests
{
    private static string Identity(string assembly) => assembly
        + ", Version=21.0.0.0, Culture=neutral, PublicKeyToken=29bfe5fdf4ba5d3b";

    [Theory]
    [InlineData("Siemens.Engineering.Sivarc")]
    [InlineData("Siemens.Engineering.Startdrive")]
    [InlineData("Siemens.Engineering.DCC")]
    [InlineData("Siemens.Engineering.Safety")]
    [InlineData("Siemens.Engineering.SafetyValidation")]
    public void Admission_requests_the_exact_v21_identity(string assembly)
    {
        AssemblyName? requested = null;
        int calls = 0;
        var expected = new HardwareAddressingReply();
        var reply = OptionalAssemblyAdmission.Run("21", assembly, () => { calls++; return expected; }, name => {
            requested = name;
            return typeof(object).Assembly;
        });

        Assert.NotNull(requested);
        Assert.Equal(Identity(assembly), requested.FullName);
        Assert.Equal(1, calls);
        Assert.Same(expected, reply);
    }

    [Theory]
    [InlineData("Siemens.Engineering.Sivarc")]
    [InlineData("Siemens.Engineering.Startdrive")]
    [InlineData("Siemens.Engineering.DCC")]
    [InlineData("Siemens.Engineering.Safety")]
    [InlineData("Siemens.Engineering.SafetyValidation")]
    public void Admission_identity_passes_the_worker_resolver(string assembly)
    {
        using var api = new ApiFolder(assembly);
        AssemblyName? requested = null;
        OptionalAssemblyAdmission.Run("21", assembly, () => new HardwareAddressingReply(), name => {
            requested = name;
            return typeof(object).Assembly;
        });

        Assert.NotNull(requested);
        string? selected = WorkerAssemblyResolver.SelectFile(api.Path, requested.FullName, file => {
            Assert.Equal(api.File, file);
            return new AssemblyName(Identity(assembly));
        });
        Assert.Equal(api.File, selected);
    }

    [Fact]
    public void Simple_name_is_refused_by_the_identity_check()
    {
        const string assembly = "Siemens.Engineering.Startdrive";
        using var api = new ApiFolder(assembly);
        var error = Assert.Throws<FileLoadException>(() => WorkerAssemblyResolver.SelectFile(api.Path, assembly,
            _ => new AssemblyName(Identity(assembly))));
        Assert.Contains("Selected SDK identity mismatch", error.Message, StringComparison.Ordinal);
        Assert.Equal(api.File, error.FileName);
    }

    [Fact]
    public void Other_version_is_refused()
    {
        const string assembly = "Siemens.Engineering.Startdrive";
        using var api = new ApiFolder(assembly);
        var error = Assert.Throws<FileLoadException>(() => WorkerAssemblyResolver.SelectFile(api.Path, Identity(assembly),
            _ => new AssemblyName(assembly + ", Version=20.0.0.0, Culture=neutral, PublicKeyToken=29bfe5fdf4ba5d3b")));
        Assert.Contains("Selected SDK identity mismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_file_returns_null()
    {
        using var api = new ApiFolder("Siemens.Engineering.Startdrive");
        Assert.Null(WorkerAssemblyResolver.SelectFile(api.Path, Identity("Siemens.Engineering.Contract"),
            _ => throw new InvalidOperationException("Missing files must defer to TIA resolution.")));
    }

    [Fact]
    public void Other_assembly_name_returns_null()
    {
        using var api = new ApiFolder("Other.Assembly");
        Assert.Null(WorkerAssemblyResolver.SelectFile(api.Path, "Other.Assembly",
            _ => throw new InvalidOperationException("Other assembly names must defer to their own resolution.")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_failure_is_not_reported_as_not_installed(bool badImage)
    {
        const string assembly = "Siemens.Engineering.Startdrive";
        const string nativeMessage = "Selected SDK identity mismatch: a / b";
        Exception error = badImage ? new BadImageFormatException(nativeMessage) : new FileLoadException(nativeMessage);
        int calls = 0;
        var reply = OptionalAssemblyAdmission.Run("21", assembly, () => { calls++; return new HardwareAddressingReply(); },
            _ => throw error);

        Assert.Equal(0, calls);
        Assert.Equal(true, reply.Meta["operationNotStarted"]);
        Assert.Equal(false, reply.Meta["mayHaveChanged"]);
        Assert.Equal("UNSUPPORTED_CAPABILITY", reply.Meta["rejectionCode"]);
        Assert.Equal("UNSUPPORTED_CAPABILITY", reply.Meta["v4Rejection"]);
        Assert.Equal("NotSupportedOnVersion", reply.Meta["status"]);
        Assert.DoesNotContain("not installed", reply.Message, StringComparison.Ordinal);
        Assert.Equal("Official optional component " + assembly
            + " is in the selected PublicAPI folder but could not be loaded: " + nativeMessage, reply.Message);
        Assert.Equal(nativeMessage, reply.Meta["nativeMessage"]);
    }

    [Fact]
    public void Absent_assembly_says_not_installed()
    {
        const string assembly = "Siemens.Engineering.Startdrive";
        int calls = 0;
        var reply = OptionalAssemblyAdmission.Run("21", assembly, () => { calls++; return new HardwareAddressingReply(); },
            _ => throw new FileNotFoundException("Synthetic absent assembly."));

        Assert.Equal(0, calls);
        Assert.Equal(true, reply.Meta["operationNotStarted"]);
        Assert.Equal("UNSUPPORTED_CAPABILITY", reply.Meta["rejectionCode"]);
        Assert.Equal("Official optional component is not installed: " + assembly
            + " is not in the selected PublicAPI folder.", reply.Message);
        Assert.False(reply.Meta.ContainsKey("nativeMessage"));
    }

    [Theory]
    [InlineData("Siemens.Engineering.Sivarc")]
    [InlineData("Siemens.Engineering.Startdrive")]
    [InlineData("Siemens.Engineering.DCC")]
    [InlineData("Siemens.Engineering.Safety")]
    [InlineData("Siemens.Engineering.SafetyValidation")]
    public void Sibling_matches_the_adapter_release_identities(string assembly)
    {
        string root = TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var props = XDocument.Load(System.IO.Path.Combine(root, "src/Adapters/V21/Release.props"));
        string expected = props.Descendants("AdapterApi").Single(api => (string?)api.Element("ApiName") == assembly)
            .Element("ExpectedIdentity")!.Value;
        Assert.Equal(expected, OpennessAssemblyNames.Sibling(OpennessAssemblyNames.V21Core, assembly).FullName);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    public void Earlier_releases_enter_the_operation_without_an_assembly_probe(string release)
    {
        int calls = 0;
        OptionalAssemblyAdmission.Run(release, "Siemens.Engineering.Startdrive",
            () => { calls++; return new HardwareAddressingReply(); },
            _ => throw new InvalidOperationException("Only V21 probes split optional assemblies."));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("V14Sp1", "14.0.1.0", "d29ec89bac048f84")]
    [InlineData("V15_1", "15.1.0.0", "d29ec89bac048f84")]
    [InlineData("V16", "16.0.0.0", "d29ec89bac048f84")]
    [InlineData("V17", "17.0.0.0", "d29ec89bac048f84")]
    [InlineData("V18", "18.0.0.0", "d29ec89bac048f84")]
    [InlineData("V19", "19.0.0.0", "d29ec89bac048f84")]
    [InlineData("V20", "20.0.0.0", "d29ec89bac048f84")]
    [InlineData("V21", "21.0.0.0", "29bfe5fdf4ba5d3b")]
    public void Sibling_preserves_each_releases_core_identity(string directory, string version, string token)
    {
        string root = TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var props = XDocument.Load(System.IO.Path.Combine(root, "src/Adapters", directory, "Release.props"));
        string coreName = directory == "V21" ? "Siemens.Engineering.Base" : "Siemens.Engineering";
        var core = new AssemblyName(props.Descendants("AdapterApi").Single(api => (string?)api.Element("ApiName") == coreName)
            .Element("ExpectedIdentity")!.Value);
        string original = core.FullName;
        var sibling = OpennessAssemblyNames.Sibling(core, "Siemens.Engineering.Hmi");
        Assert.Equal("Siemens.Engineering.Hmi, Version=" + version + ", Culture=neutral, PublicKeyToken=" + token, sibling.FullName);
        Assert.Equal(original, core.FullName);
    }

    private sealed class ApiFolder : IDisposable
    {
        internal string Path { get; }
        internal string File { get; }

        internal ApiFolder(string assembly)
        {
            string root = TestPaths.RepositoryRoot(AppContext.BaseDirectory);
            Path = System.IO.Path.Combine(root, "bin-build/vmfix-9a/assembly-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            File = System.IO.Path.Combine(Path, assembly + ".dll");
            System.IO.File.WriteAllBytes(File, Array.Empty<byte>());
        }

        public void Dispose()
        {
            System.IO.File.Delete(File);
            Directory.Delete(Path);
        }
    }
}
