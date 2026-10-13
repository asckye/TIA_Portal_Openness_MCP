using System;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static partial class RuntimeToolContract
    {
        private sealed class InputFailureException : Exception
        {
            internal Error Error { get; }
            internal InputFailureException(Error error) => Error = error;
        }

        internal static string Json<T>(T value, string parameter, InputContract<T> contract)
        {
            var result = contract.Validate(value, parameter);
            if (result.Error != null) throw new InputFailureException(result.Error);
            return V4Json.Serialize(result.Value);
        }

        internal static string Writes(WriteValue[] value, string parameter, bool dryRun, bool confirmWrite,
            Func<string, NativeValuePolicy>? targetValuePolicy = null)
            => Json(value, parameter, WriteValueValidator.Create(NativeValueValidator.Scalar(), dryRun, confirmWrite,
                targetValuePolicy: targetValuePolicy));
    }
}
