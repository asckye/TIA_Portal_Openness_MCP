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
        string normalized = name.Replace("_", "").Replace("-", "").ToLowerInvariant();
        if (DeclaredCredential(normalized)) return true;
        if (name.Length == 0 || !char.IsAsciiLetterLower(name[0]) || !name.All(char.IsAsciiLetterOrDigit)) return false;

        bool endsInKey = name.EndsWith("Key", StringComparison.Ordinal) || name.EndsWith("Keys", StringComparison.Ordinal);
        for (int i = name.Length - 1; i > 0; i--)
        {
            if (!char.IsAsciiLetterUpper(name[i])) continue;
            var suffix = normalized.AsSpan(i);
            if (!endsInKey) return DeclaredCredential(suffix);
            // A key suffix needs a credential qualifier such as Api, Access or Private.
            if (suffix is not ("key" or "keys") && DeclaredCredential(suffix)) return true;
        }
        return false;
    }

    private static bool DeclaredCredential(ReadOnlySpan<char> name) => name is
        "key" or "keys" or "accesskey" or "privatekey" or "protectedkey" or "apikey" or "xapikey" or "token" or "accesstoken" or "refreshtoken"
        or "apikeys" or "accesskeys" or "privatekeys"
        or "password" or "passwd" or "pwd" or "passphrase" or "secret" or "clientsecret"
        or "credential" or "credentials" or "authorization" or "proxyauthorization" or "cookie"
        or "moduleaccesspassword" or "blockbindingpassword" or "mastersecretpassword" or "umacpassword"
        or "currentpassword" or "newpassword" or "serverpassword";
}
