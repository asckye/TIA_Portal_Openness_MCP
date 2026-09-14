using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // No Siemens dependency: tests exercise the same reflection path as the live server.
    internal static class UnifiedMultilingualText
    {
        internal static bool ReadRequest(JsonObject item, out string text)
        {
            text = string.Empty;
            if (!item.TryGetPropertyValue("text", out var node)) return false;
            if (node is not JsonValue value || !value.TryGetValue<string>(out var input))
                throw new InvalidOperationException("text must be a string; omit to preserve, use \"\" to clear; null is invalid.");
            text = input;
            return true;
        }

        private static object? Get(object value, string property) =>
            value.GetType().GetProperty(property)?.GetValue(value);

        private static List<(object Item, string Culture, string Text)> Snapshot(object multilingual)
        {
            if (Get(multilingual, "Items") is not IEnumerable items)
                throw new InvalidOperationException("MultilingualText.Items is unavailable.");
            var result = new List<(object, string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item == null) throw new InvalidOperationException("Null multilingual item.");
                var language = Get(item, "Language");
                var culture = language == null ? null : Get(language, "Culture");
                var name = culture == null ? null : Get(culture, "Name") as string;
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("Multilingual item has no Language.Culture.Name.");
                if (!seen.Add(name!)) throw new InvalidOperationException("Duplicate language: " + name);
                if (Get(item, "Text") is not string text)
                    throw new InvalidOperationException("Text is not readable for " + name);
                result.Add((item, name!, text));
            }
            return result;
        }

        internal static JsonArray Read(object multilingual)
        {
            var result = new JsonArray();
            foreach (var row in Snapshot(multilingual))
                result.Add(new JsonObject { ["culture"] = row.Culture, ["text"] = row.Text });
            return result;
        }

        internal static JsonArray Write(object item, string property, string text, string culture)
        {
            var result = WriteDetailed(item, property, text, culture);
            if (!result["verified"]!.GetValue<bool>())
                throw new InvalidOperationException(property + " culture=" + culture + ": " + result["error"]);
            return (JsonArray)result["after"]!.DeepClone();
        }

        internal static void Validate(object item, string property, string culture)
        {
            var multilingual = Get(item, property) ?? throw new InvalidOperationException(property + " is unavailable.");
            var rows = Snapshot(multilingual);
            if (string.IsNullOrWhiteSpace(culture) || !rows.Any(x => string.Equals(x.Culture, culture, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(property + " target language not found: " + culture + "; available cultures: " + string.Join(", ", rows.Select(x => x.Culture)));
        }

        internal static JsonObject WriteDetailed(object item, string property, string text, string culture)
        {
            var result = new JsonObject {
                ["property"] = property, ["culture"] = culture, ["requestedText"] = text,
                ["verified"] = false, ["setterInvoked"] = false, ["setterCompleted"] = false,
                ["mayHaveChanged"] = false, ["state"] = "NotWritten", ["targetMatched"] = null,
                ["nonTargetUnchanged"] = null, ["actualRawText"] = null,
                ["verificationMode"] = text == "" ? "ExactOrObservedEmptyHtml" : "ExactRawText" };
            try
            {
                Validate(item, property, culture);
                var before = Snapshot(Get(item, property)!);
                result["before"] = Rows(before);
                var target = before.Single(x => string.Equals(x.Culture, culture, StringComparison.OrdinalIgnoreCase));
                var expected = text.Length == 0 || text.TrimStart().StartsWith("<body", StringComparison.OrdinalIgnoreCase)
                    ? text : "<body><p>" + SecurityElement.Escape(text) + "</p></body>";
                result["expectedRawText"] = expected;
                var textProperty = target.Item.GetType().GetProperty("Text");
                if (textProperty?.CanWrite != true) throw new InvalidOperationException("Target Text is not writable.");
                result["setterInvoked"] = true;
                result["mayHaveChanged"] = true;
                result["state"] = "Unknown";
                string? setterError = null;
                try { textProperty.SetValue(target.Item, expected); result["setterCompleted"] = true; }
                catch (Exception ex) { setterError = (ex.InnerException ?? ex).Message; }
                // Read even when a setter throws: it may already have changed the object.
                var after = Snapshot(Get(item, property) ?? throw new InvalidOperationException("Readback unavailable."));
                result["after"] = Rows(after);
                result["state"] = "ReadBack";
                var actual = after.SingleOrDefault(x => string.Equals(x.Culture, culture, StringComparison.OrdinalIgnoreCase)).Text;
                result["actualRawText"] = actual;
                // Only the exact form observed in live TIA is accepted for an explicit empty request.
                // Whitespace, nbsp, br and arbitrary tag stripping are deliberately excluded.
                var matched = actual == expected || (text == "" && actual == "<body><p/></body>");
                var unchanged = after.Count == before.Count && before.All(old => after.Any(current =>
                    string.Equals(current.Culture, old.Culture, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(old.Culture, culture, StringComparison.OrdinalIgnoreCase) || current.Text == old.Text)));
                result["targetMatched"] = matched;
                result["nonTargetUnchanged"] = unchanged;
                result["mayHaveChanged"] = after.Count != before.Count || before.Any(old => !after.Any(current =>
                    string.Equals(current.Culture, old.Culture, StringComparison.OrdinalIgnoreCase) && current.Text == old.Text));
                result["verified"] = setterError == null && matched && unchanged;
                if (setterError != null || !matched || !unchanged)
                    result["error"] = setterError ?? "Text readback mismatch or non-target language changed; inspect before retrying.";
            }
            catch (Exception ex) { result["error"] = (ex.InnerException ?? ex).Message; }
            return result;
        }

        private static JsonArray Rows(List<(object Item, string Culture, string Text)> rows)
        {
            var result = new JsonArray();
            foreach (var row in rows) result.Add(new JsonObject { ["culture"] = row.Culture, ["text"] = row.Text });
            return result;
        }
    }
}
