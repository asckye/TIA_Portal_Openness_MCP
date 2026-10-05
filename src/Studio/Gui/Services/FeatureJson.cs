namespace TiaOpenness.Gui.Services;

/// <summary>Display and clipboard share the producer privacy boundary.</summary>
public static class FeatureJson
{
    public static string Redact(string json) => TiaOpenness.Shared.CallJournalPayload.Redact(json);
}
