using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using TiaMcp.Versioning;

namespace TiaMcp.Updater
{
    public sealed class UpdaterOptions
    {
        public string InstallRoot { get; set; }
        public string Version { get; set; }
        public string Repository { get; set; } = "asckye/TIA_Portal_Openness_MCP";
        public int TimeoutSeconds { get; set; } = 60;
        public int WaitForPid { get; set; }
        public bool Check { get; set; }
        public bool Rollback { get; set; }
        public bool Force { get; set; }
        public bool RelaunchConfigurator { get; set; }
        public bool SelfTest { get; set; }
        // Overridden only by the offline suite. A real invocation always uses the GitHub URLs.
        public string ApiBaseUrl { get; set; }
        public string ReleasePageBaseUrl { get; set; }
        public Func<string, int, string> ReadTextForTest { get; set; }
        public Func<string, int, string> ReadRedirectForTest { get; set; }
        public Func<string, int, byte[]> ReadBytesForTest { get; set; }
        public int WaitForPidTimeoutSeconds { get; set; } = 30;
        public Action<string> BeforeDeployFile { get; set; }
        public Func<int, bool> ProcessExists { get; set; }
        internal string TemporaryDirectoryForTest { get; set; }
    }

    [DataContract]
    internal sealed class DeliveryRules
    {
        [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
        [DataMember(Name = "include")] public RuleSet Include { get; set; }
        [DataMember(Name = "exclude")] public RuleSet Exclude { get; set; }
        [DataMember(Name = "legacyCleanup")] public RuleSet LegacyCleanup { get; set; }
        [DataMember(Name = "requiredFiles")] public List<string> RequiredFiles { get; set; }
    }

    [DataContract]
    internal sealed class RuleSet
    {
        [DataMember(Name = "files")] public List<string> Files { get; set; }
        [DataMember(Name = "prefixes")] public List<string> Prefixes { get; set; }
    }

    [DataContract]
    internal sealed class ReleaseInfo
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; }
        [DataMember(Name = "published_at")] public string PublishedAt { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "assets")] public List<ReleaseAsset> Assets { get; set; }
    }

    [DataContract]
    internal sealed class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string Url { get; set; }
    }

    [DataContract]
    internal sealed class DeliveryManifest
    {
        [DataMember(Name = "release")] public string Release { get; set; }
        [DataMember(Name = "package")] public string Package { get; set; }
    }

    [DataContract]
    internal sealed class InventoryFileList
    {
        [DataMember(Name = "files")] public Dictionary<string, string> Files { get; set; }
    }
}
