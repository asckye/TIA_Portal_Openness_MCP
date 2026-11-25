using System;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    // Documented Openness refusals (API audit 2026-10-19, F4): each type only on the releases whose SDK defines it.
    // MissingProducts/LicenseNotFound/Multiuser derive from EngineeringTargetInvocationException, EngineeringSecurityException
    // from EngineeringException; the exact type decides, never the message text.
    internal static class OpennessRefusalMapping
    {
        internal static Exception? Refusal(Exception error, out string? code)
        {
            code = null;
            for (var cause = error; cause != null; cause = cause.InnerException)
            {
                if (cause is AdapterPreconditionException) return null;
                if (cause is EngineeringSecurityException) code = OpennessRefusals.AccessDenied;
#if OPENNESS_MISSING_PRODUCTS
                else if (cause is MissingProductsException) code = OpennessRefusals.MissingProducts;
#endif
#if OPENNESS_LICENSE_EXCEPTION
                else if (cause is LicenseNotFoundException) code = OpennessRefusals.LicenseNotFound;
#endif
#if PROJECT_MULTIUSER
                else if (cause is Siemens.Engineering.Multiuser.MultiuserException) code = OpennessRefusals.MultiuserRefused;
#endif
                if (code != null) return cause;
            }
            return null;
        }

        internal static T Run<T>(string operation, bool credentialsGiven, Func<T> call)
        {
            try { return call(); }
            catch (Exception error) when (Refusal(error, out _) != null)
            {
                var native = Refusal(error, out var code)!;
                throw OpennessRefusals.Exception(code!, operation, credentialsGiven, native);
            }
        }

        internal static void Run(string operation, bool credentialsGiven, Action call)
            => Run(operation, credentialsGiven, () => { call(); return true; });
    }
}
