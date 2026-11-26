using System;
using System.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private static PlcSoftwareFailure DescribeOrganisationFailure(PlcSoftwareRequest request, Exception error)
        {
            var cause = error.GetBaseException();
            string? loss = request.Action == "compile" && !string.IsNullOrEmpty(request.Password) && HmiReadPolicy.ConnectionUnavailable(error)
                ? "The connection was lost during password-based compilation." : null;
            var failure = new PlcSoftwareFailure {
                Code = error is PlcSoftwareException native ? native.Code : request.Operation == "DescribePlcBlockLogic"
                    && error is AdapterPreconditionException { Condition: "not-found" } or AdapterPreconditionException { IsArgument: true }
                    ? StepFailureStatus.Of(error) : error.GetType().Name, Message = loss ?? error.Message,
                Candidates = (error as PlcSoftwareException)?.Candidates?.ToArray() };
            if (SafetyLoginFailure.FromException(error) is { } login)
                login.RecordEvidence(failure.Evidence, compiling: true);
            else
            {
                failure.Evidence["nativeExceptionType"] = cause.GetType().FullName;
                failure.Evidence["nativeMessage"] = loss ?? cause.Message;
            }
            return failure;
        }
    }
}
