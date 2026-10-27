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
        public static IEnumerable<object?[]> Refusals => from release in new[] { "19", "20", "21" }
            from setting in new string?[] { null, "", "0", "true", " 1" }
            select new object?[] { release, setting };

        [Theory, MemberData(nameof(Refusals))]
        public void Default_policy_refuses_before_resolving_any_native_object(string release, string? setting)
        {
            using var scope = HardwareContract.UseRelease(release);
            WithSetting(setting, () => {
                Assert.Equal(TiaMcpServer.Siemens.CrossReferenceGuardLogic.PolicyRefusal(setting),
                    PlcCrossReferencePolicy.PolicyRefusal(setting));
                var session = new Session();
                // A valid typed-root path must not reach the native resolver while disabled.
                var reply = UnifiedCrossReferences.Read(session, "HMI", "[]");
                Assert.Equal(0, session.Resolves);
                Assert.Equal(0, session.Target.ServiceRequests);
                Assert.Equal(0, session.Target.Service.Calls);
                Assert.Equal(PlcCrossReferencePolicy.PolicyRefusal(setting, unified: true), reply.Message);
                var result = HmiInspectionContract.Map("GetUnifiedCrossReferences",
                    new ResponseMessage { Message = reply.Message, Meta = System.Text.Json.JsonSerializer.SerializeToNode(reply.Meta)!.AsObject() }, false, false);
                var body = result.StructuredContent!;
                Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
                Assert.Equal(release, (string?)body["meta"]?["releaseKey"]);
                Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
                Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
                Assert.Equal("none", (string?)body["meta"]?["completeness"]);
                Assert.False((bool)body["data"]!["queried"]!);
                Assert.Contains("does not mean there are no references", (string)body["data"]!["reason"]!);
                Assert.Contains(PlcCrossReferencePolicy.NativeQueryOptInVariable + "=1", reply.Message);
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
