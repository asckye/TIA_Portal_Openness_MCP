using System;
using System.Collections;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using TiaMcp.Adapters.Reflection;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public Func<string?>? EngineeringReflectionLookupPaths { get; set; }
        public Func<object, object?>? RenderEngineeringReflectionValue { get; set; }

        private object? ResolveReflectionReadback(string kind, string path, string software, bool write)
        {
            // Session roots are otherwise returned straight from cached fields.
            if (string.Equals(kind.Trim(), "Project", StringComparison.OrdinalIgnoreCase))
                return lifecycle.ProjectFile is { Length: > 0 } file ? ExactOpenProject(file) : null;
            if (string.Equals(kind.Trim(), "Portal", StringComparison.OrdinalIgnoreCase)) return null;
            return ResolveReflectionObject(kind, path, software, write);
        }

        public OpennessReflectionReply ExecuteOpennessReflection(OpennessReflectionRequest request, bool dryRun = true)
        {
            Check();
            if (dryRun == request.AllowWrite) throw new ArgumentException("Conflicting reflection write mode.");
            var service = new OpennessReflection(new ReflectionSession(ResolveReflectionObject,
                () => EngineeringReflectionLookupPaths != null ? EngineeringReflectionLookupPaths() : reflectionLookupPaths, ResolveReflectionReadback));
            var reply = new OpennessReflectionReply();
            try
            {
                reply.Observation = request.Operation switch {
                    "DescribeObject" => service.DescribeObject(request.ObjectKind, request.ObjectPath, request.SoftwarePath, request.Limit),
                    "DescribeObjectProperty" => service
                        .DescribeObjectProperty(request.ObjectKind, request.ObjectPath, request.PropertyPath, request.SoftwarePath, request.Limit),
                    "GetObjectProperty" => service.GetObjectProperty(request.ObjectKind, request.ObjectPath, request.PropertyPath, request.SoftwarePath),
                    "ListObjectChildren" => service
                        .ListObjectChildren(request.ObjectKind, request.ObjectPath, request.PropertyPath, request.SoftwarePath, request.Limit),
                    "DescribeService" => service
                        .DescribeService(request.ObjectKind, request.ObjectPath, request.ServiceTypeSuffix, request.SoftwarePath, request.Limit),
                    "PreviewSetAttribute" when !request.AllowWrite => service.PreviewSetAttribute(request.ObjectKind, request.ObjectPath, request.SoftwarePath,
                        request.PropertyPath),
                    "InvokeObject" => service
                        .InvokeObject(request.ObjectKind, request.ObjectPath, request.MethodName, request.Arguments, request.SoftwarePath, request.AllowWrite),
                    "InvokeService" => service
                        .InvokeService(request.ObjectKind, request.ObjectPath, request.ServiceTypeSuffix, request.MethodName, request.Arguments, request
                            .SoftwarePath, request.AllowWrite),
                    _ => throw new ArgumentException("Unregistered reflection operation: " + request.Operation)
                };
                if (reply.Observation.Value != null)
                    reply.Observation.Value = RenderEngineeringReflectionValue != null ? RenderEngineeringReflectionValue(reply.Observation.Value)
                        : LibraryValue(reply.Observation.Value);
                bool invoked = reply.Observation.Meta.TryGetValue("invocationAttempted", out var attempted) && Equals(attempted, true);
                reply.MayHaveChanged = request.AllowWrite && invoked && !MutationEvidence.ProvesNoMutation(reply.Observation.Meta);
                reply.OutcomeUnknown = reply.MayHaveChanged && !Equals(reply.Observation.Meta["success"], true);
                if (reply.OutcomeUnknown)
                { reply.Observation.Meta["writeOutcomeUnknown"] = true; }
            }
            catch (Exception error) when (error.GetBaseException() is not NonRecoverableException)
            {
                reply.Failure = EngineeringReflectionFailure != null ? EngineeringReflectionFailure(error) : new PlcSoftwareFailure {
                    Code = error is PlcSoftwareException native ? native.Code : error.GetType().Name,
                    Message = error.Message, Candidates = (error as PlcSoftwareException)?.Candidates?.ToArray() };
                reply.Failure.Evidence["mayHaveChanged"] = reply.MayHaveChanged;
                reply.Failure.Evidence["status"] = StepFailureStatus.Of(error);
                reply.Failure.Evidence["nativeMessage"] = error.GetBaseException().Message;
                if (StepFailureStatus.Parameter(error) is string parameter) reply.Failure.Evidence["parameter"] = parameter;
                StepFailureStatus.RecordTarget(error, reply.Failure.Evidence);
            }
            return reply;
        }

    }
}
