using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class StructurePart
    {
        public List<StructurePartGroupsItem> Groups { get; set; } = new List<StructurePartGroupsItem>();
        public List<StructurePartOrganizationBlocksItem>? OrganizationBlocks { get; set; }
        public List<StructurePartNumberRangesItem>? NumberRanges { get; set; }
    }

    public sealed class StructurePartGroupsItem
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Path { get; set; } = "";
        public bool? Required { get; set; }
    }

    public sealed class StructurePartOrganizationBlocksItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int? Number { get; set; }
        public string Event { get; set; } = "";
    }

    public sealed class StructurePartNumberRangesItem
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Start { get; set; }
        public int End { get; set; }
    }
}
