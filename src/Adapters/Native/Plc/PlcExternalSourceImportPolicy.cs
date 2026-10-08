using System;
using TiaMcp.Adapters.Contracts;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.Adapters
{
    internal sealed class PlcExternalSourceImportRequest
    {
        internal string Release="", Project="", Software="", Group="", File="", AllowedFile="", ExpectedHash="", ExpectedProject="";
        internal int ProcessId;
        internal bool DryRun=true, Confirm;
    }
    internal static class PlcExternalSourceImportPolicy
    {
        internal const int MaximumFileBytes=4*1024*1024;
        internal const string ApplyBlock=TiaMcp.Adapters.Contracts.ExternalSourceImportContract.ApplyBlock;
        private static string Hash(byte[] bytes) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        private static string Field(string value)=>value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+value;
        internal static string ValidateFile(string file,string parameter="filePath")
        {
            if(file==null || file.Length>1024) throw new AdapterPreconditionException("Use a canonical bounded Windows drive file path with backslashes.",parameter);
            file=file.Replace('/', '\\');
            string canonical;
            try { canonical=MutationIdentityPolicy.AbsoluteFile(file); }
            catch(ArgumentException ex) { throw new AdapterPreconditionException(ex.Message,parameter,true,ex); }
            // Local drive only; UNC, device namespaces, redundant separators and traversal are refused.
            if(canonical.Length<4 || canonical[1]!=':' || canonical!=file) throw new AdapterPreconditionException("Only a canonical absolute local Windows drive file path is allowed.",parameter);
            var name=canonical.Substring(canonical.LastIndexOf('\\')+1);
            var dot=name.LastIndexOf('.');
            if(name.Length>128 || dot<=0 || !new[]{".scl",".awl",".db",".udt"}.Contains(name.Substring(dot).ToLowerInvariant())) throw new AdapterPreconditionException("One SCL, AWL, DB or UDT source file is required.",parameter);
            return canonical;
        }
        internal static void ValidateOptions(PlcExternalSourceImportRequest request)
        {
            if(!request.DryRun) throw new AdapterPreconditionException(ApplyBlock,"import-plan",false);
            if(!new[]{"14sp1","15.1","16","17","18","19","20","21"}.Contains(request.Release)) throw new AdapterPreconditionException("Unknown exact release; V14/V15 are not aliases.","release");
            if(request.Group!="") throw new AdapterPreconditionException("Only groupPath empty string denotes the supported external-source root.","groupPath");
            request.File=ValidateFile(request.File);
            request.AllowedFile=ValidateFile(request.AllowedFile,"allowedFilePath");
            if(request.AllowedFile!=request.File) throw new AdapterPreconditionException("filePath must exactly match the single allowedFilePath, including casing.","allowedFilePath");
        }
        internal static FileStream OpenLocked(string path)
        {
            ValidateFile(path);
            if(Path.DirectorySeparatorChar!='\\') throw new PlatformNotSupportedException("Native input validation requires Windows; no host-dependent path reinterpretation.");
            var info=new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path));
            if(!info.Exists || (info.Attributes & FileAttributes.Directory)!=0) throw new AdapterPreconditionException("An existing regular source file is required.","filePath");
            for(FileSystemInfo? item=info;item!=null;item=item is DirectoryInfo dir ? dir.Parent : ((FileInfo)item).Directory)
                if((item.Attributes & FileAttributes.ReparsePoint)!=0) throw new AdapterPreconditionException("Source path ancestry contains a reparse point.","filePath");
            // Reuse the reviewed read-only share: hold against write/delete for the whole validation.
            return TiaOpenness.Shared.NativeInputPolicy.OpenRead(path,"filePath");
        }
        private static byte[] Read(Stream input)
        {
            if(!input.CanRead || !input.CanSeek || input.Length<1 || input.Length>MaximumFileBytes) throw new AdapterPreconditionException("Source must be a nonempty bounded seekable regular input.","filePath");
            input.Position=0; var length=input.Length;
            using(var memory=new MemoryStream())
            {
                var buffer=new byte[8192];int count;
                while((count=input.Read(buffer,0,buffer.Length))>0)
                {
                    if(memory.Length+count>MaximumFileBytes) throw new AdapterPreconditionException("Source exceeds byte budget.","filePath");
                    for(int i=0;i<count;i++) if((buffer[i]<32 && buffer[i]!=9 && buffer[i]!=10 && buffer[i]!=13) || buffer[i]>126) throw new AdapterPreconditionException("Wrapper ASCII subset refuses BOM, high bytes, NUL and other controls; no transcoding.","filePath");
                    memory.Write(buffer,0,count);
                }
                if(memory.Length!=length || input.Length!=length) throw new IOException("Source length changed during validation.");
                return memory.ToArray();
            }
        }
        private static string[] Inventory(Func<IEnumerable<string>> inventory,string name)
        {
            var items=inventory().Take(4097).ToArray();
            if(items.Length>4096) throw new AdapterPreconditionException("Root inventory exceeds 4096; truncated inventory is never accepted.","softwarePath");
            if(items.Any(x=>string.IsNullOrWhiteSpace(x) || x.Length>1024 || x.IndexOfAny(new[]{'\\','/','\0'})>=0)) throw new AdapterPreconditionException("Root source identity is incomplete or invalid.","softwarePath");
            if(items.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=items.Length) throw new AdapterPreconditionException("Ambiguous root source identities.","softwarePath");
            Func<string,string> stem=x=>x.LastIndexOf('.')>0 ? x.Substring(0,x.LastIndexOf('.')) : x;
            if(items.Any(x=>string.Equals(x,name,StringComparison.OrdinalIgnoreCase) || string.Equals(stem(x),stem(name),StringComparison.OrdinalIgnoreCase))) throw new AdapterPreconditionException("Exact/case-fold/stem source collision; no overwrite, alternate name or delete is allowed.","filePath");
            return items.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        }
        internal static PlcExternalSourceImportPlan Plan(PlcExternalSourceImportRequest request,Func<string,Stream> openLocked,Func<IEnumerable<string>> inventory,Action recheck)
        {
            ValidateOptions(request);
            var project=MutationIdentityPolicy.AbsoluteFile(request.Project);
            if(project!=request.Project || request.ProcessId<=0 || string.IsNullOrWhiteSpace(request.Software)) throw new AdapterPreconditionException("Exact project, positive process ID and software identity required.","softwarePath");
            if(request.ExpectedProject!="") MutationIdentityPolicy.RequireSameProject(request.ExpectedProject,project);
            using(var input=openLocked(request.File))
            {
                var bytes=Read(input); var name=request.File.Substring(request.File.LastIndexOf('\\')+1);
                var existing=Inventory(inventory,name);
                recheck();
                var second=Inventory(inventory,name);
                if(!existing.SequenceEqual(second,StringComparer.Ordinal)) throw new AdapterPreconditionException("Root source inventory changed during preview.","softwarePath",false);
                var inputHash=Hash(bytes);
                if(Hash(Read(input))!=inputHash) throw new IOException("Source bytes changed during preview.");
                var plan=new PlcExternalSourceImportPlan {Release=request.Release,ProjectFile=project,ProcessId=request.ProcessId,SoftwarePath=request.Software,FilePath=request.File,SourceName=name,Extension=name.Substring(name.LastIndexOf('.')).ToLowerInvariant(),ByteCount=bytes.Length,InputSha256=inputHash,InventoryCount=existing.Length};
                var fields=new[]{plan.Policy,request.Release,project,request.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),request.Software,plan.SessionKind,"",request.File,request.AllowedFile,name,inputHash,plan.ValidationScope}.Concat(existing);
                plan.PlanHash=Hash(Encoding.UTF8.GetBytes(string.Concat(fields.Select(Field))));
                if(request.ExpectedHash!="" && (!request.Confirm || request.ExpectedProject=="" || !string.Equals(request.ExpectedHash,plan.PlanHash,StringComparison.Ordinal))) throw new AdapterPreconditionException("Reviewed validation requires confirm=true, expectedProjectFile and unchanged expectedPlanHash.","expectedPlanHash");
                return plan;
            }
        }
    }
}
