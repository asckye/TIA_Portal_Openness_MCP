using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class DeviceTypeRule
    {
        public string DeviceType { get; set; } = "";
        public Dictionary<string, string> Title { get; set; } = new Dictionary<string, string>();
        public ParameterSchema Params { get; set; } = new ParameterSchema();
        public List<DeviceTypeRuleSignalsItem> Signals { get; set; } = new List<DeviceTypeRuleSignalsItem>();
        public List<DeviceTypeRuleEmitItem> Emit { get; set; } = new List<DeviceTypeRuleEmitItem>();
    }

    public sealed class DeviceTypeRuleSignalsItem
    {
        public string Role { get; set; } = "";
        public string Dir { get; set; } = "";
        public string? Type { get; set; }
        public bool? Optional { get; set; }
    }

    public sealed class DeviceTypeRuleEmitItem
    {
        public string Kind { get; set; } = "";
        public string? ForEach { get; set; }
        public JsonElement? When { get; set; }
        public string? Id { get; set; }
        public string? Table { get; set; }
        public string? Name { get; set; }
        public string? Address { get; set; }
        public string? Type { get; set; }
        public string? CallIn { get; set; }
        public Dictionary<string, string>? Bind { get; set; }
        public string? Class { get; set; }
        public string? TextKey { get; set; }
        public string? Template { get; set; }
        public string? Screen { get; set; }
        public JsonElement? Slot { get; set; }
        public string? Role { get; set; }
        public string? Group { get; set; }
        public Dictionary<string, JsonElement>? Arguments { get; set; }
    }
}
