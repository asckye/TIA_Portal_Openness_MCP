using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class MachineDescription
    {
        public string Schema { get; set; } = "";
        public MachineDescriptionMachine Machine { get; set; } = new MachineDescriptionMachine();
        public PackageReference Standard { get; set; } = new PackageReference();
        public MachineDescriptionTarget Target { get; set; } = new MachineDescriptionTarget();
        public List<MachineDescriptionStationsItem> Stations { get; set; } = new List<MachineDescriptionStationsItem>();
        public List<MachineDescriptionTopology> Topology { get; set; } = new List<MachineDescriptionTopology>();
        public List<MachineDescriptionDevicesItem> Devices { get; set; } = new List<MachineDescriptionDevicesItem>();
        public MachineDescriptionOptions Options { get; set; } = new MachineDescriptionOptions();
    }

    public sealed class MachineDescriptionMachine
    {
        public string Id { get; set; } = "";
        public Dictionary<string, string> Name { get; set; } = new Dictionary<string, string>();
    }

    public sealed class MachineDescriptionTarget
    {
        public string Release { get; set; } = "";
        public MachineDescriptionTargetProject Project { get; set; } = new MachineDescriptionTargetProject();
    }

    public sealed class MachineDescriptionTargetProject
    {
        public string Mode { get; set; } = "";
        public string? Name { get; set; }
        public string? Directory { get; set; }
        public string? ProjectIdentity { get; set; }
        public string? SoftwarePath { get; set; }
    }

    public sealed class MachineDescriptionStationsItem
    {
        public string Id { get; set; } = "";
        public string Role { get; set; } = "";
        public string? Article { get; set; }
        public string? Firmware { get; set; }
        public string? Ip { get; set; }
        public string? ProfinetName { get; set; }
        public string? Kind { get; set; }
    }

    public sealed class MachineDescriptionTopology
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public Dictionary<string, string> Name { get; set; } = new Dictionary<string, string>();
        public List<MachineDescriptionTopology>? Children { get; set; }
    }

    public sealed class MachineDescriptionDevicesItem
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Parent { get; set; } = "";
        public string Station { get; set; } = "";
        public Dictionary<string, string> Name { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, JsonElement> Params { get; set; } = new Dictionary<string, JsonElement>();
        public Dictionary<string, string> Io { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, bool>? Alarms { get; set; }
        public MachineDescriptionDevicesItemHmi? Hmi { get; set; }
        public string? Note { get; set; }
    }

    public sealed class MachineDescriptionDevicesItemHmi
    {
        public string? Screen { get; set; }
    }

    public sealed class MachineDescriptionOptions
    {
        public List<string> Languages { get; set; } = new List<string>();
    }
}
