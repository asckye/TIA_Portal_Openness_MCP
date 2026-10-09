using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class ModesPart
    {
        public List<ModesPartModelsItem> Models { get; set; } = new List<ModesPartModelsItem>();
    }

    public sealed class ModesPartModelsItem
    {
        public string Id { get; set; } = "";
        public string ImplementationType { get; set; } = "";
        public string DataType { get; set; } = "";
        public List<ModesPartModelsItemModesItem> Modes { get; set; } = new List<ModesPartModelsItemModesItem>();
        public List<ModesPartModelsItemStatesItem> States { get; set; } = new List<ModesPartModelsItemStatesItem>();
        public List<ModesPartModelsItemCommandsItem> Commands { get; set; } = new List<ModesPartModelsItemCommandsItem>();
        public List<ModesPartModelsItemTransitionsItem> Transitions { get; set; } = new List<ModesPartModelsItemTransitionsItem>();
        public List<ModesPartModelsItemAvailabilityItem> Availability { get; set; } = new List<ModesPartModelsItemAvailabilityItem>();
        public List<ModesPartModelsItemMappingsItem>? Mappings { get; set; }
    }

    public sealed class ModesPartModelsItemModesItem
    {
        public string Id { get; set; } = "";
        public int Number { get; set; }
        public Dictionary<string, string> Title { get; set; } = new Dictionary<string, string>();
    }

    public sealed class ModesPartModelsItemStatesItem
    {
        public string Id { get; set; } = "";
        public int Number { get; set; }
        public Dictionary<string, string> Title { get; set; } = new Dictionary<string, string>();
    }

    public sealed class ModesPartModelsItemCommandsItem
    {
        public string Id { get; set; } = "";
        public int Number { get; set; }
        public Dictionary<string, string> Title { get; set; } = new Dictionary<string, string>();
    }

    public sealed class ModesPartModelsItemTransitionsItem
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string? Command { get; set; }
        public bool? Completed { get; set; }
    }

    public sealed class ModesPartModelsItemAvailabilityItem
    {
        public string Mode { get; set; } = "";
        public List<string> States { get; set; } = new List<string>();
    }

    public sealed class ModesPartModelsItemMappingsItem
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Type { get; set; } = "";
        public Dictionary<string, string> Members { get; set; } = new Dictionary<string, string>();
    }
}
