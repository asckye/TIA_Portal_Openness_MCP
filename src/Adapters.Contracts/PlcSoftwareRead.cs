using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
    public sealed class PlcSoftwareDetails
    {
        public string Name { get; set; } = "";
        public PlcAttributeValue[] Attributes { get; set; } = new PlcAttributeValue[0];
        public string? Description { get; set; }
        public object? Meta { get; set; }
    }
    public sealed class PlcSoftwareTreeDetails
    {
        public string Tree { get; set; } = "";
        public object? Meta { get; set; }
    }
}
