using System;

namespace Siemens.Engineering
{
    public class EngineeringException : Exception
    {
        public EngineeringException() { }
        public EngineeringException(Exception cause) : base("Native diagnostic.", cause) { }
    }
    public class EngineeringTargetInvocationException : EngineeringException
    {
        public EngineeringTargetInvocationException() { }
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
    public class EngineeringObjectDisposedException : EngineeringException { }
    public class NonRecoverableException : Exception { }
}

namespace Siemens.Engineering.Multiuser
{
    public sealed class MultiuserException : EngineeringTargetInvocationException { }
}
