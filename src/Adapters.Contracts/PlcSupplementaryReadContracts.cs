using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcTechnologyReadRow
    {
        public string Name { get; set; } = "";
        public string? OfSystemLibElement { get; set; }
        public string? OfSystemLibVersion { get; set; }
        public string[] UnavailableAttributes { get; set; } = new string[0];
        public string Folder { get; set; } = "";
    }
    public sealed class PlcSupplementaryReadResult
    {
        public string SoftwarePath { get; set; } = "";
        public string ReleaseKey { get; set; } = "";
        public string Scope { get; set; } = "";
        public object Items { get; set; } = new string[0];
    }
}
