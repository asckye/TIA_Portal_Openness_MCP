using System;
using System.Collections.Generic;
using System.Security;

namespace TiaMcp.Adapters.Contracts;

public static partial class EngineeringCredentialRules
{
    public const string PasswordPolicyRefusal = "The password was refused by the configured password policy; nothing changed. Read ManagePasswordPolicy before choosing another password.";

    // Only wrap one password setter, before any other mutation in the operation.
    public static bool TrySetPassword(Action setter, IDictionary<string, object?> meta)
    {
        meta["mayHaveChanged"] = true;
        try { setter(); return true; }
        catch (Exception error) when (PasswordPolicyViolation(error))
        {
            meta["mayHaveChanged"] = false;
            meta["operationSuccess"] = false;
            meta["apiCallSuccess"] = false;
            meta["status"] = "PreconditionFailed";
            meta["precondition"] = "password-policy";
            meta["refusal"] = PasswordPolicyRefusal;
            return false;
        }
    }

    private static bool PasswordPolicyViolation(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current.GetType().FullName == "Siemens.Engineering.EngineeringPasswordPolicyViolationException") return true;
        return false;
    }

    // Serialize event callbacks with disposal: even a late callback never uses a disposed password.
    public sealed class AuthenticationScope : IDisposable
    {
        private readonly object gate = new object();
        private SecureString? password;
        public AuthenticationScope(string value) => password = ToSecureString(value);
        public void Use(Action<SecureString> callback)
        {
            lock (gate)
            {
                if (password == null) throw new InvalidOperationException("The UMC authentication operation has ended.");
                callback(password);
            }
        }
        public void Dispose()
        {
            lock (gate) { password?.Dispose(); password = null; }
        }
    }

    public static SecureString ToSecureString(string password)
    {
        var secure = new SecureString();
        foreach (char character in password) secure.AppendChar(character);
        secure.MakeReadOnly();
        return secure;
    }
}
