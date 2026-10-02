using System;
using System.IO;
using TiaMcp.Versioning;

namespace TiaMcp.PlcFoundation
{
    internal sealed class PlcLifecycleState
    {
        internal int? ProcessId { get; private set; }
        internal string? ProjectFile { get; private set; }
        internal bool OwnsProject { get; private set; }
        internal bool IsLocalSession { get; private set; }
        internal void RequireAttach(int processId)
        { if(processId<=0 || ProcessId.HasValue) throw new ArgumentException("Select one positive process ID while detached; repeated attach is refused."); }
        internal void Attached(int processId) { RequireAttach(processId); ProcessId=processId; }
        internal void RequireUnbound()
        { if(!ProcessId.HasValue || ProjectFile!=null) throw new InvalidOperationException("An attached process with no bound project/session is required."); }
        internal void Bound(string file,bool owns,bool local)
        { RequireUnbound(); ProjectFile=MutationIdentityPolicy.AbsoluteFile(file); OwnsProject=owns; IsLocalSession=local; }
        internal void RequireBound()
        { if(!ProcessId.HasValue || ProjectFile==null) throw new InvalidOperationException("Bind or open an explicit project/session first."); }
        internal void RequireClose(bool isModified)
        { RequireBound(); if(!OwnsProject) throw new InvalidOperationException("Borrowed projects or borrowed local sessions are never closed."); if(isModified) throw new InvalidOperationException("Save explicitly before closing; implicit discard is refused."); }
        internal void Unbound() { RequireBound(); ProjectFile=null; OwnsProject=false; IsLocalSession=false; }
        internal void Detached() { ProjectFile=null; OwnsProject=false; IsLocalSession=false; ProcessId=null; }
    }
    internal static class PlcLifecyclePolicy
    {
        internal static void RequireLocalSessionExecution(bool localSession,bool dryRun)
        {
            if(localSession && !dryRun) throw new NotSupportedException("Local-session mutation is blocked until session kind, server-lock effects and project identity are reconciled. Opening an exclusive session can lock a server project; preview grants no execution permission.");
        }
        internal static string Major(string release) { TiaVersionCatalog.Get(release); return release=="14sp1" ? "14" : release=="15.1" ? "15" : release; }
        internal static bool IsSessionFile(string release,string path)
        {
            var major=Major(release); var extension=Path.GetExtension(path);
            if(extension.Equals(".ap"+major,StringComparison.OrdinalIgnoreCase)) return false;
            if(extension.Equals(".als"+major,StringComparison.OrdinalIgnoreCase))
            {
                if(release=="14sp1" || release=="15.1" || release=="16") throw new NotSupportedException("LocalSession API is absent from the selected native PublicAPI; local-session operations are unavailable.");
                return true;
            }
            throw new ArgumentException("Use this release's exact .ap"+major+" or supported .als"+major+" file; archive, upgrade and server-open operations are unavailable.");
        }
        internal static string CreationFile(string release,string directory,string name)
        { PlcFoundationPolicy.RequireName(name); return MutationIdentityPolicy.Absolute(MutationIdentityPolicy.Absolute(directory).TrimEnd('\\')+"\\"+name+"\\"+name+".ap"+Major(release)); }
    }
    public sealed class PlcConnectionResult
    {
        public string Stage { get; internal set; }="attached";
        public string Strategy { get; internal set; }="explicit-existing-pid";
        public int[] AttemptedPids { get; internal set; }=new int[0];
        public string LaunchMode { get; internal set; }="never";
        public bool OwnsPortal { get; internal set; }
    }
}
