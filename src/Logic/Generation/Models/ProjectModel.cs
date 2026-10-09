using System.Collections.Generic;
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    // The host adapts complete, paged read-tool results to this Siemens-free snapshot.
    // A fingerprint must come from readback or a verified generation record, never a name alone.
    public sealed class ProjectModel
    {
        public string ProjectIdentity { get; set; } = "";
        public bool Complete { get; set; } = true;
        public List<ProjectDevice> Devices { get; set; } = new List<ProjectDevice>();
        public List<ProjectBlock> Blocks { get; set; } = new List<ProjectBlock>();
        public List<ProjectType> Types { get; set; } = new List<ProjectType>();
        public List<ProjectExternalSource> ExternalSources { get; set; } = new List<ProjectExternalSource>();
        public List<ProjectTagTable> TagTables { get; set; } = new List<ProjectTagTable>();
        public List<ProjectTag> Tags { get; set; } = new List<ProjectTag>();
        public List<ProjectGroup> Groups { get; set; } = new List<ProjectGroup>();
        public List<ProjectNetwork> Networks { get; set; } = new List<ProjectNetwork>();
        public List<ProjectAlarm> Alarms { get; set; } = new List<ProjectAlarm>();
        public List<ProjectScreen> Screens { get; set; } = new List<ProjectScreen>();
    }

    public abstract class ProjectObject
    {
        public string Station { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public sealed class ProjectDevice : ProjectObject
    {
        public string Article { get; set; } = "";
        public string Firmware { get; set; } = "";
        public string? ParentPath { get; set; }
        public int? Slot { get; set; }
        public string? Ip { get; set; }
        public string? ProfinetName { get; set; }
    }

    public sealed class ProjectBlock : ProjectObject
    {
        public string Kind { get; set; } = "";
        public string Group { get; set; } = "";
        public int? Number { get; set; }
        public string? InstanceType { get; set; }
        public string? InterfaceFingerprint { get; set; }
        public string? CodeFingerprint { get; set; }
        public string? LibraryTypeFingerprint { get; set; }
        public Dictionary<string, string> Members { get; set; } = new Dictionary<string, string>();
    }

    public sealed class ProjectType : ProjectObject
    {
        public string Group { get; set; } = "";
        public string? InterfaceFingerprint { get; set; }
        public Dictionary<string, string> Members { get; set; } = new Dictionary<string, string>();
    }

    public sealed class ProjectTagTable : ProjectObject { }

    public sealed class ProjectExternalSource : ProjectObject
    {
        public string Group { get; set; } = "";
        public string ContentFingerprint { get; set; } = "";
    }

    public sealed class ProjectTag : ProjectObject
    {
        public string Table { get; set; } = "";
        public string DataType { get; set; } = "";
        public string Address { get; set; } = "";
    }

    public sealed class ProjectGroup : ProjectObject
    {
        public string Kind { get; set; } = "";
    }

    public sealed class ProjectNetwork : ProjectObject
    {
        public string DeviceItemPath { get; set; } = "";
        public int InterfaceIndex { get; set; }
        public string SubnetType { get; set; } = "";
        public string? Ip { get; set; }
        public string? ProfinetName { get; set; }
    }

    public sealed class ProjectAlarm : ProjectObject
    {
        public int Number { get; set; }
        public string Class { get; set; } = "";
        public int Priority { get; set; }
        public string Acknowledgement { get; set; } = "";
        public Dictionary<string, string> Texts { get; set; } = new Dictionary<string, string>();
        public string Backend { get; set; } = "";
    }

    public sealed class ProjectScreen : ProjectObject
    {
        public string? Parent { get; set; }
        public string TemplateFingerprint { get; set; } = "";
        public Dictionary<string, JsonElement> Design { get; set; } = new Dictionary<string, JsonElement>();
        public List<ProjectScreenWidget> Widgets { get; set; } = new List<ProjectScreenWidget>();
    }

    public sealed class ProjectScreenWidget
    {
        public string Name { get; set; } = "";
        public string Device { get; set; } = "";
        public int Slot { get; set; }
        public string TemplateFingerprint { get; set; } = "";
    }

    public sealed class GenerationArtifact
    {
        public string Path { get; }
        public string Sha256 { get; }
        public string Content { get; }
        internal GenerationArtifact(string path, string content)
        {
            StandardPackageLoader.ValidatePath(path);
            Path = path;
            Content = content;
            Sha256 = CanonicalJson.HashBytes(System.Text.Encoding.UTF8.GetBytes(content));
        }
    }

    public sealed class GenerationPlanningResult
    {
        public GenerationPlan Plan { get; }
        public ProjectModel Expected { get; }
        public IReadOnlyList<GenerationArtifact> Artifacts { get; }
        public string CanonicalPlan => GenerationDocuments.Canonical(Plan);
        private readonly string artifactRoot;
        internal GenerationPlanningResult(GenerationPlan plan, ProjectModel expected, IReadOnlyList<GenerationArtifact> artifacts, string artifactRoot)
        { Plan = plan; Expected = expected; Artifacts = artifacts; this.artifactRoot = artifactRoot; }

        public void StageArtifacts()
        {
            if (GenerationDocuments.PlanHash(CanonicalPlan) != Plan.PlanHash) throw CanonicalJson.Failure("/planHash", "plan-hash", "Plan changed after generation.");
            var root = Path.GetFullPath(artifactRoot);
            for (var ancestor = new DirectoryInfo(root); ancestor != null; ancestor = ancestor.Parent)
                if (ancestor.Exists) RejectLink(ancestor.FullName);
            Directory.CreateDirectory(root);
            RejectLink(root);
            foreach (var artifact in Artifacts)
            {
                StandardPackageLoader.ValidatePath(artifact.Path);
                var parts = artifact.Path.Split('/');
                var directory = root;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    directory = Path.Combine(directory, parts[i]);
                    Directory.CreateDirectory(directory); RejectLink(directory);
                }
                var path = Path.Combine(directory, parts[parts.Length - 1]);
                var bytes = new UTF8Encoding(false).GetBytes(artifact.Content);
                if (CanonicalJson.HashBytes(bytes) != artifact.Sha256) throw CanonicalJson.Failure(artifact.Path, "artifact-hash", "Artifact changed after generation.");
                if (File.Exists(path))
                {
                    RejectLink(path);
                    if (CanonicalJson.HashBytes(File.ReadAllBytes(path)) != artifact.Sha256) throw CanonicalJson.Failure(artifact.Path, "artifact-conflict", "Staged file differs; it will not be overwritten.");
                    continue;
                }
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(bytes, 0, bytes.Length);
            }
            void RejectLink(string path)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw CanonicalJson.Failure(path, "path", "Staging refuses links/reparse points.");
            }
        }
    }

    public sealed class GenerationPlanningOptions
    {
        public string ArtifactRoot { get; set; } = "";
        // Paths must already be known at planning time. Apply must not fill in returned identities.
        public Dictionary<string, string> SoftwarePaths { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> DeviceItemPaths { get; set; } = new Dictionary<string, string>();
        public string? BackupPath { get; set; }
        public int MaximumObjects { get; set; } = 10000;
    }
}
