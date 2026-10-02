using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Versioning;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcDiagnostic
    {
        public string State { get; set; }="";
        public string Description { get; set; }="";
        public string Formatted { get; set; }="";
        public bool HasChildren { get; set; }
    }
    internal static class PlcCompilePolicy
    {
        internal static void RequirePasswordCapability(string release,string password)
        {
            TiaVersionCatalog.Get(release);
            if(password.Length!=0 && (release=="14sp1" || release=="15.1" || release=="16"))
                throw new NotSupportedException("The supplied native PublicAPI for "+release+" has no SafetyAdministration login API; password-based compilation is unavailable in this adapter. No password is ignored.");
        }
        internal static void Classify(PlcCompileResult result,IEnumerable<PlcDiagnostic> diagnostics)
        {
            var raw=new List<string>(); var errors=new List<string>(); var warnings=new List<string>(); var info=new List<string>();
            foreach(var item in diagnostics)
            {
                raw.Add(item.Formatted);
                var description=item.Description.Trim();
                if(new[]{"Compiling finished","Compilation finished","Kompilierung beendet"}.Any(s=>description.StartsWith(s,StringComparison.OrdinalIgnoreCase))) continue;
                bool error=item.State.IndexOf("error",StringComparison.OrdinalIgnoreCase)>=0 || item.State.IndexOf("fehler",StringComparison.OrdinalIgnoreCase)>=0;
                bool warning=item.State.IndexOf("warning",StringComparison.OrdinalIgnoreCase)>=0 || item.State.IndexOf("warnung",StringComparison.OrdinalIgnoreCase)>=0;
                if((error || warning) && item.HasChildren && string.IsNullOrWhiteSpace(item.Description)) continue;
                var target=error ? errors : warning ? warnings : info;
                if((error || warning || description.Length!=0) && !target.Contains(item.Formatted)) target.Add(item.Formatted);
            }
            result.Messages=raw.ToArray(); result.Errors=errors.ToArray(); result.Warnings=warnings.ToArray(); result.Info=info.ToArray();
        }
    }
}
