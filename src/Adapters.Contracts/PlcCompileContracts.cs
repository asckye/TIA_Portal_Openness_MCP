using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
    public sealed class PlcDiagnostic
    {
        public string State { get; set; }="";
        public string Description { get; set; }="";
        public string Formatted { get; set; }="";
        public bool HasChildren { get; set; }
    }
}
