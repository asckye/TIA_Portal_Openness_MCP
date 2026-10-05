using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Hmi
{
    internal static class HmiRules
    {
        internal static readonly string[] AmlRoles = { "Rack", "DeviceItem", "CommunicationInterface", "CommunicationPort" };
        internal static readonly string[] IoProperties = { "BackColor", "ForeColor", "BorderColor", "BorderWidth", "Visible", "Enabled", "Name" };
        internal static readonly string[] RectangleExcluded = { "ForeColor", "Text", "Font", "Content", "Padding" };
        internal static readonly string[] PaletteColors = { "Page", "Background", "Surface", "Text", "Border", "page", "background", "surface", "text", "border" };

        internal static void UniqueNames(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names) V4Validation.Require(seen.Add(name), "Duplicate Classic name: " + name);
        }

        internal static void ClassicScreen(ClassicScreenSpec spec)
        {
            UniqueNames(spec.Items.Select(i => i.Name));
            foreach (var item in spec.Items)
            {
                // Use Int64 for the comparison so Int32 overflow cannot bypass the bounds.
                V4Validation.Require((long)(item.Left ?? 0) + (item.Width ?? 120) <= (spec.Screen?.Width ?? 640)
                    && (long)(item.Top ?? 0) + (item.Height ?? 40) <= (spec.Screen?.Height ?? 480),
                    "Classic item is outside the screen: " + item.Name);
            }
        }

        internal static void ClassicColor(string? color)
        {
            // ClassicHmiScreenXmlBuilder.cs:391-402 passes RGB strings through and
            // converts only 0xRRGGBB / 0xAARRGGBB. Do not impose Unified's ARGB grammar.
            if (string.IsNullOrWhiteSpace(color)) return;
            color = color!.Trim();
            if (!color.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return;
            var hex = color.Substring(color.Length == 10 ? 4 : 2);
            if (hex.Length != 6) return;
            for (int i = 0; i < 6; i += 2) Convert.ToInt32(hex.Substring(i, 2), 16);
        }

        internal static void Palette(IReadOnlyDictionary<string, string> palette)
        {
            TextMap(palette);
            // The current builder interprets these five palette entries; extension
            // entries are retained verbatim, including their insertion order.
            foreach (string key in PaletteColors.Take(5))
            {
                string camel = char.ToLowerInvariant(key[0]) + key.Substring(1);
                if (palette.TryGetValue(key, out var color) || palette.TryGetValue(camel, out color))
                    V4Validation.Require(Regex.IsMatch(color, "^0x[0-9a-fA-F]{8}$"), "Invalid TIA ARGB color: " + key);
            }
        }

        internal static void Finite(params double?[] values)
        {
            foreach (var value in values)
                V4Validation.Require(!value.HasValue || !double.IsNaN(value.Value) && !double.IsInfinity(value.Value),
                    "Geometry must be finite.");
        }

        internal static void TextMap(IReadOnlyDictionary<string, string> values) => InputGuard.Require(values.Values.All(value => value != null));

        internal static void Properties(IReadOnlyDictionary<string, Scalar>? properties)
        {
            if (properties == null) return;
            foreach (var pair in properties)
            {
                V4Validation.Text(pair.Key, "property name");
                V4Validation.Json(pair.Value.Json);
            }
        }

        internal static void UnifiedProperties(string type, IReadOnlyDictionary<string, Scalar>? properties)
        {
            Properties(properties);
            if (properties == null) return;
            foreach (string key in properties.Keys)
            {
                if (type == "IOField") V4Validation.Require(IoProperties.Contains(key, StringComparer.Ordinal),
                    "Property is outside the stable IOField set: " + key);
                if (type == "Rectangle") V4Validation.Require(!RectangleExcluded.Contains(key.Trim(), StringComparer.OrdinalIgnoreCase),
                    "Property is unsupported on Rectangle: " + key);
            }
        }

        internal static void TextRequest(string? text, string? culture, string? property)
        {
            if (property != null) V4Validation.Require(new[] { "Text", "AlternateText", "ToolTipText" }.Contains(property),
                "Unknown Unified textProperty.");
            if (text != null) CultureInfo.GetCultureInfo(culture ?? "zh-CN");
        }

        internal static void AmlBudget(JsonElement input) => AmlBudget(
            input.GetProperty("devices").EnumerateArray().SelectMany(device => device.GetProperty("deviceItems").EnumerateArray()),
            item => item.TryGetProperty("deviceItems", out var children) ? children.EnumerateArray().ToArray() : Array.Empty<JsonElement>());

        internal static void AmlBudget(IEnumerable<DeviceItemSpec> items)
        {
            try { AmlBudget(items, item => item.DeviceItems ?? Array.Empty<DeviceItemSpec>()); }
            catch (InputRejection rejection)
            {
                // Preserve the existing constructor/Deserialize exception contract.
                // InputContract runs the same guard before constructing these DTOs.
                throw new ArgumentException("AML device item count or depth exceeds its budget.", rejection);
            }
        }

        private static void AmlBudget<T>(IEnumerable<T> items, Func<T, IEnumerable<T>> children)
        {
            int count = 0;
            var pending = new Stack<(T Item, int Depth)>(items.Select(item => (item, 0)));
            while (pending.Count != 0)
            {
                var entry = pending.Pop();
                InputGuard.Limit(++count, 5000);
                InputGuard.Limit(entry.Depth, 8);
                foreach (var child in children(entry.Item)) pending.Push((child, entry.Depth + 1));
            }
        }

        internal static void NetworkAddress(string? address)
        {
            if (address == null) return;
            address = address.Trim();
            V4Validation.Require(address.Length == 0 || IPAddress.TryParse(address, out _) || address.All(char.IsDigit),
                "Invalid networkAddress.");
        }
    }
}
