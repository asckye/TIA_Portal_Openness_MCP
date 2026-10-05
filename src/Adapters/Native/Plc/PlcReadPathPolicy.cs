using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    internal sealed class PlcReadCandidate<T>
    {
        internal T Value = default(T)!;
        internal string ExactPath = "";
        internal string[] Groups = new string[0];
        internal string Device = "";
        internal string Host = "";
        internal bool AllowLegacyAlias = true;
        internal object? Context;
        internal object? DeviceContext;
    }
    internal static class PlcReadPathPolicy
    {
        // V17-compatible valid aliases: PLC host, device, device/host, group/.../alias.
        // Group segments are case-sensitive; device/host segments ignore case.
        // Deliberate safety corrections: reject ambiguous aliases and unconsumed segments.
        internal static T Resolve<T>(IEnumerable<PlcReadCandidate<T>> inventory,string path)
            => Select(inventory,path).Value;
        internal static PlcReadCandidate<T> Select<T>(IEnumerable<PlcReadCandidate<T>> inventory,string path)
        {
            if(string.IsNullOrWhiteSpace(path)) throw new ArgumentException("softwarePath must not be empty.");
            var all=inventory.ToArray();
            var exact=all.Where(c=>string.Equals(c.ExactPath,path,StringComparison.Ordinal)).ToArray();
            if(exact.Length==1) return exact[0];
            if(exact.Length>1) throw new ArgumentException("Ambiguous exact softwarePath: "+path);
            var parts=path.Split('/');
            if(parts.Any(p=>p.Length==0 || p=="." || p=="..")) throw new ArgumentException("softwarePath contains an empty or traversal segment.");
            var matches=all.Where(c=>Matches(c,parts)).ToArray();
            if(matches.Length==0) throw new ArgumentException("PLC software not found at '"+path+"'. Use the exact address from GetProjectTree.");
            if(matches.Length>1) throw new ArgumentException("Ambiguous softwarePath '"+path+"'. Select an exact address: "+string.Join(", ",matches.Select(c=>c.ExactPath)));
            return matches[0];
        }
        private static bool Matches<T>(PlcReadCandidate<T> c,string[] parts)
        {
            if(!c.AllowLegacyAlias) return false;
            int remaining=parts.Length-c.Groups.Length;
            if(remaining<1 || remaining>2) return false;
            for(int i=0;i<c.Groups.Length;i++) if(!string.Equals(c.Groups[i],parts[i],StringComparison.Ordinal)) return false;
            var first=parts[c.Groups.Length];
            if(remaining==1) return string.Equals(first,c.Device,StringComparison.OrdinalIgnoreCase) || string.Equals(first,c.Host,StringComparison.OrdinalIgnoreCase);
            return string.Equals(first,c.Device,StringComparison.OrdinalIgnoreCase) && string.Equals(parts[c.Groups.Length+1],c.Host,StringComparison.OrdinalIgnoreCase);
        }
    }
}
