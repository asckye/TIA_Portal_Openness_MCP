using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal static class PythonCheckChanges
{
    private sealed record Test(string Name, string Body, string Skips, int Line, bool Nested = false)
    {
        internal string ScopedBody { get; init; } = "";
    }
    private static List<Test> Tests(string source, bool allFunctions = false)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var tests = new List<Test>();
        // Zero denotes module scope, -1 an untracked scope, and positive values a function's declaration line.
        var owners = new int[lines.Length];
        var scopes = new List<(int Indent, string Name, bool Function)>();
        for (int index = 0; index < lines.Length; index++)
        {
            var declaration = Regex.Match(lines[index], @"^(?<indent>\s*)(?:async\s+)?(?<kind>def|class)\s+(?<name>\w+)");
            if (!declaration.Success) continue;
            int indent = declaration.Groups["indent"].Length;
            scopes.RemoveAll(scope => scope.Indent >= indent);
            string name = declaration.Groups["name"].Value;
            bool nested = scopes.Any(scope => scope.Function);
            bool function = declaration.Groups["kind"].Value == "def";
            scopes.Add((indent, name, function));
            // Retain the original full body for rename matching and existing test-removal approval keys.
            int end = index + 1;
            while (end < lines.Length && (string.IsNullOrWhiteSpace(lines[end]) || lines[end].TakeWhile(char.IsWhiteSpace).Count() > indent)) end++;
            // A comment can be dedented anywhere in a Python suite without ending it.
            int scopeEnd = end;
            while (scopeEnd < lines.Length && (string.IsNullOrWhiteSpace(lines[scopeEnd]) || lines[scopeEnd].TrimStart().StartsWith('#')
                || lines[scopeEnd].TakeWhile(char.IsWhiteSpace).Count() > indent)) scopeEnd++;
            int parent = owners[index];
            bool tracked = function && (allFunctions || name.StartsWith("test_", StringComparison.Ordinal) || parent > 0);
            Array.Fill(owners, tracked ? index + 1 : parent > 0 ? parent : -1, index, scopeEnd - index);
            if (!tracked) continue;
            int start = index;
            while (start > 0 && lines[start - 1].TrimStart().StartsWith('@')) start--;
            string decorators = string.Join('\n', lines[start..index]);
            var skips = Regex.Matches(decorators, @"@(?:pytest\.mark\.(?:skip|skipif)|unittest\.skip\w*)\b[^\n]*").Select(match => match.Value.Trim());
            string body = string.Join('\n', lines[index..end]);
            var test = new Test(string.Join('/', scopes.Select(scope => scope.Name)), body, string.Join('\n', skips), index + 1, nested);
            tests.Add(test);
        }
        var scopedLines = Enumerable.Range(0, lines.Length).ToLookup(index => owners[index], index => lines[index]);
        for (int i = 0; i < tests.Count; i++)
        {
            var test = tests[i];
            string body = string.Join('\n', scopedLines[test.Line]);
            var calls = Regex.Matches(body, @"\bpytest\.skip\s*\([^\r\n]*").Select(match => match.Value);
            tests[i] = test with { ScopedBody = body, Skips = string.Join('\n', test.Skips.Split('\n', StringSplitOptions.RemoveEmptyEntries).Concat(calls)) };
        }
        var module = string.Join('\n', lines.Select((line, index) => owners[index] == 0 ? line : ""));
        tests.Add(new("<module>", module, "", 1) { ScopedBody = module });
        return tests;
    }

    internal static void Scan(IEnumerable<CheckChange> changes, List<ReviewFinding> findings)
    {
        var rows = changes.ToArray();
        var after = rows
            .SelectMany(change => Tests(change.After ?? "", change.Path.StartsWith("scripts/checks/", StringComparison.Ordinal))
                .Select(test => (Change: change, Test: test)))
            .ToList();
        foreach (var change in rows)
        foreach (var test in Tests(change.Before ?? "", change.OldPath.StartsWith("scripts/checks/", StringComparison.Ordinal)))
        {
            int match = after.FindIndex(candidate => candidate.Test.Nested == test.Nested && candidate.Test.Name == test.Name);
            string Body(string text) => Regex.Replace(text, @"^(\s*(?:async\s+)?def)\s+\w+", "$1 _");
            if (match < 0) match = after.FindIndex(candidate => candidate.Test.Nested == test.Nested && Body(candidate.Test.Body) == Body(test.Body));
            if (match < 0 && test.Name.Split('/').Last().StartsWith("test_", StringComparison.Ordinal))
            {
                if (change.After != null)
                    findings.Add(new(change.OldPath, "test-removed", test.Name + ":" + CheckLoosening.Hash(test.Body),
                        "Python test removed or renamed", test.Line));
                continue;
            }
            var replacement = match < 0 ? (Change: change, Test: new Test(test.Name, "", "", test.Line)) : after[match];
            if (match >= 0) after.RemoveAt(match);
            AddSkip(replacement.Change, replacement.Test, test.Skips, findings);
            if (change.After == null) continue;
            var available = Assertions(replacement.Test.ScopedBody).SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value)).ToList();
            foreach (var (assertion, count) in Assertions(test.ScopedBody))
                for (int index = 0; index < count; index++)
                {
                    int found = available.FindIndex(current => CheckLoosening.StricterCondition(assertion, current));
                    if (found >= 0) { available.RemoveAt(found); continue; }
                    findings.Add(new(change.OldPath, "assertion-removed", test.Name + ":" + CheckLoosening.Hash(assertion) + ":" + (index + 1),
                        "Removed Python assertion: " + assertion, test.Line));
                }
        }
        foreach (var added in after) AddSkip(added.Change, added.Test, "", findings);
        foreach (var change in rows.Where(change => change.After != null))
        {
            var oldMarkers =
                UnscopedSkips(change.Before ?? "", Tests(change.Before ?? "", change.OldPath.StartsWith("scripts/checks/", StringComparison.Ordinal)));
            foreach (var (marker, count) in UnscopedSkips(change.After!,
                        Tests(change.After!, change.Path.StartsWith("scripts/checks/", StringComparison.Ordinal))))
                for (int index = oldMarkers.GetValueOrDefault(marker); index < count; index++)
                    findings.Add(new(change.Path, "test-skipped", "python-marker:" + CheckLoosening.Hash(marker) + ":" + (index + 1),
                        "Added class/module Python skip: " + marker));
        }
    }

    private static void AddSkip(CheckChange change, Test test, string previous, List<ReviewFinding> findings)
    {
        if (test.Skips.Length > 0 && previous != test.Skips)
            findings.Add(new(change.Path, "test-skipped", test.Name + ":" + CheckLoosening.Hash(test.Skips), "Added Python skip: " + test.Skips, test.Line));
    }

    private static Dictionary<string, int> UnscopedSkips(string source, IEnumerable<Test> tests)
    {
        var scoped = tests.SelectMany(test => test.Skips.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Select(line => line.Trim())
            .GroupBy(line => line).ToDictionary(group => group.Key, group => group.Count());
        var result = new List<string>();
        foreach (Match match in Regex.Matches(source, @"(?m)^[ \t]*(?:@|pytestmark\s*=\s*)(?:pytest\.mark\.(?:skip|skipif)|unittest\.skip\w*)\b[^\r\n]*"))
        {
            string marker = match.Value.Trim();
            if (scoped.GetValueOrDefault(marker) > 0) scoped[marker]--;
            else result.Add(marker);
        }
        return result.GroupBy(marker => marker).ToDictionary(group => group.Key, group => group.Count());
    }

    internal static void Limits(CheckChange change, List<ReviewFinding> findings)
    {
        IEnumerable<(string Key, double Value, bool Minimum)> Read(string source)
        {
            var names = new Dictionary<string, int>();
            string owner = "";
            foreach (string line in source.Split('\n'))
            {
                var function = Regex.Match(line, @"^\s*(?:async\s+)?def\s+(?<name>\w+)");
                if (function.Success) owner = function.Groups["name"].Value;
                else if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#')) owner = "";
                const string number = @"(?<number>\d+(?:\.\d+)?)";
                const string named = @"(?<name>\b(?:\w*timeout\w*|max(?:imum)?_\w+|min(?:imum)?_\w+|" +
                    @"\w*(?:threshold|margin|budget)\w*))\s*=\s*";
                var matches = Regex.Matches(line, named + number, RegexOptions.IgnoreCase).Cast<Match>().ToList();
                var argument = Regex.Match(line, "add_argument\\(.*?['\"](?<name>--[^'\"]+)['\"].*?default\\s*=\\s*" + number);
                if (argument.Success) matches.Add(argument);
                foreach (var match in matches)
                {
                    string name = match.Groups["name"].Value;
                    if (!Regex.IsMatch(name, "timeout|growth|minimum|maximum|(?:^|--)min[_-]|(?:^|--)max[_-]|threshold|margin|budget",
                        RegexOptions.IgnoreCase)) continue;
                    string key = owner + ":" + name;
                    int index = names.GetValueOrDefault(key);
                    names[key] = index + 1;
                    yield return (key + ":" + index, double.Parse(match.Groups["number"].Value,
                        System.Globalization.CultureInfo.InvariantCulture), name.Contains("minimum", StringComparison.OrdinalIgnoreCase) ||
                        Regex.IsMatch(name, @"^(?:--)?min[_-]", RegexOptions.IgnoreCase));
                }
            }
        }
        var after = Read(change.After!).ToDictionary(row => row.Key);
        foreach (var previous in Read(change.Before!))
            if (after.TryGetValue(previous.Key, out var current) &&
                (previous.Minimum ? current.Value < previous.Value : current.Value > previous.Value))
                findings.Add(new(change.Path, previous.Minimum ? "limit-lowered" : "limit-raised",
                    "python-limit:" + previous.Key + ":" + previous.Value + "->" + current.Value, "Python check limit weakened"));
    }

    private static Dictionary<string, int> Assertions(string source) => Regex
        .Matches(source, @"(?m)^\s*(?:assert\s+[^\r\n]+|(?:require|check)\s*\([^\r\n]*|(?:with\s+)?self\.assert\w+\s*\([^\r\n]*)")
        .Select(match => Regex.Replace(match.Value.Trim(), @"\s+", " ")).GroupBy(value => value)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}
