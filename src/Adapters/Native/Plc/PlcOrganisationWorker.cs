using System;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcSoftwareReply ExecutePlcOrganisation(PlcSoftwareRequest request, bool dryRun = true)
        {
            Check();
            if (request.DryRun != dryRun) throw new ArgumentException("Conflicting PLC preview mode.");
            string? unavailable = PlcSoftwareCapabilities
                .Unsupported(ReleaseKey, request.Operation, request.Action, request.Family, request.UnitName, request.UnitKind, request.TargetKind, request
                    .CopyMode, request.GenerateOption);
            unavailable ??= PlcB4Capabilities.Unsupported(ReleaseKey, request);
            if (unavailable != null) throw new NotSupportedException(unavailable);
            var adapter = CreateOrganisationAdapter();
            var reply = new PlcSoftwareReply();
            try
            {
                DispatchOrganisationRequest(request, dryRun, adapter, reply);
            }
            catch (Exception error) when (error.GetBaseException() is not NonRecoverableException)
            {
                reply.Failure = DescribeOrganisationFailure(request, error);
                if (request.Action == "importTagTable" && error.Data["verifiedAbsent"] is true)
                    reply.Failure.Evidence["verifiedAbsent"] = true;
                reply.Failure.Evidence["mayHaveChanged"] = adapter.MutationStarted;
                reply.Failure.Evidence["status"] = StepFailureStatus.Of(error);
                if (StepFailureStatus.Parameter(error) is string parameter) reply.Failure.Evidence["parameter"] = parameter;
                StepFailureStatus.RecordTarget(error, reply.Failure.Evidence);
                reply.MayHaveChanged = adapter.MutationStarted; reply.OutcomeUnknown = adapter.MutationStarted;
                if (request.Operation == "MovePlcBlockToGroup") reply.Failure.Evidence["deleteStarted"] = adapter.MutationStarted;
                PlcMovePolicy.MergeEvidence(error, reply.Failure.Evidence);
                if (request.Operation == "MovePlcBlockToGroup" && PlcMovePolicy.SettledByRestore(reply.Failure.Evidence))
                    reply.OutcomeUnknown = false;
            }
            reply.MayHaveChanged |= adapter.MutationStarted;
            return reply;
        }
    }
}
