using System.Xml.Linq;
using Microsoft.Build.Framework;

namespace TiaMcp.ReleaseTool;

internal static class CheckNodeProperties
{
    internal static Dictionary<string, string> Globals(IBuildEngine engine) => ((IBuildEngine6)engine).GetGlobalProperties()
        .Where(pair => !pair.Key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase) && pair.Key != "CheckAllNodeId")
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    internal static string Hash(string project, IReadOnlyDictionary<string, string> properties)
    {
        // WPF's temporary project shares the parent's variant identity, with
        // a separate wpf-temp intermediate directory below it.
        var name = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(project), "_[^_]+_wpftmp$", "");
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, name + Path.GetExtension(project)).ToLowerInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path + "\n" +
            string.Join("\n", properties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key.ToLowerInvariant() + "=" + pair.Value)))));
    }
    internal static void Apply(Dictionary<string, string> properties, string text)
    {
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            properties[part[..equals]] = Microsoft.Build.Evaluation.ProjectCollection.Unescape(part[(equals + 1)..]);
        }
    }
}

// Assign the ID before evaluating each SDK node. Hashing evaluated $(Property)
// values loses whether a property was global or merely a matching local default.
public sealed class AssignCheckBuildNodes : ITask
{
    public IBuildEngine BuildEngine { get; set; } = null!;
    public ITaskHost HostObject { get; set; } = null!;
    [Required] public ITaskItem[] Projects { get; set; } = [];
    public bool ReferenceSettings { get; set; }
    public bool Negotiation { get; set; }
    public string RemoveProperties { get; set; } = "";
    [Output] public ITaskItem[] Nodes { get; set; } = [];
    public bool Execute()
    {
        Nodes = Projects.Select(project =>
        {
            var properties = CheckNodeProperties.Globals(BuildEngine);
            var remove = RemoveProperties + ";" + project.GetMetadata("GlobalPropertiesToRemove") + ";" + project.GetMetadata("UndefineProperties");
            if (Negotiation) remove += ";TargetFramework;RuntimeIdentifier;SelfContained";
            foreach (var name in remove.Split(';', StringSplitOptions.RemoveEmptyEntries)) properties.Remove(name.Trim());
            if (ReferenceSettings)
                foreach (var name in new[] { "SetConfiguration", "SetPlatform", "SetTargetFramework" })
                    if (!Negotiation || name != "SetTargetFramework") CheckNodeProperties.Apply(properties, project.GetMetadata(name));
            var additional = project.GetMetadata("AdditionalProperties").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !part.StartsWith("CheckAllNodeId=", StringComparison.OrdinalIgnoreCase));
            CheckNodeProperties.Apply(properties, string.Join(";", additional));
            var item = new Microsoft.Build.Utilities.TaskItem(project);
            item.SetMetadata("AdditionalProperties", string.Join(";", additional.Append("CheckAllNodeId=" + CheckNodeProperties.Hash(project.GetMetadata("FullPath"), properties)[..24])));
            return (ITaskItem)item;
        }).ToArray();
        return true;
    }
}

public sealed class VerifyCheckBuildIdentity : ITask
{
    public IBuildEngine BuildEngine { get; set; } = null!;
    public ITaskHost HostObject { get; set; } = null!;
    [Required] public string Directory { get; set; } = "";
    [Required] public string Project { get; set; } = "";
    public string NodeId { get; set; } = "";
    public bool Execute()
    {
        var identity = CheckNodeProperties.Hash(Project, CheckNodeProperties.Globals(BuildEngine));
        if (NodeId.Length != 0 && NodeId != identity[..24])
        {
            BuildEngine.LogErrorEvent(new BuildErrorEventArgs("", "", "", 0, 0, 0, 0,
                "Planned node ID does not match actual global properties: " + Directory + "; expected " + identity[..24] + "; actual " + NodeId, "", nameof(VerifyCheckBuildIdentity)));
            return false;
        }
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, "check-global-properties.txt");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream); writer.Write(identity);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            // A simultaneous identical evaluation can still be flushing the tiny
            // receipt. Open after its writer closes, without accepting a mismatch.
            for (int attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    if (File.ReadAllText(path) == identity) return true;
                    if (new FileInfo(path).Length == 0) { Thread.Sleep(10); continue; }
                    break;
                }
                catch (IOException) { Thread.Sleep(10); }
            }
            BuildEngine.LogErrorEvent(new BuildErrorEventArgs("", "", "", 0, 0, 0, 0,
                "Different global-property sets share an intermediate directory: " + Directory, "", nameof(VerifyCheckBuildIdentity)));
            return false;
        }
    }
}

// The SDK reference-preparation targets call this on every actual build node,
// including target-time AdditionalProperties and framework negotiation.
public sealed class CollectCheckBuildNode : ITask
{
    public IBuildEngine BuildEngine { get; set; } = null!;
    public ITaskHost HostObject { get; set; } = null!;
    [Required] public string Project { get; set; } = "";
    [Output] public ITaskItem Node { get; set; } = null!;
    public bool Execute()
    {
        Node = new Microsoft.Build.Utilities.TaskItem(Project);
        var properties = ((IBuildEngine6)BuildEngine).GetGlobalProperties().Where(pair => !pair.Key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase));
        Node.SetMetadata("AdditionalProperties", string.Join(";", properties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + Microsoft.Build.Evaluation.ProjectCollection.Escape(pair.Value))));
        return true;
    }
}

// Evaluation needs the identity before the SDK imports NuGet assets. Write the
// discovered nodes, then reevaluate in a new MSBuild invocation. Restore and
// Build therefore use the same complete global-property set and explicit ID.
public sealed class DiscoverCheckBuildProperties : ITask
{
    public IBuildEngine BuildEngine { get; set; } = null!;
    public ITaskHost HostObject { get; set; } = null!;
    [Required] public ITaskItem[] Projects { get; set; } = [];
    [Required] public string GraphFile { get; set; } = "";
    public bool Execute()
    {
        try
        {
            var nodes = Projects.DistinctBy(item => item.ItemSpec + "|" + item.GetMetadata("AdditionalProperties"))
                .OrderBy(item => item.ItemSpec, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.GetMetadata("AdditionalProperties"), StringComparer.Ordinal).ToArray();
            string[] PropertyNames(ITaskItem item) => item.GetMetadata("AdditionalProperties").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part[..part.IndexOf('=')]).ToArray();
            var names = nodes.SelectMany(PropertyNames)
                .Concat(new[] { "TargetFramework", "RuntimeIdentifier", "SelfContained", "Configuration", "Platform" })
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var items = nodes.Select(item => new XElement("CheckGraphProject", new XAttribute("Include", item.ItemSpec),
                new XElement("AdditionalProperties", item.GetMetadata("AdditionalProperties")),
                new XElement("GlobalPropertiesToRemove", string.Join(";", names.Except(PropertyNames(item), StringComparer.OrdinalIgnoreCase)))));
            new XDocument(new XElement("Project", new XElement("ItemGroup", items))).Save(GraphFile);
            return true;
        }
        catch (Exception ex)
        {
            BuildEngine.LogErrorEvent(new BuildErrorEventArgs("", "", "", 0, 0, 0, 0, ex.ToString(), "", nameof(DiscoverCheckBuildProperties)));
            return false;
        }
    }
}
