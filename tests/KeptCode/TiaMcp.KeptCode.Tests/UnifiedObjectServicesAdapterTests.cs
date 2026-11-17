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

    private sealed class Session : IUnifiedSession
    {
        internal int Resolves, EditAccess;
        public object? CurrentProject => null;
        public object? HmiReadFault => null;
        public MigrationPages MigrationPages => throw new InvalidOperationException();
        public object ExactUnifiedRoot(string path) { Resolves++; throw new InvalidOperationException("Unexpected native target resolution."); }
        public object ExactPlantViews() => throw new InvalidOperationException();
        public object ResolveHmiSoftwareOrThrow(string path) => ExactUnifiedRoot(path);
        public string UnifiedCollection(string category) => throw new InvalidOperationException();
        public IDisposable AcquireHmiEditAccess() { EditAccess++; throw new InvalidOperationException("Unexpected edit access."); }
        public void RecordHmiReadFault(HmiObject meta) => throw new InvalidOperationException();
        public HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
        {
            var meta = new HmiObject();
            return new HmiMessage { Message = action(meta), Meta = meta };
        }
    }
}
