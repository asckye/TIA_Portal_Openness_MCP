using System;
using System.IO;
using TiaMcp.Adapters.Contracts;
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
        { if(processId<=0 || ProcessId.HasValue) throw new AdapterPreconditionException("Select one positive process ID while detached; repeated attach is refused.","processId"); }
        internal void Attached(int processId) { RequireAttach(processId); ProcessId=processId; }
        internal void RequireUnbound()
        { if(!ProcessId.HasValue || ProjectFile!=null) throw new AdapterPreconditionException("An attached process with no bound project/session is required.",isArgument:false); }
        internal void Bound(string file,bool owns,bool local)
        { RequireUnbound(); ProjectFile=MutationIdentityPolicy.AbsoluteFile(file); OwnsProject=owns; IsLocalSession=local; }
        internal void RequireBound()
        { if(!ProcessId.HasValue || ProjectFile==null) throw new AdapterPreconditionException("Bind or open an explicit project/session first.",isArgument:false); }
        internal void RequireClose(bool isModified)
        { RequireBound(); if(!OwnsProject) throw new AdapterPreconditionException("Borrowed projects or borrowed local sessions are never closed.",isArgument:false); if(isModified) throw new AdapterPreconditionException("Save explicitly before closing; implicit discard is refused.",isArgument:false); }
        internal void Unbound() { RequireBound(); ProjectFile=null; OwnsProject=false; IsLocalSession=false; }
        internal void Detached() { ProjectFile=null; OwnsProject=false; IsLocalSession=false; ProcessId=null; }
    }
    internal static class PlcLifecyclePolicy
    {
        internal static void RequireLocalSessionExecution(bool localSession,bool dryRun)
        {
            if(localSession && !dryRun) throw new AdapterPreconditionException("Local-session mutation is blocked until session kind, server-lock effects and project identity are reconciled. Opening an exclusive session can lock a server project; preview grants no execution permission.",isArgument:false);
        }
        // TIA names project files by release: V14 SP1 keeps .ap14, V15.1 uses .ap15_1 (.ap15 is the older V15 format).
        internal static string FileSuffix(string release) { TiaVersionCatalog.Get(release); return release=="14sp1" ? "14" : release=="15.1" ? "15_1" : release; }
        internal static bool IsSessionFile(string release,string path)
        {
            var major=FileSuffix(release); var extension=Path.GetExtension(path);
            if(extension.Equals(".ap"+major,StringComparison.OrdinalIgnoreCase)) return false;
            if(extension.Equals(".als"+major,StringComparison.OrdinalIgnoreCase))
            {
                if(release=="14sp1" || release=="15.1" || release=="16") throw new AdapterPreconditionException("LocalSession API is absent from the selected native PublicAPI; local-session operations are unavailable.","path",false);
                return true;
            }
            throw new AdapterPreconditionException("Use this release's exact .ap"+major+" or supported .als"+major+" file; archive, upgrade and server-open operations are unavailable.","path");
        }
        internal static string CreationFile(string release,string directory,string name)
        { PlcFoundationPolicy.RequireName(name); return MutationIdentityPolicy.Absolute(MutationIdentityPolicy.Absolute(directory).TrimEnd('\\')+"\\"+name+"\\"+name+".ap"+FileSuffix(release)); }
    }
}
