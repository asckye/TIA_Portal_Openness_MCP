using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters
{
    public sealed class PlcConnectionResult
    {
        public string Stage { get; internal set; }="attached";
        public string Strategy { get; internal set; }="explicit-existing-pid";
        public int[] AttemptedPids { get; internal set; }=new int[0];
        public string LaunchMode { get; internal set; }="never";
        public bool OwnsPortal { get; internal set; }
    }
}
