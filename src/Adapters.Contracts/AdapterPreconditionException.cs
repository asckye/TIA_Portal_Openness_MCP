using System;

namespace TiaMcp.Adapters.Contracts
{
    // Thrown only by validation that finishes before the first native mutation.
    // Hosts use this type to distinguish a safe rejection from an uncertain call.
    public sealed class AdapterPreconditionException : ArgumentException
    {
        public bool IsArgument { get; }

        public AdapterPreconditionException(string message, string? parameter = null, bool isArgument = true, Exception? innerException = null)
            : base(message, parameter, innerException)
        {
            IsArgument = isArgument;
        }
    }
}
