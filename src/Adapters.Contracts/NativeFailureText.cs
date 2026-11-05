using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TiaMcp.Adapters.Contracts;

public static class NativeFailureText
{
    public static string Compose(string? message, string? detailText, IEnumerable<string?> detailTexts)
        => string.Join("; ", new[] { message, detailText }.Concat(detailTexts)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => Redact(text!))
            .Select(text => string.Join(" ", text.Split(new[] { '\r', '\n', '\u0085', '\u2028', '\u2029' }, StringSplitOptions.RemoveEmptyEntries)
                .TakeWhile(line => !Regex.IsMatch(line, @"^\s*at\s+(?:[^\s(]+\.){2}[^\s(]+\(",
                        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                    && !line.TrimStart().StartsWith("--- End of", StringComparison.Ordinal))).Trim())
            .Where(text => text.Length != 0)
            .Distinct(StringComparer.Ordinal));

    private static string Redact(string text)
    {
        var timeout = TimeSpan.FromSeconds(1);
        var prefix = Regex.Match(text, @"^(?:[A-Za-z_][\w.`]*Exception|KeyNotFoundError):\s",
            RegexOptions.CultureInvariant, timeout);
        string value = text.Substring(prefix.Length);
        value = Regex.Replace(value, @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----", "••••",
            RegexOptions.CultureInvariant, timeout);
        const string pairs = @"(?i)\b([\w.-]*(?:password|passwd|pwd|secret|token|key|credential)[\w.-]*\s*[=:]\s*)([^\s&;,""'<>]+)";
        const string authorization = @"(?i)\b(?:Bearer|Basic)\s+([^\s""'<>\\,;]+)";
        var secrets = Regex.Matches(value, pairs, RegexOptions.CultureInvariant, timeout).Cast<Match>()
            .Select(match => match.Groups[2].Value)
            .Concat(Regex.Matches(value, authorization, RegexOptions.CultureInvariant, timeout).Cast<Match>().Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal).OrderByDescending(secret => secret.Length).ToArray();
        foreach (string secret in secrets) value = value.Replace(secret, "••••");
        value = Regex.Replace(value, @"(?i)(https?://)[^/\s@]+@", "$1••••@", RegexOptions.CultureInvariant, timeout);
        return prefix.Value + value;
    }
}
