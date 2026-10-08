using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.Adapters
{
    internal static class PlcDocumentExportPolicy
    {
        internal const long MaxFileBytes=64L*1024*1024;
        internal static T Exact<T>(IEnumerable<IEnumerable<T>> groups,Func<T,string> path,string selected)
        {
            int groupCount=0,itemCount=0,matches=0; T found=default!;
            foreach(var group in groups)
            {
                if(++groupCount>1024) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Document selection exceeds the complete 1024-group bound.","export-plan",false);
                foreach(var item in group)
                {
                    if(++itemCount>10000) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Document selection exceeds the complete 10000-block bound.","export-plan",false);
                    if(path(item)==selected) {found=item; matches++;}
                }
            }
            if(matches!=1) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Document selection must resolve exactly one block within the complete bounded inventory.");
            return found;
        }
        internal static void RequireRelease(string release)
        { if(release!="20" && release!="21") throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Document export preview is restricted by policy to exact V20/V21; older API applicability is not inferred.","export-plan",false); }
        internal static void Name(string value)
        {
            if(string.IsNullOrWhiteSpace(value) || value.Length>120 || value.EndsWith(".") || value.EndsWith(" ") || value.Any(c=>c<32 || "<>:\"/\\|?*".Contains(c))) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Ordinary bounded Windows filename required.");
            var stem=value.Split('.')[0].TrimEnd(' ','.').ToUpperInvariant();
            if(new[]{"CON","PRN","AUX","NUL","CONIN$","CONOUT$"}.Contains(stem) || ((stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem.Length==4 && "123456789¹²³".Contains(stem[3]))) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Reserved device filename.");
        }
        internal static DirectoryInfo Destination(string path)
        {
            path=TiaOpenness.Shared.NativeInputPolicy.NormalizeSeparators(path);
            if(!Path.IsPathRooted(path) || Path.GetFullPath(path)!=path || path.Length>240) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Canonical absolute bounded output directory required.");
            var directory=new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)); Name(directory.Name);
            if(directory.Parent==null || !directory.Parent.Exists) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Existing output parent required; preview creates nothing.");
            try { File.GetAttributes(path); throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Output must be a new directory; overwrite and reuse refused."); } catch(FileNotFoundException) /* swallow(env-probe): a missing output path is the required result of the new-directory existence probe */ { } catch(DirectoryNotFoundException) /* swallow(env-probe): a missing output path is the required result of the new-directory existence probe */ { }
            for(var parent=directory.Parent;parent!=null;parent=parent.Parent)
                if((parent.Attributes & FileAttributes.ReparsePoint)!=0) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Output ancestors cannot be links/reparse points.");
            return directory;
        }
        internal static string Hash(params string[] fields)
        {
            var value=new StringBuilder(); foreach(var field in fields) value.Append(field.Length).Append(':').Append(field);
            using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToString()))).Replace("-","").ToLowerInvariant();
        }
        internal static PlcDocumentExportResult Run(string release,string project,string software,string block,string output,string language,bool consistent,bool dryRun,string expectedHash,Action requireOffline,Func<DirectoryInfo,string,string[]> export,Action<DirectoryInfo,DirectoryInfo>? move=null)
        {
            RequireRelease(release);
            if(string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(software) || PlcExchangePolicy.ObjectPath(block)!=block) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact project/software/canonical block identity required.");
            if(language!="LAD" && language!="DB") throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Bounded document route admits LAD and DB only; other language and mixed-language semantics are unresolved here.","export-plan",false);
            output=TiaOpenness.Shared.NativeInputPolicy.NormalizeSeparators(output);
            var destination=Destination(output); var name=Uri.UnescapeDataString(block.Split('/').Last()); Name(name);
            var files=new[]{name+".s7dcl",name+".s7res"};
            var result=new PlcDocumentExportResult { ReleaseKey=release,ProjectFile=project,SoftwarePath=software,BlockPath=block,OutputDirectory=output,Language=language,Files=files,Status=consistent?"planned":"inconsistent" };
            result.PlanHash=Hash(release,project,software,block,output,language,result.Status,result.Options,string.Join("|",files));
            if(dryRun) return result;
            if(expectedHash!=result.PlanHash) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Execution requires exact fresh preview plan hash.");
            TiaOpenness.Shared.NativeExportPolicy.RequireApply(result.Status);
            // The production boundary is Windows-only; test injection exercises file policy without Siemens.
            if(move==null && Environment.OSVersion.Platform!=PlatformID.Win32NT) throw new PlatformNotSupportedException("Document pair publication requires the Windows worker.");
            requireOffline(); Destination(output);
            var stage=new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(Path.Combine(destination.Parent!.FullName,".tia-documents-"+Guid.NewGuid().ToString("N"))));
            result.Executed=true; bool exportReturned=false;
            try
            {
                stage.Create();
                if((stage.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("Staging directory cannot be a link.");
                var reported=export(stage,name);
                exportReturned=true;
                var published=ValidatePair(stage,files,reported,release=="21");
                Destination(output);
                (move??((from,to)=>Directory.Move(from.FullName,to.FullName)))(stage,destination);
                result.Files=published; result.Status="exported";
            }
            catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) when(!exportReturned) { throw; }
            catch(Exception) /* swallow(native-fallback): failed export or publication retains staging evidence and returns failed status with a session reset */
            {
                result.Status="failed"; result.RequiresSessionReset=true; result.RecoveryDirectory=stage.FullName;
            }
            return result;
        }
        internal static string[] ValidatePair(DirectoryInfo stage,string[] expected,string[] reported,bool resourceOptional=false)
        {
            if(reported==null || (reported.Length!=2 && !(resourceOptional && reported.Length==1)) || reported.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=reported.Length) throw new IOException("Native result did not report the bounded document set.");
            var names=reported.Length==1?expected.Take(1).ToArray():expected;
            var paths=names.Select(n=>Path.Combine(stage.FullName,n)).ToArray();
            if(!reported.OrderBy(p=>p,StringComparer.Ordinal).SequenceEqual(paths.OrderBy(p=>p,StringComparer.Ordinal),StringComparer.Ordinal)) throw new IOException("Native document paths differ from the expected pair.");
            var actual=Directory.EnumerateFileSystemEntries(stage.FullName).Take(3).ToArray();
            if(actual.Length!=paths.Length || !actual.OrderBy(p=>p,StringComparer.Ordinal).SequenceEqual(paths.OrderBy(p=>p,StringComparer.Ordinal),StringComparer.Ordinal)) throw new IOException("Unexpected extra or missing staged documents.");
            foreach(var path in paths)
            {
                var file=new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path));
                if(!file.Exists || file.Length<=0 || file.Length>MaxFileBytes || (file.Attributes & (FileAttributes.Directory|FileAttributes.ReparsePoint))!=0) throw new IOException("Document must be a bounded nonempty regular file.");
            }
            return names;
        }
    }
}
