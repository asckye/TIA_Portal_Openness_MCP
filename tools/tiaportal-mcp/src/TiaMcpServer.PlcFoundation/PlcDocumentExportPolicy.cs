using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcDocumentExportResult
    {
        public bool Executed { get; set; }
        public string ReleaseKey { get; set; } = "";
        public string ProjectFile { get; set; } = "";
        public string SoftwarePath { get; set; } = "";
        public string BlockPath { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public string Language { get; set; } = "";
        public string PlanHash { get; set; } = "";
        public string Status { get; set; } = "planned";
        public bool RequiresSessionReset { get; set; }
        public string Options { get; set; } = "native-default-two-argument-overload";
        public string[] Files { get; set; } = new string[0];
        public string Evidence { get; set; } = "official-manual-source-candidate; exact-sdk-build/native-acceptance-pending; no-cross-version-roundtrip-claim";
        public string RecoveryDirectory { get; set; } = "";
    }
    internal static class PlcDocumentExportPolicy
    {
        internal const long MaxFileBytes=64L*1024*1024;
        internal static T Exact<T>(IEnumerable<IEnumerable<T>> groups,Func<T,string> path,string selected)
        {
            int groupCount=0,itemCount=0,matches=0; T found=default!;
            foreach(var group in groups)
            {
                if(++groupCount>1024) throw new NotSupportedException("Document selection exceeds the complete 1024-group bound.");
                foreach(var item in group)
                {
                    if(++itemCount>10000) throw new NotSupportedException("Document selection exceeds the complete 10000-block bound.");
                    if(path(item)==selected) {found=item; matches++;}
                }
            }
            if(matches!=1) throw new ArgumentException("Document selection must resolve exactly one block within the complete bounded inventory.");
            return found;
        }
        internal static void RequireRelease(string release)
        { if(release!="20" && release!="21") throw new NotSupportedException("Document export preview is restricted by policy to exact V20/V21; older API applicability is not inferred."); }
        internal static void Name(string value)
        {
            if(string.IsNullOrWhiteSpace(value) || value.Length>120 || value.EndsWith(".") || value.EndsWith(" ") || value.Any(c=>c<32 || "<>:\"/\\|?*".Contains(c))) throw new ArgumentException("Ordinary bounded Windows filename required.");
            var stem=value.Split('.')[0].TrimEnd(' ','.').ToUpperInvariant();
            if(new[]{"CON","PRN","AUX","NUL","CONIN$","CONOUT$"}.Contains(stem) || ((stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem.Length==4 && "123456789¹²³".Contains(stem[3]))) throw new ArgumentException("Reserved device filename.");
        }
        internal static DirectoryInfo Destination(string path)
        {
            if(!Path.IsPathRooted(path) || Path.GetFullPath(path)!=path || path.Length>240) throw new ArgumentException("Canonical absolute bounded output directory required.");
            var directory=new DirectoryInfo(path); Name(directory.Name);
            if(directory.Parent==null || !directory.Parent.Exists) throw new ArgumentException("Existing output parent required; preview creates nothing.");
            try { File.GetAttributes(path); throw new ArgumentException("Output must be a new directory; overwrite and reuse refused."); } catch(FileNotFoundException) { } catch(DirectoryNotFoundException) { }
            for(var parent=directory.Parent;parent!=null;parent=parent.Parent)
                if((parent.Attributes & FileAttributes.ReparsePoint)!=0) throw new ArgumentException("Output ancestors cannot be links/reparse points.");
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
            if(string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(software) || PlcExchangePolicy.ObjectPath(block)!=block) throw new ArgumentException("Exact project/software/canonical block identity required.");
            if(language!="LAD" && language!="DB") throw new NotSupportedException("Bounded document route admits LAD and DB only; other language and mixed-language semantics are unresolved here.");
            var destination=Destination(output); var name=Uri.UnescapeDataString(block.Split('/').Last()); Name(name);
            var files=new[]{name+".s7dcl",name+".s7res"};
            var result=new PlcDocumentExportResult { ReleaseKey=release,ProjectFile=project,SoftwarePath=software,BlockPath=block,OutputDirectory=output,Language=language,Files=files,Status=consistent?"planned":"inconsistent" };
            result.PlanHash=Hash(release,project,software,block,output,language,result.Status,result.Options,string.Join("|",files));
            if(dryRun) return result;
            if(expectedHash!=result.PlanHash) throw new ArgumentException("Execution requires exact fresh preview plan hash.");
            if(!consistent) throw new NotSupportedException("Inconsistent block is preview only.");
            // The production boundary is Windows-only; test injection exercises file policy without Siemens.
            if(move==null && Environment.OSVersion.Platform!=PlatformID.Win32NT) throw new PlatformNotSupportedException("Document pair publication requires the Windows worker.");
            requireOffline(); Destination(output);
            var stage=new DirectoryInfo(Path.Combine(destination.Parent!.FullName,".tia-documents-"+Guid.NewGuid().ToString("N")));
            result.Executed=true;
            try
            {
                stage.Create();
                if((stage.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("Staging directory cannot be a link.");
                var reported=export(stage,name);
                var published=ValidatePair(stage,files,reported,release=="21");
                Destination(output);
                (move??((from,to)=>Directory.Move(from.FullName,to.FullName)))(stage,destination);
                result.Files=published; result.Status="exported";
            }
            catch(Exception)
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
                var file=new FileInfo(path);
                if(!file.Exists || file.Length<=0 || file.Length>MaxFileBytes || (file.Attributes & (FileAttributes.Directory|FileAttributes.ReparsePoint))!=0) throw new IOException("Document must be a bounded nonempty regular file.");
            }
            return names;
        }
    }
}
