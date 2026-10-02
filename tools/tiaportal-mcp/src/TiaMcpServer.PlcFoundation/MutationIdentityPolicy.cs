using System;
using System.IO;

namespace TiaMcp.PlcFoundation
{
    public static class MutationIdentityPolicy
    {
        private static string Absolute(string path)
        {
            // .NET Framework has no Path.IsPathFullyQualified. Reject drive-relative
            // and current-drive-rooted paths explicitly on the Windows worker.
            bool drive=path!=null && path.Length>=3 && char.IsLetter(path[0]) && path[1]==':' && (path[2]=='\\' || path[2]=='/');
            bool unc=path!=null && path.StartsWith("\\\\",StringComparison.Ordinal) && path.Length>2;
            if(!drive && !unc) throw new ArgumentException("An absolute expected project file is required.");
            return Path.GetFullPath(path!);
        }
        public static void RequireSameProject(string expected,string actual)
        {
            if(!string.Equals(Absolute(expected),Absolute(actual),StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The project does not match expectedProjectFile; no mutation was started.");
        }
        public static void ValidateTarget(bool dryRun,bool confirm,string expected,string operation,string releaseKey,string file,string directory,string name,Action<string> requireBound)
        {
            if(dryRun) return;
            if(!confirm) throw new ArgumentException("Execution requires confirm=true.");
            expected=Absolute(expected);
            if(operation=="OpenProject") RequireSameProject(expected,file);
            else if(operation=="CreateProject")
            {
                if(releaseKey!="14sp1" && releaseKey!="15.1" && releaseKey!="16" && releaseKey!="17" && releaseKey!="18" && releaseKey!="19" && releaseKey!="20" && releaseKey!="21") throw new ArgumentException("Unsupported precise release key.");
                if(string.IsNullOrWhiteSpace(name) || name=="." || name==".." || name.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || name.Contains("/") || name.Contains("\\"))
                    throw new ArgumentException("A single new project name is required.");
                var major=releaseKey=="14sp1" ? "14" : releaseKey=="15.1" ? "15" : releaseKey;
                if(major!="14" && major!="15" && major!="16" && major!="17" && major!="18" && major!="19" && major!="20" && major!="21") throw new ArgumentException("Unsupported release key.");
                RequireSameProject(expected,Path.Combine(Absolute(directory),name,name+".ap"+major));
            }
            else requireBound(expected);
        }
    }
}
