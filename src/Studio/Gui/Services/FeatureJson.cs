using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaOpenness.Gui.Services;

/// <summary>Display and clipboard share the producer privacy boundary.</summary>
public static class FeatureJson
{
    public static string Redact(string json) => TiaOpenness.Shared.CallJournalPayload.Redact(json);

    internal static JsonNode? ApprovalParameters(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            MaskCredentials(node);
            return node;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Malformed or duplicate fields cannot be displayed safely.
            return null;
        }
    }

    private static void MaskCredentials(JsonNode? node)
    {
        if (node is JsonArray items)
            foreach (var item in items) MaskCredentials(item);
        if (node is not JsonObject fields) return;
        foreach (var field in fields.ToArray())
        {
            if (IsCredential(field.Key)) fields[field.Key] = "••••";
            else MaskCredentials(field.Value);
        }
    }

    private static bool IsCredential(string name)
    {
        if (DeclaredCredential(name.Replace("_", "").Replace("-", "").ToLowerInvariant())) return true;
        if (name.Length == 0 || !char.IsAsciiLetterLower(name[0]) || !name.All(char.IsAsciiLetterOrDigit)) return false;
        // Only the final camelCase word can be a credential suffix.
        for (int i = name.Length - 1; i > 0; i--)
            if (char.IsAsciiLetterUpper(name[i])) return DeclaredCredential(name[i..].ToLowerInvariant());
        return false;
    }

    private static bool DeclaredCredential(string name) => name is
        "key" or "keys" or "privatekey" or "protectedkey" or "apikey" or "xapikey" or "token" or "accesstoken" or "refreshtoken"
        or "password" or "passwd" or "pwd" or "passphrase" or "secret" or "clientsecret"
        or "credential" or "credentials" or "authorization" or "proxyauthorization" or "cookie"
        or "moduleaccesspassword" or "blockbindingpassword" or "mastersecretpassword" or "umacpassword"
        or "currentpassword" or "newpassword" or "serverpassword";
}
