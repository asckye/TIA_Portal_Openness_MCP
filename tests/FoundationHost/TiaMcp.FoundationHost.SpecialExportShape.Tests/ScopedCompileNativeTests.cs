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

    private static CompilerResultMessage Node(string path, params CompilerResultMessage[] children)
    {
        var node = new CompilerResultMessage { Path = path };
        node.Messages.AddRange(children);
        return node;
    }

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
        Assert.Equal(2, result.Messages.Length);
        Assert.Contains("Path=Main", result.Messages[1]);
        if (scope is "block" or "group")
        {
            var scoped = Assert.IsType<PlcScopedCompileResult>(result);
            Assert.True(scoped.ScopeMismatch);
            Assert.Equal(new[] { "Main" }, scoped.CompiledRoots);
            Assert.Empty(result.Errors);
        }
        else
        {
            Assert.Single(result.Errors); Assert.Contains("Path=Main", result.Errors[0]);
        }
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")]
    [InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public void Roots_identify_the_PLC_and_only_the_named_block_subtree_supplies_its_errors(string release)
    {
        var (engine, owner, compiler) = Target("block");
        engine.ReleaseKey = release;
        engine.Software.Name = "MCP_B6F_PLC";
        var requested = Assert.IsType<PlcBlock>(owner);
        requested.Name = "MCP_B6F_FC";
        var folder = engine.Software.BlockGroup.Groups[0];
        folder.Blocks.Remove(requested);
        engine.Software.BlockGroup.Blocks.Add(requested);
        string scopePath = requested.Name;
        PlcScopedCompileResult Compile() => Assert.IsType<PlcScopedCompileResult>(
            engine.CompileSoftwareScoped("CPU", dryRun: false, scope: "block", scopePath: scopePath));
        var empty = Compile();
        Assert.False(empty.ScopeMismatch); Assert.Empty(empty.CompiledRoots); Assert.Empty(empty.Errors);
        // V21 raw-B/021 and raw-C/161: the PLC owns category rows, which own qualified block labels.
        var program = Node("Program blocks", Node("Main (OB1)"));
        var station = Node("MCP_B6F_PLC", Node("PLC data types", Node("MCP_B6F_UDT (UDT)")), program);
        compiler.Result.Messages.Add(station);
        var clean = Compile();
        Assert.False(clean.ScopeMismatch); Assert.Equal(new[] { "MCP_B6F_PLC", "PLC data types", "Program blocks" }, clean.CompiledRoots);
        Assert.Empty(clean.Errors);
        var block = Node("MCP_B6F_FC (FC9700)");
        program.Messages.Add(block);
        var direct = Compile();
        Assert.False(direct.ScopeMismatch); Assert.Equal(clean.CompiledRoots, direct.CompiledRoots); Assert.Empty(direct.Errors);
        Assert.Contains(direct.Messages, m => m.Contains("Path=MCP_B6F_FC (FC9700)"));

        var safety = new CompilerResultMessage { Path = "Safety", State = CompilerResultState.Error, Description = "Safety diagnostic" };
        station.Messages.Add(safety);
        compiler.Result.ErrorCount = 1; compiler.Result.State = CompilerResultState.Error;
        var safetyOnly = Compile();
        Assert.False(safetyOnly.ScopeMismatch); Assert.Empty(safetyOnly.Errors); Assert.Equal(1, safetyOnly.ErrorCount);
        Assert.Contains(safetyOnly.Messages, message => message.Contains("Safety diagnostic"));

        block.State = CompilerResultState.Error; block.Description = "Unknown operand";
        compiler.Result.ErrorCount = 2;
        var directError = Compile();
        Assert.False(directError.ScopeMismatch); Assert.Contains("Unknown operand", Assert.Single(directError.Errors));
        program.Path = "程序块";
        var localized = Compile();
        Assert.False(localized.ScopeMismatch); Assert.Equal(new[] { "MCP_B6F_PLC", "PLC data types", "程序块", "Safety" }, localized.CompiledRoots);
        Assert.Equal(directError.Errors, localized.Errors);

        engine.Software.BlockGroup.Blocks.Remove(requested);
        folder.Blocks.Add(requested);
        scopePath = "Group%2fone/MCP_B6F_FC";
        program.Path = "Program blocks";
        program.Messages.Remove(block);
        block.Messages.Add(new CompilerResultMessage { State = CompilerResultState.Error, Description = block.Description });
        block.Description = "";
        var group = Node(folder.Name, block);
        program.Messages.Add(group);
        group.Messages.Add(new CompilerResultMessage { Path = "MCP_B6F_FCExtra (FC9701)", State = CompilerResultState.Error, Description = "Sibling" });
        compiler.Result.ErrorCount = 3;
        var nested = Compile();
        Assert.False(nested.ScopeMismatch); Assert.Equal(new[] { "MCP_B6F_PLC", "PLC data types", "Program blocks", "Safety" }, nested.CompiledRoots);
        Assert.Equal(3, nested.ErrorCount); Assert.Contains("Unknown operand", Assert.Single(nested.Errors));
        Assert.Contains(nested.Messages, m => m.Contains("Safety diagnostic")); Assert.Contains(nested.Messages, m => m.Contains("Sibling"));
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")]
    [InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public void Foreign_station_and_device_item_report_a_mismatch_until_one_identifies_the_PLC(string release)
    {
        var (engine, _, compiler) = Target("block");
        engine.ReleaseKey = release;
        PlcScopedCompileResult Compile() => Assert.IsType<PlcScopedCompileResult>(
            engine.CompileSoftwareScoped("CPU", dryRun: false, scope: "block", scopePath: PathFor("block")));
        // V21 raw-C/163: the foreign station has a device item above both Safety and Program blocks.
        var libraryBlock = new CompilerResultMessage { Path = "LGEN_GLOBAL_INFO_FB (FB18)", State = CompilerResultState.Error,
            Description = "'LGEN_GLOBAL_INFO_FB' is the instance of type in a library, but it calls 'LGEN_CLOCK_GEN_FB' that is not an instance of a type. "
                + "Add 'LGEN_CLOCK_GEN_FB' as a type to the library." };
        var program = Node("Program blocks", Node("_Lib", Node("LGen", Node("Initialization", libraryBlock))));
        var deviceItem = Node("+S1-K1", Node("Safety"), program);
        compiler.Result.Messages.Add(Node("+S1", deviceItem));
        compiler.Result.State = CompilerResultState.Error; compiler.Result.ErrorCount = 1;
        var foreign = Compile();
        Assert.True(foreign.ScopeMismatch); Assert.Equal(new[] { "+S1", "+S1-K1" }, foreign.CompiledRoots);
        Assert.Empty(foreign.Errors); Assert.Contains(foreign.Messages, m => m.Contains("Path=LGEN_GLOBAL_INFO_FB (FB18)"));
        Assert.Equal(1, foreign.ErrorCount);

        var block = Node("Main%one (FC9700)", new CompilerResultMessage { State = CompilerResultState.Error, Description = "Unknown operand" });
        program.Messages.Add(block); compiler.Result.ErrorCount = 2;
        var sameBlockName = Compile();
        Assert.True(sameBlockName.ScopeMismatch); Assert.Empty(sameBlockName.Errors);
        Assert.Contains(sameBlockName.Messages, m => m.Contains("Unknown operand"));
        deviceItem.Path = "CPU";
        var matchedItem = Compile();
        Assert.False(matchedItem.ScopeMismatch); Assert.Equal(new[] { "+S1", "CPU" }, matchedItem.CompiledRoots);
        Assert.Contains("Unknown operand", Assert.Single(matchedItem.Errors));
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")]
    [InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public void A_group_named_like_the_block_does_not_own_its_other_blocks_errors(string release)
    {
        var (engine, owner, compiler) = Target("block");
        engine.ReleaseKey = release;
        string name = Assert.IsType<PlcBlock>(owner).Name;
        engine.Software.BlockGroup.Groups[0].Name = name;
        var sibling = new CompilerResultMessage { Path = "Sibling (FB18)", State = CompilerResultState.Error, Description = "Sibling diagnostic" };
        var target = Node(name + " (FC9700)", new CompilerResultMessage { State = CompilerResultState.Error, Description = "Target diagnostic" });
        var dataType = new CompilerResultMessage { Path = name + " (UDT)", State = CompilerResultState.Error, Description = "Type diagnostic" };
        compiler.Result.Messages.Add(Node("D", Node("程序块", Node(name, sibling, target)), Node("PLC data types", dataType)));
        compiler.Result.ErrorCount = 3; compiler.Result.State = CompilerResultState.Error;
        var result = Assert.IsType<PlcScopedCompileResult>(
            engine.CompileSoftwareScoped("CPU", dryRun: false, scope: "block", scopePath: "Main%25one/Main%25one"));
        Assert.False(result.ScopeMismatch); Assert.Equal(new[] { "D", "程序块", "PLC data types" }, result.CompiledRoots);
        Assert.Equal(3, result.ErrorCount); Assert.Contains("Sibling diagnostic", Assert.Single(result.Messages, m => m.Contains("Path=Sibling (FB18)")));
        Assert.Contains(result.Messages, m => m.Contains("Type diagnostic"));
        Assert.Contains("Target diagnostic", Assert.Single(result.Errors));
    }

    [Fact]
    public void A_device_name_equal_to_the_block_name_is_only_a_station_root()
    {
        var (engine, owner, compiler) = Target("block");
        Assert.IsType<PlcBlock>(owner).Name = "D";
        compiler.Result.Messages.Add(Node("D", new CompilerResultMessage {
            Path = "Safety", Description = "Safety diagnostic", State = CompilerResultState.Error }));
        compiler.Result.ErrorCount = 1;
        var result = Assert.IsType<PlcScopedCompileResult>(
            engine.CompileSoftwareScoped("CPU", dryRun: false, scope: "block", scopePath: "Group%2fone/D"));
        Assert.False(result.ScopeMismatch); Assert.Empty(result.Errors); Assert.Contains(result.Messages, message => message.Contains("Safety diagnostic"));
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")]
    [InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public void Group_compile_reports_roots_and_respects_release_unit_support(string release)
    {
        string unit = release is "14sp1" or "15.1" ? "" : "Unit/one";
        var (engine, owner, compiler) = Target("group", unit);
        engine.ReleaseKey = release;
        if (unit == "")
            Assert.Equal(PlcCompileScope.UnavailableCondition, Assert.Throws<AdapterPreconditionException>(() =>
                engine.CompileSoftwareScoped("CPU", dryRun: false, scope: "group", scopePath: PathFor("group"), unitName: "Unit/one")).Condition);
        compiler.Result.Messages.Add(Node(engine.Software.Name, Node("Program blocks", Node("Group/one", Node("Main%one (FC9700)")))));
        var group = Assert.IsType<PlcScopedCompileResult>(engine.CompileSoftwareScoped("CPU", dryRun: false,
            scope: "group", scopePath: PathFor("group"), unitName: unit));
        Assert.False(group.ScopeMismatch); Assert.Equal(new[] { engine.Software.Name, "Program blocks" }, group.CompiledRoots);
        Assert.Equal(unit, group.UnitName); Assert.Equal(1, compiler.Calls); Assert.Equal(new[] { typeof(ICompilable) }, owner.ServiceReads);
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
