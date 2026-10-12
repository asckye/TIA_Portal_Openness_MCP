using System.Text.RegularExpressions;

namespace TiaMcp.Logic.V4
{
    public static class DiagnosticVocabulary
    {
        public static string? Key(string? value)
        {
            if (value == null) return null;
            var text = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2");
            text = Regex.Replace(text, "([A-Z])([A-Z][a-z])", "$1-$2");
            text = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return text == "option-package-operation" ? "optional-package-operation" : text;
        }
    }
}
