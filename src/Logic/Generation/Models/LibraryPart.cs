using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class LibraryPart
    {
        public List<LibraryPartTypesItem> Types { get; set; } = new List<LibraryPartTypesItem>();
        public List<LibraryPartLibrariesItem> Libraries { get; set; } = new List<LibraryPartLibrariesItem>();
    }

    public sealed class LibraryPartTypesItem
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Version { get; set; } = "";
        public LibraryPartTypesItemInterface Interface { get; set; } = new LibraryPartTypesItemInterface();
        public List<LibraryPartTypesItemImplementationsItem> Implementations { get; set; } = new List<LibraryPartTypesItemImplementationsItem>();
    }

    public sealed class LibraryPartTypesItemInterface
    {
        public List<InterfaceMember>? In { get; set; }
        public List<InterfaceMember>? Out { get; set; }
        public List<InterfaceMember>? InOut { get; set; }
        public List<InterfaceMember>? Static { get; set; }
    }

    public sealed class LibraryPartTypesItemImplementationsItem
    {
        public JsonElement Releases { get; set; }
        public string Kind { get; set; } = "";
        public List<string>? Files { get; set; }
        public string? Library { get; set; }
        public string? TypePath { get; set; }
        public string? VersionRange { get; set; }
        public string? MasterCopyPath { get; set; }
        public Dictionary<string, string>? InterfaceMap { get; set; }
    }

    public sealed class LibraryPartLibrariesItem
    {
        public string Id { get; set; } = "";
        public string Provision { get; set; } = "";
        public Dictionary<string, string> FileNames { get; set; } = new Dictionary<string, string>();
        public string? Note { get; set; }
        public string? License { get; set; }
    }
}
