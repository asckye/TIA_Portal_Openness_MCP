using System;

namespace Siemens.Engineering
{
    public class EngineeringException : Exception
    {
        public EngineeringException() { }
        protected EngineeringException(string message) : base(message) { }
        protected EngineeringException(string message, Exception cause) : base(message, cause) { }
        public EngineeringException(Exception cause) : base("Native diagnostic.", cause) { }
    }
    public class EngineeringTargetInvocationException : EngineeringException
    {
        public EngineeringTargetInvocationException() { }
        public EngineeringTargetInvocationException(string message) : base(message) { }
        public EngineeringTargetInvocationException(Exception cause) : base(cause) { }
    }
    public class EngineeringUserAbortException : EngineeringTargetInvocationException { }
    public class EngineeringSecurityException : EngineeringException
    {
        public EngineeringSecurityException() { }
        public EngineeringSecurityException(Exception cause) : base(cause) { }
    }
    public class MissingProductsException : EngineeringTargetInvocationException { }
    public class LicenseNotFoundException : EngineeringTargetInvocationException { }
    public class EngineeringObjectDisposedException : EngineeringException
    {
        public EngineeringObjectDisposedException() { }
        public EngineeringObjectDisposedException(string message) : base(message) { }
        public EngineeringObjectDisposedException(string message, Exception cause) : base(message, cause) { }
    }
    public class NonRecoverableException : Exception { }
}

namespace Siemens.Engineering.Multiuser
{
    public sealed class MultiuserException : EngineeringTargetInvocationException { }
}
