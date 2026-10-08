using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Native.Plc;
using Xunit;

public sealed class NativeAdapterTests
{
    private sealed class Compiler : ICompilable
    {
        internal int Calls;
        internal CompilerResult Result = new();
        internal Action? During;
        public CompilerResult Compile() { Calls++; During?.Invoke(); return Result; }
    }
    private sealed class Fixture
    {
        internal readonly Compiler Compiler = new();
        internal readonly DeviceItem Cpu = new();
        internal readonly SafetyAdministration Safety = new();
        internal readonly PlcSoftware Software = new();
        internal readonly CompileObservation State = new() { Binding = new() { ProcessId = 1, ProcessStartUtc = DateTimeOffset.Parse("2026-10-05T00:00:00Z"), ProjectFile = "C:\\project\\Project.ap21", Epoch = 1 }, ObjectValidity = "valid", Dirty = false };
        internal readonly List<int> Threads = new();
        internal CompileNativeTarget Target;
        internal readonly CompileAdapter Adapter;
        internal Fixture()
        {
            Cpu.Services[typeof(SoftwareContainer)] = new SoftwareContainer { Software = Software };
            Cpu.Services[typeof(OnlineProvider)] = new OnlineProvider();
            Target = new() { Owner = Software, Software = Software, SafetyItem = Cpu, OfflineOwner = Cpu, Kind = "PlcSoftware", Name = "PLC_1", Compiler = Compiler };
            Adapter = new(() => new CompileObservation { Binding = State.Binding, ObjectValidity = State.ObjectValidity, Dirty = State.Dirty }, () => Target, () => Threads.Add(Environment.CurrentManagedThreadId));
        }
        internal CompileCheck Check(bool password = false)
        {
            var check = new CompileCheck { Request = new() { PasswordProvided = password }, Before = Adapter.Observe() }; check.Digest = CompilePrimitives.Digest(check); return check;
        }
    }
    [Theory]
    [InlineData("PlcSoftware")][InlineData("HmiTarget")][InlineData("HmiSoftware via Device")][InlineData("Device")][InlineData("DeviceItem")]
    public void NativeAdapterCompilesOnlyTheReviewedCompilerOnTheCallingThread(string kind)
    {
        var f = new Fixture(); f.Target.Kind = kind; f.Target.SafetyItem = null;
        int caller = Environment.CurrentManagedThreadId; var check = f.Check(); var other = new Compiler();
        var result = f.Adapter.Execute(check, ""); Assert.True(result.CompileIssued); Assert.Null(result.Fault); Assert.False(result.RequiresSessionReset);
        Assert.Equal(1, f.Compiler.Calls); Assert.Equal(0, other.Calls); Assert.All(f.Threads, thread => Assert.Equal(caller, thread));
        Assert.Equal(check.Before.TargetId, result.After!.TargetId); Assert.Equal(kind, result.After.TargetKind);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void OnlineOrMissingProviderRefusesBeforeNativeCompile(bool missing)
    {
        var f = new Fixture(); if (missing) f.Cpu.Services.Remove(typeof(OnlineProvider)); else ((OnlineProvider)f.Cpu.Services[typeof(OnlineProvider)]).State = OnlineState.Online;
        var result = f.Adapter.Execute(f.Check(), ""); Assert.False(result.CompileIssued); Assert.Equal(missing ? "precondition" : "offline", result.Fault!.Kind); Assert.Equal(0, f.Compiler.Calls);
    }
    [Fact]
    public void DeviceOfflineInventoryIncludesNestedPlcButSoftwareCompileDoesNotIncludeOtherDevices()
    {
        var f = new Fixture(); var other = new DeviceItem { Name = "Other_CPU" };
        other.Services[typeof(SoftwareContainer)] = new SoftwareContainer { Software = new PlcSoftware() }; other.Services[typeof(OnlineProvider)] = new OnlineProvider { State = OnlineState.Online };
        var device = new HardwareObject(); device.DeviceItems.Add(f.Cpu); device.DeviceItems.Add(other);
        Assert.Equal("offline", f.Adapter.Observe().OfflineState);
        f.Target = new() { Owner = device, OfflineOwner = device, Kind = "Device", Name = "Device", Compiler = f.Compiler };
        var observed = f.Adapter.Observe(); Assert.Equal("online", observed.OfflineState); Assert.Contains("Device/Other_CPU", observed.OfflineTargets);
    }
    [Fact]
    public void ExactNativeTargetReplacementInvalidatesBeforeCompile()
    {
        var f = new Fixture(); var check = f.Check(); f.Target.Owner = new PlcSoftware();
        var result = f.Adapter.Execute(check, ""); Assert.Equal("identity", result.Fault!.Kind); Assert.False(result.CompileIssued); Assert.Equal(0, f.Compiler.Calls);
    }
    [Fact]
    public void NativeDiagnosticTreeRetainsNestedPathDescriptionAndRootCounts()
    {
        var f = new Fixture(); f.Compiler.Result.ErrorCount = 1;
        var root = new CompilerResultMessage { State = CompilerResultState.Success, Description = "group" };
        root.Messages.Add(new() { State = CompilerResultState.Error, Path = "PLC_1/Block_1", Description = "leaf", ErrorCount = 1 }); f.Compiler.Result.Messages.Add(root);
        var result = f.Adapter.Execute(f.Check(), ""); Assert.Null(result.Fault); var d = result.Diagnostics!;
        Assert.Equal(1, d.RootErrorCount); Assert.Equal(1, d.LeafErrorCount); Assert.True(d.CountsConsistent); Assert.Equal("leaf", d.Messages[0].Messages[0].Description);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void RealAdapterSafetyPrimitivesKeepExistingLoginAndCleanCreatedLogin(bool existing)
    {
        var f = new Fixture(); f.Cpu.Services[typeof(SafetyAdministration)] = f.Safety; f.Safety.IsLoggedOnToSafetyOfflineProgram = existing;
        var result = f.Adapter.Execute(f.Check(true), "secret");
#if PLC_SAFETY
        Assert.Null(result.Fault); Assert.Equal(existing ? 0 : 1, f.Safety.LoginCalls); Assert.Equal(existing ? 0 : 1, f.Safety.LogoutCalls);
        Assert.Equal(existing, f.Safety.IsLoggedOnToSafetyOfflineProgram); Assert.Equal(1, f.Compiler.Calls);
#else
        Assert.Equal("unsupported", result.Fault!.Kind); Assert.False(result.CompileIssued); Assert.Equal(0, f.Safety.LoginCalls); Assert.Equal(0, f.Safety.LogoutCalls);
#endif
    }
    [Theory]
    [InlineData("login")][InlineData("compile")][InlineData("logout")]
    public void NativePrimitiveFaultsRetainCleanupAndNeverRetry(string stage)
    {
        var f = new Fixture(); f.Cpu.Services[typeof(SafetyAdministration)] = f.Safety;
        if (stage == "login") f.Safety.LoginAction = () => throw new IOException();
        if (stage == "compile") f.Compiler.During = () => throw new IOException();
        if (stage == "logout") f.Safety.LogoutAction = () => throw new IOException();
        var result = f.Adapter.Execute(f.Check(true), "secret");
#if PLC_SAFETY
        Assert.Equal(1, f.Safety.LoginCalls); Assert.Equal(1, f.Safety.LogoutCalls); Assert.Equal(stage == "login" ? 0 : 1, f.Compiler.Calls);
        if (stage == "logout") { Assert.NotNull(result.CleanupFault); Assert.Equal("failed", result.CleanupState); Assert.Equal("logged-on", result.After!.SafetyPermission); }
        else Assert.Equal("succeeded", result.CleanupState);
        Assert.Equal(stage == "compile", result.RequiresSessionReset);
#else
        Assert.Equal("unsupported", result.Fault!.Kind); Assert.Equal(0, f.Compiler.Calls);
#endif
    }
    [Fact]
    public void NativeLoginReturningWithoutPermissionDoesNotCompileOrLogout()
    {
        var f = new Fixture(); f.Cpu.Services[typeof(SafetyAdministration)] = f.Safety;
        f.Safety.LoginAction = () => f.Safety.IsLoggedOnToSafetyOfflineProgram = false;
        var result = f.Adapter.Execute(f.Check(true), "secret");
#if PLC_SAFETY
        Assert.NotNull(result.Fault); Assert.False(result.LoginCreated); Assert.False(result.RequiresSessionReset);
        Assert.Equal(1, f.Safety.LoginCalls); Assert.Equal(0, f.Safety.LogoutCalls); Assert.Equal(0, f.Compiler.Calls);
#else
        Assert.Equal("unsupported", result.Fault!.Kind); Assert.Equal(0, f.Compiler.Calls);
#endif
    }
    [Fact]
    public void NativeLogoutReturningWithoutClearingPermissionKeepsCleanupFailure()
    {
        var f = new Fixture(); f.Cpu.Services[typeof(SafetyAdministration)] = f.Safety; f.Safety.LeaveLoggedOn = true;
        var result = f.Adapter.Execute(f.Check(true), "secret");
#if PLC_SAFETY
        Assert.NotNull(result.CleanupFault); Assert.Equal("failed", result.CleanupState); Assert.False(result.RequiresSessionReset);
        Assert.Equal("logged-on", result.After!.SafetyPermission); Assert.Equal(1, f.Safety.LogoutCalls); Assert.Equal(1, f.Compiler.Calls);
#else
        Assert.Equal("unsupported", result.Fault!.Kind); Assert.Equal(0, f.Compiler.Calls);
#endif
    }
}
