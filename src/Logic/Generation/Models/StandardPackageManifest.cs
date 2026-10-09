using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class StandardPackageManifest
    {
        public int SchemaVersion { get; set; }
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public Dictionary<string, string> Title { get; set; } = new Dictionary<string, string>();
        public string? Publisher { get; set; }
        public string License { get; set; } = "";
        public PackageReference? Extends { get; set; }
        public List<PackageReference>? DependsOn { get; set; }
        public StandardPackageManifestTargets Targets { get; set; } = new StandardPackageManifestTargets();
        public StandardPackageManifestRequires Requires { get; set; } = new StandardPackageManifestRequires();
        public StandardPackageManifestLanguages Languages { get; set; } = new StandardPackageManifestLanguages();
        public StandardPackageManifestParts Parts { get; set; } = new StandardPackageManifestParts();
        public List<FileDigest> Files { get; set; } = new List<FileDigest>();
        public Dictionary<string, string>? Description { get; set; }
    }

    public sealed class StandardPackageManifestTargets
    {
        public List<string> Releases { get; set; } = new List<string>();
        public List<string> PlcFamilies { get; set; } = new List<string>();
        public List<string> Hmi { get; set; } = new List<string>();
    }

    public sealed class StandardPackageManifestRequires
    {
        public string Framework { get; set; } = "";
        public List<string> OptionalProducts { get; set; } = new List<string>();
    }

    public sealed class StandardPackageManifestLanguages
    {
        public List<string> Required { get; set; } = new List<string>();
        public string Default { get; set; } = "";
    }

    public sealed class StandardPackageManifestParts
    {
        public string? Naming { get; set; }
        public string? Structure { get; set; }
        public string? Hardware { get; set; }
        public string? Library { get; set; }
        public string? Modes { get; set; }
        public string? Alarms { get; set; }
        public string? Hmi { get; set; }
        public string? Checks { get; set; }
        public List<string>? Rules { get; set; }
    }
}
