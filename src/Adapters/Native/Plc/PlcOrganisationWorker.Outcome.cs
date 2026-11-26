using System;
using System.Linq;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        internal static PlcSoftwareReply RunOrganisationOperation(PlcSoftwareRequest request, PlcOrganisationAdapter adapter,
            Action<PlcSoftwareReply> operation)
        {
            var reply = new PlcSoftwareReply();
            try { operation(reply); }
            catch (Exception error) when (error.GetBaseException() is not NonRecoverableException)
            {
                var cause = error.GetBaseException();
                string? loss = request.Action == "compile" && !string.IsNullOrEmpty(request.Password) && HmiReadPolicy.ConnectionUnavailable(error)
                    ? "The connection was lost during password-based compilation." : null;
                reply.Failure = new PlcSoftwareFailure {
                    Code = error is PlcSoftwareException native ? native.Code : request.Operation == "DescribePlcBlockLogic"
                        && error is AdapterPreconditionException { Condition: "not-found" } or AdapterPreconditionException { IsArgument: true }
                        ? StepFailureStatus.Of(error) : error.GetType().Name, Message = loss ?? error.Message,
                    Candidates = (error as PlcSoftwareException)?.Candidates?.ToArray() };
                if (SafetyLoginFailure.FromException(error) is { } login)
                    login.RecordEvidence(reply.Failure.Evidence, compiling: true);
                else
                {
                    reply.Failure.Evidence["nativeExceptionType"] = cause.GetType().FullName;
                    reply.Failure.Evidence["nativeMessage"] = loss ?? cause.Message;
                }
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
