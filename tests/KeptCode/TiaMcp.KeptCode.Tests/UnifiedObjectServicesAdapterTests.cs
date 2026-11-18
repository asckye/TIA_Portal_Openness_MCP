using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class UnifiedObjectServicesAdapterTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Native_generic_runtime_writes_refuse_before_target_resolution_or_edit_access(bool multilingual, bool nested, bool dryRun)
    {
        string path = nested ? "[{\"property\":\"RuntimeSettings\"},{\"property\":\"OpcUaServerRuntimeSettings\"}]"
            : "[{\"property\":\"RuntimeSettings\"}]";
        var steps = new HmiArray(new HmiObject { ["property"] = "RuntimeSettings" });
        if (nested) steps.Add(new HmiObject { ["property"] = "OpcUaServerRuntimeSettings" });
        var previous = UnifiedRequestValues.Inputs;
        UnifiedRequestValues.Inputs = new Dictionary<string, object?> { [path] = steps };
        try
        {
            var session = new Session();
            var adapter = new UnifiedObjectServicesAdapter(session);
            var error = Assert.Throws<NotSupportedException>(() => {
                if (multilingual) adapter.UpdateUnifiedMultilingualProperty("HMI", path, "Text", "en-US", "value", dryRun);
                else adapter.UpdateUnifiedObjectProperties("HMI", path, "{}", dryRun);
            });
            Assert.Contains("Use SetUnifiedRuntimeSettings", error.Message);
            Assert.Equal(0, session.Resolves);
            Assert.Equal(0, session.EditAccess);
        }
        finally { UnifiedRequestValues.Inputs = previous; }
    }

    [Fact]
    public void Refused_native_plant_creation_verifies_the_exact_node_is_absent()
    {
        var views = new PlantViews();
        var session = new Session(views);
        var result = new UnifiedObjectServicesAdapter(session).ManageUnifiedPlantNode("/View_A", "create", dryRun: false);
        Assert.Equal(1, views.Attempts);
        Assert.Empty(views);
        Assert.Equal(false, result.Meta["mayHaveChanged"]);
        Assert.Equal("restored", result.Meta["rollbackOutcome"]);
        Assert.Equal("Plant view creation refused.", result.Meta["nativeMessage"]);
    }

    private sealed class PlantViews : List<object>
    {
        internal int Attempts;
        public object Create(string name) { Attempts++; throw new InvalidOperationException("Plant view creation refused."); }
    }

    private sealed class Access : IDisposable { public void Dispose() { } }
    private sealed class Session(PlantViews? views = null) : IUnifiedSession
    {
        internal int Resolves, EditAccess;
        public object? CurrentProject => null;
        public object? HmiReadFault => null;
        public MigrationPages MigrationPages => throw new InvalidOperationException();
        public object ExactUnifiedRoot(string path) { Resolves++; throw new InvalidOperationException("Unexpected native target resolution."); }
        public object ExactPlantViews() => views ?? throw new InvalidOperationException();
        public object ResolveHmiSoftwareOrThrow(string path) => ExactUnifiedRoot(path);
        public string UnifiedCollection(string category) => throw new InvalidOperationException();
        public IDisposable AcquireHmiEditAccess()
        {
            EditAccess++;
            return views != null ? new Access() : throw new InvalidOperationException("Unexpected edit access.");
        }
        public void RecordHmiReadFault(HmiObject meta) => throw new InvalidOperationException();
        public HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
        {
            var meta = new HmiObject();
            var message = views == null ? action(meta) : TiaMcp.Adapters.Native.Engineering.EngineeringWriteJournal.RunReported(meta, () => action(meta),
                error => error.Message);
            return new HmiMessage { Message = message, Meta = meta };
        }
    }
}
