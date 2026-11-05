using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Text.Json.Nodes;

namespace TiaMcp.ReleaseTool;

internal static class ReviewTestSources
{
    internal static Func<string, bool> Create(string root)
    {
        var projects = new List<(string Directory, bool Defaults, string[] Includes, string[] Removes)>();
        var directory = Path.Combine(root, "tests");
        var catalogPath = Path.Combine(directory, "test-suites.json");
        var catalog = File.Exists(catalogPath) ? JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject() : new JsonObject();
        foreach (string project in catalog.Select(pair => Path.GetFullPath(pair.Value!["project"]!.GetValue<string>(), root)).Distinct())
        {
            if (Path.GetRelativePath(root, project).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) continue;
            string folder = Path.GetDirectoryName(project)!;
            var documents = new List<(XDocument Xml, string Directory)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Read(string file)
            {
                if (!visited.Add(file)) return;
                var xml = XDocument.Load(file);
                string importedDirectory = Path.GetDirectoryName(file)! + Path.DirectorySeparatorChar;
                documents.Add((xml, importedDirectory));
                foreach (var import in xml.Descendants("Import").Attributes("Project"))
                {
                    string value = import.Value.Replace("$(MSBuildThisFileDirectory)", importedDirectory, StringComparison.Ordinal);
                    if (value.Contains("$(", StringComparison.Ordinal)) continue;
                    string path = Path.GetFullPath(value, importedDirectory);
                    if (File.Exists(path)) Read(path);
                }
            }
            Read(project);
            string[] Patterns(string attribute) => documents.SelectMany(document => document.Xml.Descendants("Compile")
                .Attributes(attribute).SelectMany(value => value.Value.Split(';')).Select(value => value
                    .Replace("$(MSBuildThisFileDirectory)", document.Directory, StringComparison.Ordinal)
                    .Replace("$(MSBuildProjectDirectory)", folder, StringComparison.Ordinal)))
                .Where(value => !value.Contains("$(", StringComparison.Ordinal))
                .Select(value => Path.GetRelativePath(root, Path.GetFullPath(value, folder)).Replace('\\', '/')).ToArray();
            projects.Add((Path.GetRelativePath(root, folder).Replace('\\', '/') + "/",
                !documents.SelectMany(document => document.Xml.Descendants("EnableDefaultCompileItems")).Any(value => value.Value == "false"),
                Patterns("Include"), Patterns("Remove").Concat(Patterns("Exclude")).ToArray()));
        }
        bool Matches(string path, string pattern) => Regex.IsMatch(path,
            "^" + Regex.Escape(pattern).Replace("\\*\\*/", "(?:.*/)?").Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return path => path.EndsWith(".py", StringComparison.Ordinal) && path.StartsWith("scripts/checks/", StringComparison.Ordinal) ||
            path.EndsWith(".cs", StringComparison.Ordinal) && !path.Split('/').Any(part => part is "bin" or "obj") &&
            projects.Any(project => (project.Defaults && path.StartsWith(project.Directory, StringComparison.OrdinalIgnoreCase) ||
                project.Includes.Any(pattern => Matches(path, pattern))) && !project.Removes.Any(pattern => Matches(path, pattern)));
    }
}
