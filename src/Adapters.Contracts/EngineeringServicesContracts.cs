using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts
{
    public sealed class EngineeringServicesRequest
    {
        public string? ValidationError { get; set; }
        public bool SameDevicePaths { get; set; }
        public string Operation { get; set; } = "";
        public string Action { get; set; } = "read";
        public string Kind { get; set; } = "device";
        public string Culture { get; set; } = "";
        public string SourceCulture { get; set; } = "";
        public string TargetCulture { get; set; } = "";
        public string FilePath { get; set; } = "";
        public bool UpdateSourceLanguage { get; set; }
        public string SoftwarePath { get; set; } = "";
        public string ObjectPath { get; set; } = "";
        public string Identifier { get; set; } = "";
        public string[] DevicePath { get; set; } = Array.Empty<string>();
        public string[] ItemPath { get; set; } = Array.Empty<string>();
        public string TargetProjectName { get; set; } = "";
        public string TargetSoftwarePath { get; set; } = "";
        public string[] TargetDevicePath { get; set; } = Array.Empty<string>();
        public string[] TargetItemPath { get; set; } = Array.Empty<string>();
        public string TargetLibraryName { get; set; } = "";
        public string LeftLibraryName { get; set; } = "";
        public string RightLibraryName { get; set; } = "";
        public bool IncludeIdentical { get; set; }
        public int MaxDepth { get; set; } = 8;
        public int Offset { get; set; }
        public int Limit { get; set; } = 100;
        public string FolderPath { get; set; } = "";
        public string CustomIdentityKey { get; set; } = "";
        public Dictionary<string, bool> CompilationSettings { get; set; } = new Dictionary<string, bool>();
        public bool DryRun { get; set; } = true;
    }

    public static class EngineeringServicesCapabilities
    {
        public static bool AtLeast(string release, string first)
        {
            var releases = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };
            return Array.IndexOf(releases, release) >= Array.IndexOf(releases, first);
        }
        public static string? Unsupported(string release, string tool, string kind = "", string customIdentityKey = "", System.Collections.Generic.IEnumerable<string>? properties = null)
        {
            if (tool == "GetObjectIdentifier" && !AtLeast(release, "20")) return "ObjectIdentifierProvider requires V20 or later.";
            if (tool == "ManageProjectCompilationSettings" && !AtLeast(release, "16")) return "Project compilation settings require V16 or later.";
            if (tool == "ManageProjectCompilationSettings" && !AtLeast(release, "19") && properties != null)
                foreach (var property in properties) if (property == "IsVirtualPlcDuringBlockCompilationEnabled") return "Virtual PLC compilation settings require V19 or later.";
            if (tool == "CompareLibraries" && !AtLeast(release, "18")) return "CompareToLibrary requires V18 or later.";
            if (tool == "CompareProjects" && kind == "hardware" && !AtLeast(release, "15.1")) return "HardwareObject.CompareTo requires V15.1 or later.";
            if (tool == "GetProjectSettings" && !string.IsNullOrEmpty(customIdentityKey) && !AtLeast(release, "16")) return "CustomIdentityProvider requires V16 or later.";
            return null;
        }
    }
}
