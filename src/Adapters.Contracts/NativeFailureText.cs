using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcp.Adapters.Contracts;

public static class NativeFailureText
{
    public static string BoundedDiagnostic(Exception error)
    {
        var message = Compose(error.Message, null, Array.Empty<string>());
        return BoundedLines(new[] { message.Length > 0 ? message : error.GetType().Name }, WithoutLocalPaths)[0];
    }

    public static string Compose(string? message, string? detailText, IEnumerable<string?> detailTexts)
        => string.Join("; ", new[] { message, detailText }.Concat(detailTexts)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => Redact(text!))
            .Select(text => string.Join(" ", text.Split(new[] { '\r', '\n', '\u0085', '\u2028', '\u2029' }, StringSplitOptions.RemoveEmptyEntries)
                .TakeWhile(line => !IsStackBoundary(line))).Trim())
            .Where(text => text.Length != 0)
            .Distinct(StringComparer.Ordinal));

    public static bool IsStackBoundary(string line) => line.TrimStart().StartsWith("--- End of", StringComparison.Ordinal)
        || Regex.IsMatch(line, @"^\s*at\s+(?:[^\s(]+\.){2}[^\s(]+\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string WithoutLocalPaths(string text)
    {
        const string uri = @"\b(?:(?<web>https?)://|file:/+)[^\s""<>]+";
        const string windowsPath = @"(?:(?<![\w/])[a-z]:[\\/]|\\\\)(?:(?!\s+https?://)[^\r\n""<>|;])*";
        return Regex.Replace(text, uri + "|" + windowsPath, match => match.Groups["web"].Success ? match.Value : "<path>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    public static List<string> BoundedLines(IEnumerable<string> lines, Func<string, string> sanitize)
    {
        var result = new List<string>();
        int remaining = 4096;
        foreach (string line in lines.Take(40))
        {
            string text = sanitize(line);
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > remaining)
            {
                while (remaining > 0 && (bytes[remaining] & 0xc0) == 0x80) remaining--;
                result.Add(Encoding.UTF8.GetString(bytes, 0, remaining));
                break;
            }
            result.Add(text);
            remaining -= bytes.Length + 1;
            if (remaining <= 0) break;
        }
        return result;
    }

    public static string BoundedMessage(string message, Func<string, string?> sanitizeLine)
    {
        // A quoted credential or private key can span several compiler lines. Mask it before splitting.
        IEnumerable<string> Lines()
        {
            foreach (string raw in Redact(message).Split(new[] { '\r', '\n', '\u0085', '\u2028', '\u2029' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (IsStackBoundary(line) || line.Contains("-----BEGIN")) yield break;
                if (line.Length == 0 || sanitizeLine(line) is not string safe) continue;
                yield return safe;
                // A masked structured fragment may continue on subsequent lines.
                if (safe == "••••" || safe.EndsWith("••••", StringComparison.Ordinal) && (line.Contains("{") || line.IndexOf('[', 1) >= 0)) yield break;
            }
        }
        try { return string.Join(" ", BoundedLines(Lines(), WithoutLocalPaths)); }
        catch (RegexMatchTimeoutException) { /* swallow(privacy): use the host's generic failure if sanitizing the diagnostic times out */ return ""; }
    }

    private static string Redact(string text)
    {
        var timeout = TimeSpan.FromSeconds(1);
        var prefix = Regex.Match(text, @"^(?:[A-Za-z_][\w.`]*Exception|KeyNotFoundError):\s",
            RegexOptions.CultureInvariant, timeout);
        string value = text.Substring(prefix.Length);
        value = Regex.Replace(value, @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----", "••••",
            RegexOptions.CultureInvariant, timeout);
        const string pairs = @"(?i)\b([\w.-]*(?:password|passwd|pwd|secret|token|key|credential)[\w.-]*\s*(?::=|[=:])\s*)"
            + @"(?:""([^""]*)""?|'([^']*)'?|([^\s&;,""'<>]+))";
        const string authorization = @"(?i)\b(?:Bearer|Basic)\s+([^\s""'<>\\,;]+)";
        var secrets = Regex.Matches(value, pairs, RegexOptions.CultureInvariant, timeout).Cast<Match>()
            .SelectMany(match => match.Groups.Cast<Group>().Skip(2).Where(group => group.Success && group.Length != 0).Select(group => group.Value))
            .Concat(Regex.Matches(value, authorization, RegexOptions.CultureInvariant, timeout).Cast<Match>().Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal).OrderByDescending(secret => secret.Length).ToArray();
        if (secrets.Length > 0)
        {
            // The assignment and authorization patterns delimit each captured value. A single scalar may also occur inside a compiler symbol.
            static string Pattern(string secret)
            {
                string literal = Regex.Escape(secret);
                bool single = secret.Length == 1 || secret.Length == 2 && char.IsSurrogatePair(secret, 0);
                return single ? @"(?<![\p{L}\p{N}_])" + literal + @"(?![\p{L}\p{N}_])" : literal;
            }
            value = Regex.Replace(value, string.Join("|", secrets.Select(Pattern)), "••••", RegexOptions.CultureInvariant, timeout);
        }
        value = Regex.Replace(value, @"(?i)(https?://)[^/\s@]+@", "$1••••@", RegexOptions.CultureInvariant, timeout);
        return prefix.Value + value;
    }
}
