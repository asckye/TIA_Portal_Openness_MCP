using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class HardwarePart
    {
        public List<HardwarePartRolesItem> Roles { get; set; } = new List<HardwarePartRolesItem>();
        public HardwarePartAllocation? Allocation { get; set; }
    }

    public sealed class HardwarePartRolesItem
    {
        public string Id { get; set; } = "";
        public List<HardwarePartRolesItemVariantsItem> Variants { get; set; } = new List<HardwarePartRolesItemVariantsItem>();
    }

    public sealed class HardwarePartRolesItemVariantsItem
    {
        public List<string> Releases { get; set; } = new List<string>();
        public string Article { get; set; } = "";
        public string? Firmware { get; set; }
        public string? Kind { get; set; }
        public List<HardwarePartRolesItemVariantsItemModulesItem>? Modules { get; set; }
    }

    public sealed class HardwarePartRolesItemVariantsItemModulesItem
    {
        public string Id { get; set; } = "";
        public string Article { get; set; } = "";
        public int Slot { get; set; }
    }

    public sealed class HardwarePartAllocation
    {
        public HardwarePartAllocationIp? Ip { get; set; }
        public List<HardwarePartAllocationIoItem>? Io { get; set; }
        public string? ProfinetName { get; set; }
        public string? IoSystemName { get; set; }
    }

    public sealed class HardwarePartAllocationIp
    {
        public string Subnet { get; set; } = "";
        public string Start { get; set; } = "";
        public string End { get; set; } = "";
    }

    public sealed class HardwarePartAllocationIoItem
    {
        public string Id { get; set; } = "";
        public string Direction { get; set; } = "";
        public string Start { get; set; } = "";
        public string End { get; set; } = "";
    }
}
