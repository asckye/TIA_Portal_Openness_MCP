using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters
{
    public static class MutationIdentityPolicy
    {
        internal static string Absolute(string path) => Contracts.ProjectFileIdentity.Absolute(path);
        private static void RequireSegment(string segment) => Contracts.ProjectFileIdentity.RequireSegment(segment);
        internal static string AbsoluteFile(string path) => Contracts.ProjectFileIdentity.AbsoluteFile(path);
        public static void RequireSameProject(string expected,string actual)
        {
            bool same = string.Equals(AbsoluteFile(expected), AbsoluteFile(actual), StringComparison.OrdinalIgnoreCase);
            if (!same && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Contracts.ProjectFilePreset.WorkerVariable)))
                same = Contracts.ProjectFileCanonicalizer.Same(expected, actual);
            if(!same)
                throw new ArgumentException("The project does not match expectedProjectFile; no mutation was started.");
        }
        public static void ValidateTarget(bool dryRun,bool confirm,string expected,string operation,string releaseKey,string file,string directory,string name,
                Action<string> requireBound, string secondaryProjectFile = "")
        {
            if(dryRun) return;
            if(!confirm) throw new ArgumentException("Execution requires confirm=true.");
            expected=AbsoluteFile(expected);
            if (operation == "CloseProject" && secondaryProjectFile.Length > 0) RequireSameProject(expected, secondaryProjectFile);
            else if(operation=="OpenProject") RequireSameProject(expected,file);
            else if(operation=="CreateProject")
            {
                if(releaseKey!="14sp1" && releaseKey!="15.1" && releaseKey!="16" && releaseKey!="17" && releaseKey!="18" && releaseKey!="19"
                        && releaseKey!="20" && releaseKey!="21")
                    throw new ArgumentException("Unsupported precise release key.");
                if(string.IsNullOrWhiteSpace(name) || name=="." || name==".." || name.Contains("/") || name.Contains("\\"))
                    throw new ArgumentException("A single new project name is required.");
                RequireSegment(name);
                var major=releaseKey=="14sp1" ? "14" : releaseKey=="15.1" ? "15_1" : releaseKey;
                if(major!="14" && major!="15_1" && major!="16" && major!="17" && major!="18" && major!="19" && major!="20" && major!="21")
                    throw new ArgumentException("Unsupported release key.");
                RequireSameProject(expected,Absolute(directory).TrimEnd('\\')+"\\"+name+"\\"+name+".ap"+major);
            }
            else requireBound(expected);
        }
    }
}
