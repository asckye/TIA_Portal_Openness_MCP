using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace TiaOpenness.Tests;

internal static class V4Fixtures
{
    internal static IEnumerable<JsonElement> Supplemental()
    {
        var assembly = typeof(V4Fixtures).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("V4Supplemental.json")))!;
        using var document = JsonDocument.Parse(stream);
        foreach (var envelope in document.RootElement.EnumerateArray()) yield return envelope.Clone();
    }
}
