using System;
using System.Collections.Generic;

namespace TiaMcp.PlcFoundation
{
    public static class MutationIdentityPolicy
    {
        internal static string Absolute(string path)
        {
            // Interpret the target Windows namespace on every host OS. Never resolve
            // C:\... against a Linux working directory or accept device/relative paths.
            if(string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An absolute expected project file is required.");
            path=path.Replace('/', '\\');
            bool drive=path.Length>=3 && ((path[0]>='A' && path[0]<='Z') || (path[0]>='a' && path[0]<='z')) && path[1]==':' && path[2]=='\\';
            string root; string rest;
            if(drive) { root=path.Substring(0,3); rest=path.Substring(3); }
            else if(path.StartsWith("\\\\",StringComparison.Ordinal))
            {
                var parts=path.Substring(2).Split('\\');
                if(parts.Length<2 || parts[0].Length==0 || parts[1].Length==0 || parts[0]=="." || parts[0]==".." || parts[1]=="." || parts[1]=="..")
                    throw new ArgumentException("A complete UNC server and share are required.");
                RequireSegment(parts[0]); RequireSegment(parts[1]);
                root="\\\\"+parts[0]+"\\"+parts[1]+"\\";
                rest=string.Join("\\",parts,2,parts.Length-2);
            }
            else throw new ArgumentException("An absolute expected project file is required.");
            var segments=new List<string>();
            foreach(var segment in rest.Split('\\'))
            {
                if(segment.Length==0 || segment==".") continue;
                if(segment=="..")
                {
                    if(segments.Count==0) throw new ArgumentException("Project path escapes its Windows root.");
                    segments.RemoveAt(segments.Count-1); continue;
                }
                RequireSegment(segment); segments.Add(segment);
            }
            return root+string.Join("\\",segments)+(segments.Count>0 && path.EndsWith("\\",StringComparison.Ordinal) ? "\\" : "");
        }
        private static void RequireSegment(string segment)
        {
            // Reject aliases whose Win32 normalization is ambiguous rather than
            // silently broadening project identity (ADS, device names, trailing dots).
            if(segment.EndsWith(".",StringComparison.Ordinal) || segment.EndsWith(" ",StringComparison.Ordinal))
                throw new ArgumentException("Ambiguous Windows path segment.");
            foreach(char c in segment) if(c<32 || "<>:\"|?*".IndexOf(c)>=0)
                throw new ArgumentException("Invalid Windows path segment.");
            string stem=segment.Split('.')[0].ToUpperInvariant();
            if(stem=="CON" || stem=="PRN" || stem=="AUX" || stem=="NUL" ||
               (stem.Length==4 && (stem.StartsWith("COM",StringComparison.Ordinal) || stem.StartsWith("LPT",StringComparison.Ordinal)) && ((stem[3]>='1' && stem[3]<='9') || stem[3]=='¹' || stem[3]=='²' || stem[3]=='³')))
                throw new ArgumentException("Windows device names cannot identify a project.");
        }
        internal static string AbsoluteFile(string path)
        {
            if(path!=null && (path.EndsWith("\\",StringComparison.Ordinal) || path.EndsWith("/",StringComparison.Ordinal)))
                throw new ArgumentException("A project file cannot end with a directory separator.");
            return Absolute(path!);
        }
        public static void RequireSameProject(string expected,string actual)
        {
            if(!string.Equals(AbsoluteFile(expected),AbsoluteFile(actual),StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The project does not match expectedProjectFile; no mutation was started.");
        }
        public static void ValidateTarget(bool dryRun,bool confirm,string expected,string operation,string releaseKey,string file,string directory,string name,Action<string> requireBound)
        {
            if(dryRun) return;
            if(!confirm) throw new ArgumentException("Execution requires confirm=true.");
            expected=AbsoluteFile(expected);
            if(operation=="OpenProject") RequireSameProject(expected,file);
            else if(operation=="CreateProject")
            {
                if(releaseKey!="14sp1" && releaseKey!="15.1" && releaseKey!="16" && releaseKey!="17" && releaseKey!="18" && releaseKey!="19" && releaseKey!="20" && releaseKey!="21") throw new ArgumentException("Unsupported precise release key.");
                if(string.IsNullOrWhiteSpace(name) || name=="." || name==".." || name.Contains("/") || name.Contains("\\"))
                    throw new ArgumentException("A single new project name is required.");
                RequireSegment(name);
                var major=releaseKey=="14sp1" ? "14" : releaseKey=="15.1" ? "15_1" : releaseKey;
                if(major!="14" && major!="15_1" && major!="16" && major!="17" && major!="18" && major!="19" && major!="20" && major!="21") throw new ArgumentException("Unsupported release key.");
                RequireSameProject(expected,Absolute(directory).TrimEnd('\\')+"\\"+name+"\\"+name+".ap"+major);
            }
            else requireBound(expected);
        }
    }
}
