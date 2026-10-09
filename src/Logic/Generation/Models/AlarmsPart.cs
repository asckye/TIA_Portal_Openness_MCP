using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class AlarmsPart
    {
        public List<AlarmsPartClassesItem> Classes { get; set; } = new List<AlarmsPartClassesItem>();
        public List<AlarmsPartTemplatesItem> Templates { get; set; } = new List<AlarmsPartTemplatesItem>();
        public Dictionary<string, Dictionary<string, string>> Texts { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public AlarmsPartNumbering? Numbering { get; set; }
    }

    public sealed class AlarmsPartClassesItem
    {
        public string Id { get; set; } = "";
        public int Priority { get; set; }
        public string Acknowledgement { get; set; } = "";
        public string? Color { get; set; }
    }

    public sealed class AlarmsPartTemplatesItem
    {
        public string Id { get; set; } = "";
        public string DeviceType { get; set; } = "";
        public string Class { get; set; } = "";
        public string TextKey { get; set; } = "";
        public string Backend { get; set; } = "";
        public Dictionary<string, string>? Cause { get; set; }
        public Dictionary<string, string>? Consequence { get; set; }
        public Dictionary<string, string>? Remedy { get; set; }
        public string? Suppression { get; set; }
    }

    public sealed class AlarmsPartNumbering
    {
        public int Start { get; set; }
        public int End { get; set; }
        public string? Template { get; set; }
    }
}
