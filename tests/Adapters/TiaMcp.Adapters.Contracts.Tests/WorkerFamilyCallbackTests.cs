using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Siemens.Engineering.Online;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Native.Hmi;
using Xunit;

namespace TiaMcp.Adapters.Contracts.Tests;

public sealed class WorkerFamilyCallbackTests
{
    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Every_family_binding_uses_the_same_adapter_and_preserves_arguments(string release)
    {
        var engine = Create(release);
        Assert.Empty(engine.FamilyCalls);
        Assert.True(engine.OrganisationSessionConfigured);
        Assert.Same(engine.FamilyObject, engine.ResolveEngineeringBlock!("PLC", "Folder/Block"));
        Assert.Same(engine.FamilyObject, engine.ResolveEngineeringDevice!("Station"));
        Assert.Same(engine.FamilyItem, engine.ResolveEngineeringItem!("Station/CPU"));
        Assert.NotNull(engine.ResolveEngineeringSoftwareContainer!("HMI"));
        Assert.Same(engine.FamilyObject, engine.ResolveEngineeringHmiSoftware!("HMI"));
        Assert.Equal((engine.FamilyObject, "/Folder/Screen"), engine.ResolveEngineeringHmiScreen!("HMI", "/Folder/Screen"));
        Assert.Same(engine.FamilyObject, engine.ResolveEngineeringLibraryDevice!("[\"Group\",\"Station\"]"));
        Assert.Same(engine.FamilyItem, engine.ResolveUnifiedHmiDeviceItem!("Station/HMI"));
        Assert.Equal((engine.FamilyObject, "/Scripts/Exact%2FName", false), engine.ResolveEngineeringUnifiedScripts!("/Scripts/Exact%2FName", "HMI", false));
        Assert.Equal((engine.FamilyObject, "/Scripts", true), engine.ResolveEngineeringUnifiedScripts!("/Scripts", "HMI", true));
        Assert.Equal(new[] { "block:PLC:Folder/Block", "device:Station", "item:Station/CPU", "container:HMI", "container:HMI",
            "container:HMI", "hardware:Group/Station:", "item:Station/HMI", "container:HMI", "container:HMI" }, engine.FamilyCalls);

        var cause = new PlcSoftwareException("NotFound", "exact object missing", new[] { "candidate" });
        Assert.Equal(cause.ToString(), engine.FormatEngineeringException!(cause));
        var failure = engine.EngineeringReflectionFailure!(cause);
        Assert.Equal("NotFound", failure.Code);
        Assert.Equal(cause.Message, failure.Message);
        Assert.Equal(new[] { "candidate" }, failure.Candidates);
        Assert.Same(engine.FamilyObject, engine.RenderEngineeringValue!(engine.FamilyObject));
        Assert.Same(engine.FamilyObject, engine.RenderEngineeringReflectionValue!(engine.FamilyObject));
        Assert.Equal("option:package", engine.EngineeringLibraryOptionKind!("package"));
        Assert.Equal("Folder/Subfolder", engine.EngineeringBlockGroupPath!(new Siemens.Engineering.SW.Blocks.PlcBlockGroup()));
        Assert.Equal("exact/path", engine.PlcB5ResolvedPath!(engine.FamilyObject));
        Assert.Equal("", engine.PlcB5ResolvedPath!(null));
        Assert.Equal("preflight:document", engine.PlcB5DocumentPreflight!("document"));
        engine.EngineeringRecordExportPath!("output.xml");
        Assert.Equal("output.xml", engine.WorkerAnalysisExportPath);
        engine.AdmitHardwareAmlExport!("output.aml");
        Assert.Equal("output.aml", HardwareAmlPolicy.LastPath);
        engine.UpdateEngineeringReflectionLookupPaths!(" Available PLC paths: PLC");
        Assert.Equal(" Available PLC paths: PLC", engine.EngineeringReflectionLookupPaths!());
        Assert.Same(engine.FamilyPlc, Assert.Single(engine.OnlinePlcs!()));
        engine.OnlineLog!(3, cause, "failure", new object?[] { "PLC" });
        engine.OnlineLog!(2, null, "warning", Array.Empty<object?>());
        engine.OnlineLog!(1, null, "information", Array.Empty<object?>());
        engine.OnlineLog!(0, null, "debug", Array.Empty<object?>());
        Assert.Equal("log:failure", engine.FamilyCalls.Last());
        HmiReadTrace.Lines.Clear();
        engine.WriteEngineeringHmiTrace!("trace.log", "row");
        Assert.Equal(("trace.log", "row"), Assert.Single(HmiReadTrace.Lines));
    }

    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Steps_preserve_project_admission_and_lock_unknown_writes(string release)
    {
        var engine = Create(release);
        int calls = 0;
        var missing = engine.EngineeringStep!("Tool", _ => { calls++; return "unexpected"; });
        Assert.Equal("Project is null", missing.Message);
        Assert.Equal("InvalidState", missing.Meta["status"]);
        Assert.False((bool)missing.Meta["operationSuccess"]!);
        Assert.Equal(0, calls);
        var independent = engine.EngineeringServicesStep!("CompareLibraries", false, _ => { calls++; return "read"; });
        Assert.Equal("read", independent.Message);
        Assert.True((bool)independent.Meta["operationSuccess"]!);
        Bind(engine);
        var hardware = engine.EngineeringStep!("Hardware", _ => throw new HardwareAddressingException("NotFound", "missing"));
        Assert.Equal("NotFound", hardware.Meta["status"]);
        Assert.False(hardware.RequiresSessionReset);
        var changed = engine.EngineeringStep!("Write", meta => { meta["mayHaveChanged"] = true; throw new InvalidOperationException("failed after write"); });
        Assert.True(changed.RequiresSessionReset);
        Assert.False((bool)changed.Meta["operationSuccess"]!);
        var unknown = engine.EngineeringServicesStep!("Write", true, meta =>
        { meta["mayHaveChanged"] = true; meta["writeOutcomeUnknown"] = true; return "unknown"; });
        Assert.True(unknown.RequiresSessionReset);
        var native = engine.EngineeringStep!("Native", _ => throw new Siemens.Engineering.EngineeringException("native error"));
        Assert.Equal("native message", ((Dictionary<string, object?>)native.Meta["messageData"]!)["text"]);
        Assert.False((bool)native.Meta["apiCallSuccess"]!);
        Assert.Equal(3, engine.ExportFailures);
        var unavailable = engine.EngineeringStep!("Read", meta => { meta["connectionUnavailable"] = true; return "partial"; });
        Assert.True(unavailable.RequiresSessionReset);
    }

    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Hmi_connection_faults_block_every_step_until_explicit_project_binding(string release)
    {
        var engine = Create(release);
        Bind(engine);
        var properties = new List<object?> { "Name" };
        var meta = new Dictionary<string, object?> { ["error"] = "remote connection unavailable", ["appliedProperties"] = properties };
        engine.HmiReadFault!(meta);
        properties.Add("mutated after recording");
        Assert.True((bool)meta["connectionUnavailable"]!);
        Assert.True((bool)meta["requiresExplicitRebind"]!);
        var snapshot = engine.ReadEngineeringHmiReadFault!()!;
        Assert.Single((List<object?>)snapshot["appliedProperties"]!);
        snapshot["error"] = "mutated returned fault";
        Assert.Equal("remote connection unavailable", engine.ReadEngineeringHmiReadFault!()!["error"]);
        engine.ClearCalls();
        var blocked = engine.EngineeringServicesStep!("CompareLibraries", false, _ => throw new Exception("must not run"));
        Assert.Equal("HmiReadSessionBlocked", blocked.Meta["status"]);
        Assert.True(blocked.RequiresSessionReset);
        Assert.Empty(engine.NativeCalls);
        var project = engine.NewProject("Replacement");
        engine.ProjectReleased!();
        engine.ProjectRetrieved!(project);
        Assert.Null(engine.ReadEngineeringHmiReadFault!());
        Assert.Equal("read", engine.EngineeringStep!("Tool", _ => "read").Message);
        var lost = engine.EngineeringStep!("Tool", _ => throw new ObjectDisposedException("Openness handle"));
        Assert.True(lost.RequiresSessionReset);
        Assert.True((bool)lost.Meta["connectionUnavailable"]!);
        Assert.NotNull(engine.ReadEngineeringHmiReadFault!());
    }

    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Document_operations_require_positive_selected_plc_offline_evidence(string release)
    {
        var engine = Create(release);
        int calls = 0;
        engine.FamilyProvider.State = OnlineState.Offline;
        Assert.True(engine.PlcB5AutoOffline!("PLC", () => { calls++; return true; }));
        Assert.Equal(new[] { "plc:PLC:False", "service:PLC" }, engine.FamilyCalls);
        Assert.Equal(1, calls);
        foreach (var state in new[] { OnlineState.Online, OnlineState.Unknown })
        {
            engine.FamilyProvider.State = state;
            Assert.ThrowsAny<Exception>(() => engine.PlcB5AutoOffline!("PLC", () => { calls++; return true; }));
            Assert.Equal(1, calls);
        }
        engine.MissingProvider = true;
        Assert.ThrowsAny<Exception>(() => engine.PlcB5AutoOffline!("PLC", () => { calls++; return true; }));
        Assert.Equal(1, calls);
        engine.MissingProvider = false;
        engine.FamilyProvider.State = OnlineState.Offline;
        var error = new InvalidOperationException("not permitted in online mode");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => engine.PlcB5AutoOffline!("PLC", () => { calls++; throw error; })));
        Assert.Equal(2, calls);
        Assert.DoesNotContain(engine.FamilyCalls, call => call.Contains("GoOffline", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Tokens_ignore_inspection_timing_and_expire_on_every_binding_boundary(string release)
    {
        var engine = Create(release);
        Bind(engine);
        var content = new Dictionary<string, object?> { ["kind"] = "InspectionSnapshot", ["elapsedMs"] = 1, ["nodes"] = new[] { "first" } };
        string original = engine.HmiContentToken!("screen", content);
        content["elapsedMs"] = 999;
        Assert.Equal(original, engine.HmiContentToken!("screen", content));
        Assert.Equal(999, content["elapsedMs"]);
        Assert.NotEqual(original, engine.HmiContentToken!("other screen", content));
        engine.RefreshWorkerSessionBinding();
        Assert.Equal(original, engine.HmiContentToken!("screen", content));
        engine.ReplaceProjectHandle(engine.NewProject("ActualName"));
        engine.RefreshWorkerSessionBinding();
        string replacement = engine.HmiContentToken!("screen", content);
        Assert.NotEqual(original, replacement);
        engine.ProjectSavedAs!();
        string saved = engine.HmiContentToken!("screen", content);
        Assert.NotEqual(replacement, saved);
        engine.UpdateEngineeringReflectionLookupPaths!("paths");
        engine.EngineeringRecordExportPath!("file.xml");
        engine.HmiReadFault!(new Dictionary<string, object?> { ["error"] = "fault" });
        engine.ProjectReleased!();
        Assert.NotEqual(saved, engine.HmiContentToken!("screen", content));
        Assert.Null(engine.ReadEngineeringHmiReadFault!());
        Assert.Null(engine.EngineeringReflectionLookupPaths!());
        Assert.Null(engine.WorkerAnalysisExportPath);
        var plain = new Dictionary<string, object?> { ["elapsedMs"] = 1 };
        string guarded = engine.HmiContentToken!("plain", plain);
        plain["elapsedMs"] = 2;
        Assert.NotEqual(guarded, engine.HmiContentToken!("plain", plain));
        engine.ProjectRetrieved!(engine.NewProject("ActualName"));
        string beforeLoss = engine.HmiContentToken!("screen", content);
        engine.ReplaceProjectHandle(null!);
        engine.RefreshWorkerSessionBinding();
        Assert.NotEqual(beforeLoss, engine.HmiContentToken!("screen", content));
        string cleared = engine.HmiContentToken!("screen", content);
        engine.RefreshWorkerSessionBinding();
        Assert.Equal(cleared, engine.HmiContentToken!("screen", content));
    }

    private static PlcFoundationEngine Create(string release)
    {
        var engine = new PlcFoundationEngine(release);
        engine.ConfigureFixture();
        engine.ConfigureWorkerFamilyCallbacks(text => "preflight:" + text,
            path => JsonSerializer.Deserialize<string[]>(path)!);
        engine.ClearCalls();
        return engine;
    }
    private static void Bind(PlcFoundationEngine engine)
    {
        engine.ProjectConnectionCreated!(engine.Connection);
        engine.ProjectRetrieved!(engine.NewProject("ActualName"));
    }
}
