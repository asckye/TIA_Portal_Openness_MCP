using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Units;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using Xunit;

namespace TiaMcp.FoundationHost.SpecialExportShape.Tests;

public sealed class ScopedCompileNativeTests
{
    private sealed class Compiler : ICompilable
    {
        internal int Calls;
        internal Action? Compiling;
        internal CompilerResult Result { get; } = new();
        public CompilerResult Compile() { Calls++; Compiling?.Invoke(); return Result; }
    }
    private static (PlcFoundationEngine Engine, CompileServiceOwner Owner, Compiler Compiler) Target(string scope, string unitName = "")
    {
        var engine = new PlcFoundationEngine();
        var unit = new PlcUnit { Name = "Unit/one" };
        var provider = new PlcUnitProvider(); provider.UnitGroup.Units.Add(unit);
        engine.Software.Services.Add(typeof(PlcUnitProvider), provider);
        var root = unitName == "" ? engine.Software.BlockGroup : unit.BlockGroup;
        var group = new PlcBlockUserGroup { Name = "Group/one" }; root.Groups.Add(group);
        var block = new PlcBlock { Name = "Main%one" }; group.Blocks.Add(block);
        // A root block with the same name must never receive the scoped compile.
        engine.Software.BlockGroup.Blocks.Add(new PlcBlock { Name = block.Name });
        CompileServiceOwner owner = scope switch { "block" => block, "group" => group, "unit" => unit, _ => engine.Software };
        var compiler = new Compiler { Compiling = () => engine.CompileEvents.Add("compile") };
        owner.Services.Add(typeof(ICompilable), compiler);
        return (engine, owner, compiler);
    }
    private static string PathFor(string scope) => scope switch
    { "block" => "Group%2fone/Main%25one", "group" => "Group%2fone", "unit" => "Unit%2fone", _ => "" };

    [Theory]
    [InlineData("block", "")][InlineData("group", "")][InlineData("unit", "")]
    [InlineData("block", "Unit/one")][InlineData("group", "Unit/one")]
    public void Preview_resolves_split_then_decoded_exact_owner_without_compiling(string scope, string unitName)
    {
        var (engine, owner, compiler) = Target(scope, unitName);
        var result = Assert.IsType<PlcScopedCompileResult>(engine.CompileSoftwareScoped("CPU", scope: scope, scopePath: PathFor(scope), unitName: unitName));
        Assert.False(result.Executed); Assert.True(result.Compilable); Assert.False(result.ApplyBlocked);
        Assert.Equal(scope, result.Scope); Assert.Equal(unitName, result.UnitName);
        Assert.Equal(PathFor(scope).Replace("%2f", "%2F"), result.ScopePath);
        string unit = scope == "unit" || unitName != "" ? "/units/Unit%2Fone" : "";
        Assert.Equal("devices/D/CPU" + unit + (scope == "unit" ? "" : "/blocks/" + result.ScopePath), result.ResolvedObject);
        Assert.Equal(new[] { typeof(ICompilable) }, owner.ServiceReads);
        Assert.Equal(0, compiler.Calls); Assert.Empty(engine.CompileEvents); Assert.Null(result.State); Assert.Null(result.ErrorCount);
        Assert.DoesNotContain(typeof(ICompilable), engine.Software.ServiceReads);
    }

    [Theory]
    [InlineData("block")][InlineData("group")][InlineData("unit")]
    public void Missing_service_reports_preview_and_refuses_execution_without_software_fallback(string scope)
    {
        var (engine, owner, compiler) = Target(scope); owner.Services.Clear();
        engine.Software.Services[typeof(ICompilable)] = compiler;
        var preview = Assert.IsType<PlcScopedCompileResult>(engine.CompileSoftwareScoped("CPU", scope: scope, scopePath: PathFor(scope)));
        Assert.False(preview.Compilable); Assert.True(preview.ApplyBlocked);
        Assert
            .Equal(PlcCompileScope.UnavailableCondition, Assert
                .Throws<AdapterPreconditionException>(() => engine.CompileSoftwareScoped("CPU", dryRun: false, scope: scope, scopePath: PathFor(scope)))
                .Condition);
        Assert.Equal(0, compiler.Calls); Assert.Empty(engine.CompileEvents);
        Assert.DoesNotContain(typeof(ICompilable), engine.Software.ServiceReads);
    }

    [Theory]
    [InlineData("block")][InlineData("group")][InlineData("unit")][InlineData("software")]
    public void Execution_retains_two_offline_checks_native_counts_and_nested_diagnostics(string scope)
    {
        var (engine, _, compiler) = Target(scope);
        compiler.Result.State = CompilerResultState.Error; compiler.Result.ErrorCount = 4; compiler.Result.WarningCount = 2;
        var summary = new CompilerResultMessage { State = CompilerResultState.Error, Description = "Compiling finished with errors", Path = "Main" };
        summary.Messages.Add(new CompilerResultMessage { State = CompilerResultState.Error, Description = "Bad symbol" });
        compiler.Result.Messages.Add(summary);
        var result = engine.CompileSoftwareScoped("CPU", dryRun: false, scope: scope, scopePath: PathFor(scope));
        Assert.Equal(new[] { "target-offline", "project-offline", "target-offline", "project-offline", "compile" }, engine.CompileEvents);
        Assert.Equal(1, compiler.Calls); Assert.True(result.Executed); Assert.Equal("Error", result.State);
        Assert.Equal(4, result.ErrorCount); Assert.Equal(2, result.WarningCount); Assert.Equal(new[] { "HMI" }, result.OfflineStateNotExposedByDevices);
        Assert.Equal(2, result.Messages.Length); Assert.Single(result.Errors); Assert.Contains("Path=Main", result.Errors[0]);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void Online_target_or_other_project_device_blocks_scoped_compile(bool targetOnline)
    {
        var (engine, _, compiler) = Target("block"); engine.TargetOnline = targetOnline; engine.ProjectOnline = !targetOnline;
        Assert.Throws<InvalidOperationException>(() => engine.CompileSoftwareScoped("CPU", dryRun: false, scope: "block", scopePath: PathFor("block")));
        Assert.Equal(0, compiler.Calls);
    }

    [Theory]
    [InlineData(false, false)][InlineData(true, false)][InlineData(false, true)]
    public void Safety_login_compile_and_cleanup_keep_original_order(bool alreadyLoggedOn, bool compileFails)
    {
        var (engine, _, compiler) = Target("block");
        var admin = new SafetyAdministration { IsLoggedOnToSafetyOfflineProgram = alreadyLoggedOn, Observing = engine.CompileEvents.Add };
        engine.CompileDevice.Services.Add(typeof(SafetyAdministration), admin);
        if (compileFails) compiler.Compiling = () => { engine.CompileEvents.Add("compile"); throw new IOException("compile"); };
        void Execute() => engine.CompileSoftwareScoped("CPU", "secret", false, "block", PathFor("block"));
        if (compileFails) Assert.Throws<IOException>(Execute); else Execute();
        var expected = new List<string> { "target-offline", "project-offline" };
        if (!alreadyLoggedOn) expected.Add("login");
        expected.AddRange(new[] { "target-offline", "project-offline", "compile" });
        if (!alreadyLoggedOn) expected.Add("logoff");
        Assert.Equal(expected, engine.CompileEvents); Assert.Equal(alreadyLoggedOn, admin.IsLoggedOnToSafetyOfflineProgram);
    }

    [Theory]
    [InlineData("block")][InlineData("group")]
    public void Unit_block_and_group_execution_compile_only_the_selected_unit_owner(string scope)
    {
        var (engine, owner, compiler) = Target(scope, "Unit/one");
        var result = Assert.IsType<PlcScopedCompileResult>(engine.CompileSoftwareScoped("CPU", dryRun: false,
            scope: scope, scopePath: PathFor(scope), unitName: "Unit/one"));
        Assert.Equal(1, compiler.Calls); Assert.True(result.Executed); Assert.Equal("Unit/one", result.UnitName);
        Assert.DoesNotContain(typeof(ICompilable), engine.Software.ServiceReads);
        Assert.Equal(new[] { typeof(ICompilable) }, owner.ServiceReads);
    }

    [Fact]
    public void Login_failure_redacts_native_details_and_does_not_compile_or_logoff()
    {
        var (engine, _, compiler) = Target("group");
        var admin = new SafetyAdministration { FailLogin = true, Observing = engine.CompileEvents.Add };
        engine.CompileDevice.Services.Add(typeof(SafetyAdministration), admin);
        var error = Assert.Throws<InvalidOperationException>(() => engine.CompileSoftwareScoped("CPU", "secret", false, "group", PathFor("group")));
        Assert.DoesNotContain("secret", error.Message); Assert.DoesNotContain("native secret", error.Message);
        Assert.Equal(new[] { "target-offline", "project-offline", "login" }, engine.CompileEvents); Assert.Equal(0, compiler.Calls);
    }

    [Fact]
    public void Compile_failure_preserves_original_exception_when_safety_cleanup_also_fails()
    {
        var (engine, _, compiler) = Target("block"); var original = new IOException("compile failure");
        compiler.Compiling = () => throw original;
        engine.CompileDevice.Services.Add(typeof(SafetyAdministration), new SafetyAdministration {
            FailLogoff = true, Observing = engine.CompileEvents.Add });
        var error = Assert.Throws<IOException>(() => engine.CompileSoftwareScoped("CPU", "secret", false, "block", PathFor("block")));
        Assert.Same(original, error); Assert.True(error.Data.Contains("safetyCleanup")); Assert.Equal("logoff", engine.CompileEvents.Last());
    }

    [Fact]
    public void Safety_logoff_failure_after_compile_retains_unknown_outcome()
    {
        var (engine, _, compiler) = Target("block");
        engine.CompileDevice.Services.Add(typeof(SafetyAdministration), new SafetyAdministration {
            FailLogoff = true, Observing = engine.CompileEvents.Add });
        var error = Assert.Throws<InvalidOperationException>(() => engine.CompileSoftwareScoped("CPU", "secret", false, "block", PathFor("block")));
        Assert.Contains("outcome is unknown", error.Message); Assert.Equal(1, compiler.Calls); Assert.Equal("logoff", engine.CompileEvents.Last());
    }

    [Fact]
    public void A_device_going_online_during_login_is_refused_by_the_second_offline_check()
    {
        var (engine, _, compiler) = Target("block");
        engine.CompileDevice.Services.Add(typeof(SafetyAdministration), new SafetyAdministration { Observing = step => {
            engine.CompileEvents.Add(step); if (step == "login") engine.ProjectOnline = true;
        } });
        Assert.Throws<InvalidOperationException>(() => engine.CompileSoftwareScoped("CPU", "secret", false, "block", PathFor("block")));
        Assert.Equal(0, compiler.Calls); Assert.Equal(new[] { "target-offline", "project-offline", "login", "target-offline", "project-offline", "logoff" },
            engine.CompileEvents);
    }

    [Fact]
    public void Safety_preview_does_not_login_and_execution_refuses_missing_service()
    {
        var (engine, _, compiler) = Target("block");
        engine.CompileSoftwareScoped("CPU", "secret", scope: "block", scopePath: PathFor("block"));
        Assert.Empty(engine.CompileDevice.ServiceReads);
        Assert.Throws<AdapterPreconditionException>(() => engine.CompileSoftwareScoped("CPU", "secret", false, "block", PathFor("block")));
        Assert.Equal(0, compiler.Calls);
    }

    [Theory]
    [InlineData("block", "Group%2Fone/missing")][InlineData("group", "missing")][InlineData("unit", "missing")]
    public void Missing_target_does_not_compile_a_parent_or_software(string scope, string path)
    {
        var (engine, _, compiler) = Target(scope);
        Assert.Throws<AdapterPreconditionException>(() => engine.CompileSoftwareScoped("CPU", dryRun: false, scope: scope, scopePath: path));
        Assert.Equal(0, compiler.Calls); Assert.Empty(engine.CompileEvents);
    }

    [Theory]
    [InlineData("block")][InlineData("group")][InlineData("unit")]
    public void Name_readback_mismatch_refuses_target(string scope)
    {
        var (engine, owner, compiler) = Target(scope);
        if (scope == "block") engine.Software.BlockGroup.Groups[0].Blocks.Finding = _ => new PlcBlock { Name = "wrong" };
        if (scope == "group") engine.Software.BlockGroup.Groups.Finding = _ => new PlcBlockUserGroup { Name = "wrong" };
        if (scope == "unit") ((PlcUnitProvider)engine.Software.Services[typeof(PlcUnitProvider)]).UnitGroup.Units.Finding = _ => new PlcUnit { Name = "wrong" };
        Assert.Throws<AdapterPreconditionException>(() => engine.CompileSoftwareScoped("CPU", scope: scope, scopePath: PathFor(scope)));
        Assert.Equal(0, compiler.Calls); Assert.Empty(owner.ServiceReads);
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")]
    public void Old_releases_refuse_units_before_native_selection(string release)
    {
        var engine = new PlcFoundationEngine { ReleaseKey = release };
        Assert
            .Equal(PlcCompileScope.UnavailableCondition, Assert
                .Throws<AdapterPreconditionException>(() => engine.CompileSoftwareScoped("nonexistent", scope: "unit", scopePath: "Unit")).Condition);
        Assert
            .Equal(PlcCompileScope.UnavailableCondition, Assert
                .Throws<AdapterPreconditionException>(() => engine.CompileSoftwareScoped("nonexistent", scope: "block", scopePath: "Main", unitName: "Unit"))
                .Condition);
        Assert.Empty(engine.Software.ServiceReads);
    }

    [Fact]
    public void Default_scoped_route_preserves_legacy_software_json_and_service_owner()
    {
        var (engine, _, compiler) = Target("software");
        var original = engine.CompileSoftware("CPU"); var routed = engine.CompileSoftwareScoped("CPU");
        Assert.IsType<PlcCompileResult>(original); Assert.IsType<PlcCompileResult>(routed);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(routed)); Assert.Equal(0, compiler.Calls);
        Assert.Equal(new[] { typeof(ICompilable), typeof(ICompilable) }, engine.Software.ServiceReads);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void Explicit_software_defaults_are_accepted_by_the_native_worker_route(bool dryRun)
    {
        var (engine, _, compiler) = Target("software");
        var original = engine.CompileSoftware("CPU", dryRun: dryRun);
        var explicitDefaults = engine.CompileSoftwareScoped("CPU", dryRun: dryRun, scope: "software", scopePath: "", unitName: "");
        Assert.IsType<PlcCompileResult>(explicitDefaults);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(explicitDefaults));
        Assert.Equal(dryRun ? 0 : 2, compiler.Calls);
        Assert.Equal(new[] { typeof(ICompilable), typeof(ICompilable) }, engine.Software.ServiceReads);
    }
}
