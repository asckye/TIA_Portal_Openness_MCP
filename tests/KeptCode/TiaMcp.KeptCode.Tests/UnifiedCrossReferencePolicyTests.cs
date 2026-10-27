using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace Siemens.Engineering
{
    internal interface IEngineeringServiceProvider { public T GetService<T>(); }
}
namespace Siemens.Engineering.CrossReference
{
    public enum CrossReferenceFilter { AllObjects, ObjectsWithReferences, UnusedObjects }
    public sealed class CrossReferenceService
    {
        public int Calls;
        public CrossReferenceFilter LastFilter;
        public Result GetCrossReferences(CrossReferenceFilter filter)
        {
            Calls++; LastFilter = filter;
            return new Result();
        }
        public sealed class Result { public object[] Sources => Array.Empty<object>(); }
    }
}
namespace TiaMcp.Engine.Tests
{
    public sealed class UnifiedCrossReferencePolicyTests
    {
        [Theory]
        [InlineData("strings")]
        [InlineData("object")]
        [InlineData("unknown")]
        [InlineData("backlink")]
        public void Malformed_path_is_refused_before_root_or_native_service_access(string kind)
        {
            var previous = UnifiedRequestValues.Inputs;
            try
            {
                object input = kind == "strings" ? new HmiArray("Screens", "Main") : kind == "object" ? new HmiObject()
                    : kind == "unknown" ? new HmiArray(new HmiObject { ["property"] = "Screens", ["unknown"] = true })
                    : new HmiArray(new HmiObject { ["property"] = "Parent" });
                UnifiedRequestValues.Inputs = new Dictionary<string, object?> { ["fixture-path"] = input };
                var session = new Session();
                var error = Assert.Throws<ArgumentException>(() => UnifiedCrossReferences.Read(session, "HMI", "fixture-path"));
                Assert.Contains("Expected an array", error.Message);
                Assert.Equal(0, session.Resolves);
                Assert.Equal(0, session.Target.ServiceRequests);
                Assert.Equal(0, session.Target.Service.Calls);
            }
            finally { UnifiedRequestValues.Inputs = previous; }
        }
        public static IEnumerable<object?[]> Refusals => from release in new[] { "19", "20", "21" }
            from setting in new string?[] { null, "", "0", "true", " 1" }
            select new object?[] { release, setting };

        [Theory]
        [InlineData("19")]
        [InlineData("20")]
        [InlineData("21")]
        public void Explicit_opt_in_uses_the_existing_native_sequence_and_filter(string release)
        {
            using var scope = HardwareContract.UseRelease(release);
            WithSetting("1", () => {
                var session = new Session();
                var reply = UnifiedCrossReferences.Read(session, "HMI", "[]", "UnusedObjects");
                Assert.Equal(1, session.Resolves);
                Assert.Equal(1, session.Target.ServiceRequests);
                Assert.Equal(1, session.Target.Service.Calls);
                Assert.Equal(global::Siemens.Engineering.CrossReference.CrossReferenceFilter.UnusedObjects, session.Target.Service.LastFilter);
                Assert.True((bool)reply.Meta["apiCallSuccess"]!);
                Assert.True((bool)reply.Meta["traversalComplete"]!);
                Assert.False((bool)reply.Meta["dataComplete"]!);
            });
        }

        private static void WithSetting(string? setting, Action action)
        {
            string? previous = Environment.GetEnvironmentVariable(PlcCrossReferencePolicy.NativeQueryOptInVariable);
            try { Environment.SetEnvironmentVariable(PlcCrossReferencePolicy.NativeQueryOptInVariable, setting); action(); }
            finally { Environment.SetEnvironmentVariable(PlcCrossReferencePolicy.NativeQueryOptInVariable, previous); }
        }
        public sealed class TargetObject : global::Siemens.Engineering.IEngineeringServiceProvider
        {
            public int ServiceRequests;
            public global::Siemens.Engineering.CrossReference.CrossReferenceService Service = new();
            public T GetService<T>() { ServiceRequests++; return (T)(object)Service; }
        }
        private sealed class Session : IUnifiedSession
        {
            internal int Resolves;
            internal TargetObject Target = new();
            public object CurrentProject => this;
            public object? HmiReadFault => null;
            public MigrationPages MigrationPages => throw new NotSupportedException();
            public object ExactUnifiedRoot(string path) { Resolves++; return Target; }
            public object ExactPlantViews() => throw new NotSupportedException();
            public object ResolveHmiSoftwareOrThrow(string path) => ExactUnifiedRoot(path);
            public string UnifiedCollection(string category) => throw new NotSupportedException();
            public IDisposable AcquireHmiEditAccess() => throw new NotSupportedException();
            public void RecordHmiReadFault(HmiObject meta) => throw new NotSupportedException();
            public HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true)
            {
                var meta = new HmiObject();
                return new HmiMessage { Message = action(meta), Meta = meta };
            }
        }
    }
}
