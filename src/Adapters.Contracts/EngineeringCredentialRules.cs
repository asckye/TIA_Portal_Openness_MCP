using System.Security;

namespace TiaMcp.Adapters.Contracts;

public static class EngineeringCredentialRules
{
    public static SecureString ToSecureString(string password)
    {
        var secure = new SecureString();
        foreach (char character in password) secure.AppendChar(character);
        secure.MakeReadOnly();
        return secure;
    }
}
