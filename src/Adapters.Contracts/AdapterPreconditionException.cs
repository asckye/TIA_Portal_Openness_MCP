using System;

namespace TiaMcp.Adapters.Contracts
{
    // Thrown only by validation that finishes before the first native mutation, or (with a Condition) for a documented
    // Openness refusal such as EngineeringSecurityException that leaves the session unchanged (API audit 2026-10-19, F4).
    // Hosts use this type to distinguish a safe rejection from an uncertain call.
    public sealed class AdapterPreconditionException : InvalidOperationException
    {
        public bool IsArgument { get; }
        public string? ParamName { get; }
        // Explicit precondition code (for example openness-access-denied) reported as PRECONDITION_FAILED details.condition.
        public string? Condition { get; }

        public AdapterPreconditionException(string message, string? parameter = null, bool isArgument = true, Exception? innerException = null)
            : base(new ArgumentException(message, parameter).Message, innerException)
        {
            // Preserve the existing runtime-specific message, including its optional parameter suffix.
            ParamName = parameter;
            IsArgument = isArgument;
        }

        public AdapterPreconditionException(string message, string condition, Exception? innerException)
            : this(message, null, false, innerException) { Condition = condition; }
    }
}
