using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.Adapters
{
    internal static class PlcSpecialExportPolicy
    {
        internal static bool Documented(string release,string kind) => release=="16" || release=="17" || release=="18" || release=="19" || release=="20" || (release=="21" && kind=="watch-table");
        internal static string Hash(params string[] fields)
        {
            // Length-prefix every field, avoiding delimiter ambiguity in project/object names.
            var builder=new StringBuilder(); foreach(var field in fields) builder.Append(field.Length).Append(':').Append(field);
            using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()))).Replace("-","").ToLowerInvariant();
        }
        private static FileInfo Output(string path)
        {
            path=TiaOpenness.Shared.NativeInputPolicy.NormalizeSeparators(path);
            if(!Path.IsPathRooted(path) || !string.Equals(Path.GetFullPath(path),path,StringComparison.OrdinalIgnoreCase)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Canonical absolute export path required.");
            var file=PlcFoundationPolicy.XmlOutput(path);
            var name=file.Name;
            if(name.EndsWith(".",StringComparison.Ordinal) || name.EndsWith(" ",StringComparison.Ordinal) || name.IndexOfAny(new[]{'<','>',':','"','/','\\','|','?','*'})>=0 || System.Linq.Enumerable.Any(name,c=>c<32)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Export filename must be an ordinary Windows filename, without streams or aliases.");
            var stem=name.Split('.')[0].TrimEnd(' ','.').ToUpperInvariant();
            if(stem=="CON" || stem=="PRN" || stem=="AUX" || stem=="NUL" || stem=="CONIN$" || stem=="CONOUT$" || ((stem.StartsWith("COM",StringComparison.Ordinal) || stem.StartsWith("LPT",StringComparison.Ordinal)) && stem.Length==4 && "123456789¹²³".IndexOf(stem[3])>=0)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Reserved Windows device filename is not an export target.");
            if(Directory.Exists(file.FullName)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Export destination is an existing directory.");
            try { File.GetAttributes(file.FullName); throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Export destination already exists, including a link."); }
            catch(FileNotFoundException) /* swallow(env-probe): a missing destination is the required result of the no-overwrite file existence probe */ { }
            for(var ancestor=file.Directory;ancestor!=null;ancestor=ancestor.Parent)
                if((ancestor.Attributes & FileAttributes.ReparsePoint)!=0) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Export directory ancestry cannot contain links or reparse points.");
            return file;
        }
        internal static PlcSpecialExportResult Run(string kind,string release,string project,string software,string path,string output,bool consistent,bool dryRun,string expectedPlanHash,Action requireOffline,Action<FileInfo> export,Func<FileInfo,Action<FileInfo>,string?>? publish=null)
        {
            if(kind!="watch-table" && kind!="technology-object") throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Unknown special export kind.");
            PlcSupplementaryReadPolicy.RequireRelease(release,kind=="watch-table");
            if(PlcExchangePolicy.ObjectPath(path)!=path) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact canonical object path required.");
            if(string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(software)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact project and software identity required.");
            var destination=Output(output);
            var result=new PlcSpecialExportResult { Executed=!dryRun,ProjectFile=project,SoftwarePath=software,ObjectPath=path,OutputFile=destination.FullName,ReleaseKey=release,Kind=kind,
                Scope=kind=="watch-table"?PlcSupplementaryReadPolicy.WatchScope:PlcSupplementaryReadPolicy.TechnologyReadScope(release),
                MethodEvidence="static-sdk-signature-verified",SemanticsEvidence=Documented(release,kind)?"official-manual-source-candidate":"unverified",
                Status=!Documented(release,kind)?"semantics-unverified":!consistent?"inconsistent":"planned" };
            result.PlanHash=Hash(kind,release,project,software,path,destination.FullName,result.Scope,result.ExportOptions,result.MethodEvidence,result.SemanticsEvidence,result.Status);
            if(!consistent) TiaOpenness.Shared.NativeExportPolicy.RequireApply("inconsistent");
            if(dryRun) return result;
            if(expectedPlanHash!=result.PlanHash) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Execution requires the exact plan hash from a fresh preview.");
            TiaOpenness.Shared.NativeExportPolicy.RequireApply(result.Status);
            requireOffline();
            Output(destination.FullName);
            try
            {
                var recovery=(publish??PlcExportPublication.Publish)(destination,export);
                result.Status="exported";
                if(recovery!=null) result.Evidence["recoveryDirectory"]=recovery;
            }
            catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch(Exception error)
            {
                result.Status="failed"; result.RequiresSessionReset=true;
                foreach(var name in new[]{"outputFile","stagedFile","recoveryDirectory","exportPhase","stagedSha256"})
                    if(error.Data[name] is string value) result.Evidence[name]=value;
            }
            return result;
        }
    }
}
