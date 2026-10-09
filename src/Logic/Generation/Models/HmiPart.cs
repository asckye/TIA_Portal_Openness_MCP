using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class HmiPart
    {
        public List<HmiPartProfilesItem> Profiles { get; set; } = new List<HmiPartProfilesItem>();
        public Dictionary<string, JsonElement>? Theme { get; set; }
        public List<HmiPartScreensItem> Screens { get; set; } = new List<HmiPartScreensItem>();
        public List<HmiPartWidgetsItem> Widgets { get; set; } = new List<HmiPartWidgetsItem>();
        public List<HmiPartTagGroupsItem> TagGroups { get; set; } = new List<HmiPartTagGroupsItem>();
    }

    public sealed class HmiPartProfilesItem
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public sealed class HmiPartScreensItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Template { get; set; } = "";
        public string? Parent { get; set; }
        public string? Profile { get; set; }
    }

    public sealed class HmiPartWidgetsItem
    {
        public string Id { get; set; } = "";
        public string DeviceType { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Template { get; set; } = "";
        public Dictionary<string, string>? InterfaceMap { get; set; }
    }

    public sealed class HmiPartTagGroupsItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Path { get; set; }
    }
}
