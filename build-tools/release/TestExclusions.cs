using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal static class TestExclusions
{
    internal static bool DisabledByFalse(string source, int position)
    {
        var stack = new List<(bool False, bool Else)>();
        foreach (var line in source[..Math.Min(position, source.Length)].Split('\n'))
        {
            var text = line.Split("//")[0].Trim();
            if (text.StartsWith("#if ", StringComparison.Ordinal))
                stack.Add((text[4..].Split("//")[0].Trim() is "false" or "0", false));
            else if (text == "#else" && stack.Count > 0)
                stack[^1] = (stack[^1].False, !stack[^1].Else);
            else if (text.StartsWith("#elif ", StringComparison.Ordinal) && stack.Count > 0)
                stack[^1] = (text[6..].Trim() is "false" or "0", false);
            else if (text == "#endif" && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
        }
        return stack.Any(item => item.False && !item.Else);
    }

    internal static void Scan(CheckChange change, List<ReviewFinding> findings)
    {
        if (Path.GetExtension(change.Path) is not (".csproj" or ".props" or ".targets" or ".runsettings") || change.After == null) return;
        bool testScope = change.Path.StartsWith("tests/", StringComparison.Ordinal) ||
            Path.GetFileName(change.Path).Contains("Test", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(change.Path) is "Directory.Build.props" or "Directory.Build.targets" ||
            change.Path.EndsWith(".runsettings", StringComparison.Ordinal);
        IEnumerable<string> Exclusions(string? source)
        {
            if (string.IsNullOrWhiteSpace(source)) return [];
            var xml = XDocument.Parse(source);
            return xml.Descendants().SelectMany(element =>
            {
                var name = element.Name.LocalName;
                if (name == "Compile")
                    return element.Attributes().Where(attribute => attribute.Name.LocalName is "Remove" or "Exclude")
                        .Where(attribute => testScope || attribute.Value.Contains("test", StringComparison.OrdinalIgnoreCase))
                        .Select(attribute => element.ToString(SaveOptions.DisableFormatting));
                if ((testScope || element.Value.Contains("test", StringComparison.OrdinalIgnoreCase)) &&
                    (name is "DefaultItemExcludes" or "DefaultExcludesInProjectFolder" ||
                     name == "EnableDefaultCompileItems" && element.Value.Trim() == "false"))
                    return [element.ToString(SaveOptions.DisableFormatting)];
                return Array.Empty<string>();
            }).Distinct(StringComparer.Ordinal);
        }
        string Filter(string? source) => string.IsNullOrWhiteSpace(source) ? "" : XDocument.Parse(source).Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "TestCaseFilter")?.Value.Trim() ?? "";
        string oldFilter = Filter(change.Before), newFilter = Filter(change.After);
        if (CheckLimitChanges.FilterNarrows(oldFilter, newFilter))
            findings.Add(new(change.Path, "test-excluded", "runsettings-filter:" + CheckLoosening.Hash(oldFilter + "->" + newFilter),
                "Runsettings test filter narrowed: " + newFilter));
        foreach (var exclusion in Exclusions(change.After).Except(Exclusions(change.Before)))
            findings.Add(new(change.Path, "test-excluded", CheckLoosening.Hash(exclusion), "Test compilation/selection exclusion added: " + exclusion));
        if (change.Before == null) return;
        string[] Includes(string source) => XDocument.Parse(source).Descendants().Where(element => element.Name.LocalName == "Compile")
            .Select(element => (string?)element.Attribute("Include")).Where(value => value != null).Cast<string>().ToArray();
        foreach (var removed in Includes(change.Before).Except(Includes(change.After)))
            if (change.Path.StartsWith("tests/", StringComparison.Ordinal) || removed.Contains("test", StringComparison.OrdinalIgnoreCase))
                findings.Add(new(change.Path, "test-excluded", "compile-include:" + CheckLoosening.Hash(removed), "Test Compile Include removed: " + removed));
    }
}
