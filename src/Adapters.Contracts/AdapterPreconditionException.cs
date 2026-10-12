using System;

namespace TiaMcp.Adapters.Contracts
{
    // Thrown only by validation that finishes before the first native mutation.
    // Hosts use this type to distinguish a safe rejection from an uncertain call.
    public sealed class AdapterPreconditionException : InvalidOperationException
    {
        public bool IsArgument { get; }
        public string? ParamName { get; }

        public AdapterPreconditionException(string message, string? parameter = null, bool isArgument = true, Exception? innerException = null)
            : base(new ArgumentException(message, parameter).Message, innerException)
        {
            // Preserve the existing runtime-specific message, including its optional parameter suffix.
            ParamName = parameter;
            IsArgument = isArgument;
        }
    }
}
