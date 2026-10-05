using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// Reads the UI's own source, which the test project embeds.
///
/// Scanning the real markup is the point: a test that kept its own list of keys and resource
/// names would pass forever while the window drifted away from it. Embedding rather than
/// walking up to a source directory keeps it working from whatever folder the test host runs in.
/// </summary>
internal static class SourceScan
{
    private static readonly Lazy<IReadOnlyList<SourceFile>> Files = new(Load);

    /// <summary>Every embedded .xaml file, name and text.</summary>
    public static IEnumerable<SourceFile> Markup
        => Files.Value.Where(f => f.Name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every embedded .cs file, name and text.</summary>
    public static IEnumerable<SourceFile> Code
        => Files.Value.Where(f => f.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<SourceFile> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var files = new List<SourceFile>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;

            using var reader = new StreamReader(stream);
            files.Add(new SourceFile(name, reader.ReadToEnd()));
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "No UI source was embedded. Check the EmbeddedResource globs in TiaOpenness.Gui.Tests.csproj - " +
                "without them these tests would pass by scanning nothing.");
        }

        return files;
    }
}

internal sealed record SourceFile(string Name, string Text);
