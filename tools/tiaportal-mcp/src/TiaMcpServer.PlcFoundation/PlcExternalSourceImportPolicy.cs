using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcExternalSourceImportPlan
    {
        public string Status { get; internal set; } = "planned";
        public string MutationStatus => "notAttempted";
        public bool Executed => false;
        public bool Attempted => false;
        public int CreatedCount => 0;
        public bool ApplyBlocked => true;
        public string ApplyBlockedReason => PlcExternalSourceImportPolicy.ApplyBlock;
        public string Generation => "notRun";
        public string Compilation => "notRun";
        public string Save => "notRun";
        public string Download => "notRun";
        public string Release { get; internal set; } = "";
        public string ProjectFile { get; internal set; } = "";
        public int ProcessId { get; internal set; }
        public string SoftwarePath { get; internal set; } = "";
        public string GroupPath => "";
        public string SessionKind => "ordinary-project";
        public string FilePath { get; internal set; } = "";
        public string SourceName { get; internal set; } = "";
        public string Extension { get; internal set; } = "";
        public long ByteCount { get; internal set; }
        public string InputSha256 { get; internal set; } = "";
        public string PlanHash { get; internal set; } = "";
        public int InventoryCount { get; internal set; }
        public string CollisionStatus => "none-in-complete-root-snapshot-not-race-proof";
        public string ValidationScope => "wrapper-policy: ASCII printable plus TAB/CR/LF, unchanged bytes, 1..4194304 bytes; syntax/native validity/encoding support/source lifetime not established";
        public string Policy => "root-external-source-plan-v1";
    }
    internal sealed class PlcExternalSourceImportRequest
    {
        internal string Release="", Project="", Software="", Group="", File="", AllowedFile="", ExpectedHash="", ExpectedProject="";
        internal int ProcessId;
        internal bool DryRun=true, Confirm;
    }
    internal static class PlcExternalSourceImportPolicy
    {
        internal const int MaximumFileBytes=4*1024*1024;
        internal const string ApplyBlock="This compatibility planning tool cannot execute. Use ImportPlcExternalSource and its own preview/confirmation contract to create a source.";
        private static string Hash(byte[] bytes) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        private static string Field(string value)=>value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+value;
        internal static string ValidateFile(string file)
        {
            if(file==null || file.Length>1024 || file.IndexOf('/')>=0) throw new ArgumentException("Use a canonical bounded Windows drive file path with backslashes.");
            var canonical=MutationIdentityPolicy.AbsoluteFile(file);
            // Local drive only; UNC, device namespaces, redundant separators and traversal are refused.
            if(canonical.Length<4 || canonical[1]!=':' || canonical!=file) throw new ArgumentException("Only a canonical absolute local Windows drive file path is allowed.");
            var name=canonical.Substring(canonical.LastIndexOf('\\')+1);
            var dot=name.LastIndexOf('.');
            if(name.Length>128 || dot<=0 || !new[]{".scl",".awl",".db",".udt"}.Contains(name.Substring(dot).ToLowerInvariant())) throw new ArgumentException("One SCL, AWL, DB or UDT source file is required.");
            return canonical;
        }
        internal static void ValidateOptions(PlcExternalSourceImportRequest request)
        {
            if(!request.DryRun) throw new NotSupportedException(ApplyBlock);
            if(!new[]{"14sp1","15.1","16","17","18","19","20","21"}.Contains(request.Release)) throw new ArgumentException("Unknown exact release; V14/V15 are not aliases.");
            if(request.Group!="") throw new ArgumentException("Only groupPath empty string denotes the supported external-source root.");
            var path=ValidateFile(request.File);
            if(ValidateFile(request.AllowedFile)!=path) throw new ArgumentException("filePath must exactly match the single allowedFilePath, including casing.");
        }
        internal static FileStream OpenLocked(string path)
        {
            ValidateFile(path);
            if(Path.DirectorySeparatorChar!='\\') throw new PlatformNotSupportedException("Native input validation requires Windows; no host-dependent path reinterpretation.");
            var info=new FileInfo(path);
            if(!info.Exists || (info.Attributes & FileAttributes.Directory)!=0) throw new ArgumentException("An existing regular source file is required.");
            for(FileSystemInfo? item=info;item!=null;item=item is DirectoryInfo dir ? dir.Parent : ((FileInfo)item).Directory)
                if((item.Attributes & FileAttributes.ReparsePoint)!=0) throw new ArgumentException("Source path ancestry contains a reparse point.");
            // Reuse the reviewed read-only share: hold against write/delete for the whole validation.
            return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        }
        private static byte[] Read(Stream input)
        {
            if(!input.CanRead || !input.CanSeek || input.Length<1 || input.Length>MaximumFileBytes) throw new ArgumentException("Source must be a nonempty bounded seekable regular input.");
            input.Position=0; var length=input.Length;
            using(var memory=new MemoryStream())
            {
                var buffer=new byte[8192];int count;
                while((count=input.Read(buffer,0,buffer.Length))>0)
                {
                    if(memory.Length+count>MaximumFileBytes) throw new ArgumentException("Source exceeds byte budget.");
                    for(int i=0;i<count;i++) if((buffer[i]<32 && buffer[i]!=9 && buffer[i]!=10 && buffer[i]!=13) || buffer[i]>126) throw new ArgumentException("Wrapper ASCII subset refuses BOM, high bytes, NUL and other controls; no transcoding.");
                    memory.Write(buffer,0,count);
                }
                if(memory.Length!=length || input.Length!=length) throw new IOException("Source length changed during validation.");
                return memory.ToArray();
            }
        }
        private static string[] Inventory(Func<IEnumerable<string>> inventory,string name)
        {
            var items=inventory().Take(4097).ToArray();
            if(items.Length>4096) throw new ArgumentException("Root inventory exceeds 4096; truncated inventory is never accepted.");
            if(items.Any(x=>string.IsNullOrWhiteSpace(x) || x.Length>1024 || x.IndexOfAny(new[]{'\\','/','\0'})>=0)) throw new ArgumentException("Root source identity is incomplete or invalid.");
            if(items.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=items.Length) throw new ArgumentException("Ambiguous root source identities.");
            Func<string,string> stem=x=>x.LastIndexOf('.')>0 ? x.Substring(0,x.LastIndexOf('.')) : x;
            if(items.Any(x=>string.Equals(x,name,StringComparison.OrdinalIgnoreCase) || string.Equals(stem(x),stem(name),StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Exact/case-fold/stem source collision; no overwrite, alternate name or delete is allowed.");
            return items.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        }
        internal static PlcExternalSourceImportPlan Plan(PlcExternalSourceImportRequest request,Func<string,Stream> openLocked,Func<IEnumerable<string>> inventory,Action recheck)
        {
            ValidateOptions(request);
            var project=MutationIdentityPolicy.AbsoluteFile(request.Project);
            if(project!=request.Project || request.ProcessId<=0 || string.IsNullOrWhiteSpace(request.Software)) throw new ArgumentException("Exact project, positive process ID and software identity required.");
            if(request.ExpectedProject!="") MutationIdentityPolicy.RequireSameProject(request.ExpectedProject,project);
            using(var input=openLocked(request.File))
            {
                var bytes=Read(input); var name=request.File.Substring(request.File.LastIndexOf('\\')+1);
                var existing=Inventory(inventory,name);
                recheck();
                var second=Inventory(inventory,name);
                if(!existing.SequenceEqual(second,StringComparer.Ordinal)) throw new InvalidOperationException("Root source inventory changed during preview.");
                var inputHash=Hash(bytes);
                if(Hash(Read(input))!=inputHash) throw new IOException("Source bytes changed during preview.");
                var plan=new PlcExternalSourceImportPlan {Release=request.Release,ProjectFile=project,ProcessId=request.ProcessId,SoftwarePath=request.Software,FilePath=request.File,SourceName=name,Extension=name.Substring(name.LastIndexOf('.')).ToLowerInvariant(),ByteCount=bytes.Length,InputSha256=inputHash,InventoryCount=existing.Length};
                var fields=new[]{plan.Policy,request.Release,project,request.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),request.Software,plan.SessionKind,"",request.File,request.AllowedFile,name,inputHash,plan.ValidationScope}.Concat(existing);
                plan.PlanHash=Hash(Encoding.UTF8.GetBytes(string.Concat(fields.Select(Field))));
                if(request.ExpectedHash!="" && (!request.Confirm || request.ExpectedProject=="" || !string.Equals(request.ExpectedHash,plan.PlanHash,StringComparison.Ordinal))) throw new ArgumentException("Reviewed validation requires confirm=true, expectedProjectFile and unchanged expectedPlanHash.");
                return plan;
            }
        }
    }
}
