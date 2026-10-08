using System;
using TiaMcp.Adapters.Contracts;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.Adapters
{
    internal static class PlcExternalSourceWorkflowPolicy
    {
        internal static void RequireRoot(string group)
        { if(group != "") throw new AdapterPreconditionException("External-source subgroups are not supported by Openness; groupPath must be empty.","groupPath"); }
        internal static void RequireSourceName(string name)
        { if(string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[]{'/', '\\', '\0'}) >= 0) throw new AdapterPreconditionException("Use the exact external source name returned by GetPlcExternalSources, including its extension.","externalSourceName"); }
        private static string Hash(byte[] bytes)
        { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static string HashFields(IEnumerable<string> values) => Hash(Encoding.UTF8.GetBytes(string.Concat(values.Select(x => x.Length + ":" + x))));
        private static IEnumerable<string> Identity(PlcExternalSourceWorkflowResult result) => new[]{result.Operation,result.Release,result.ProjectFile,result.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),result.SoftwarePath,result.GroupPath,result.SourceName};
        private static void RequireReview(PlcExternalSourceWorkflowResult result,bool dryRun,string expectedPlanHash,bool confirm,string expectedProjectFile)
        {
            if(dryRun) return;
            MutationIdentityPolicy.RequireSameProject(expectedProjectFile,result.ProjectFile);
            if(!confirm || expectedPlanHash!=result.PlanHash) throw new AdapterPreconditionException("Execution requires confirm=true and expectedPlanHash from the unchanged preview.","expectedPlanHash");
        }
        private static void Execute(PlcExternalSourceWorkflowResult result,Action action)
        {
            result.Attempted=true;
            try { action(); result.Executed=true; result.Status="completed"; }
            catch(Exception ex) { result.Status="outcome-unknown"; result.RequiresSessionReset=true; result.Error=ex.GetType().Name+": "+ex.Message; }
        }
        internal static string InputHash(Stream stream)
        {
            if(!stream.CanRead || !stream.CanSeek || stream.Length==0) throw new AdapterPreconditionException("An existing nonempty source file is required.","filePath");
            // The official GenerateBlocksFromSource contract admits ASCII external files.
            // Do not transcode source text or reject valid large sources with an arbitrary size limit.
            stream.Position=0;
            var buffer=new byte[8192]; int read;
            using(var sha=SHA256.Create())
            {
                while((read=stream.Read(buffer,0,buffer.Length))!=0)
                {
                    for(var i=0;i<read;i++) if(buffer[i]>127 || buffer[i]==0) throw new AdapterPreconditionException("External-source generation requires ASCII text; save this source as ASCII without a BOM.","filePath");
                    sha.TransformBlock(buffer,0,read,buffer,0);
                }
                sha.TransformFinalBlock(new byte[0],0,0);
                return BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
            }
        }
        internal static PlcExternalSourceImportResult Import(PlcExternalSourceImportResult result,Stream input,bool dryRun,string expectedPlanHash,bool confirm,string expectedProjectFile,Func<IEnumerable<string>> readNames,Action check,Func<string,string,string> create)
        {
            RequireSourceName(result.RequestedSourceName);
            if(!new[]{".scl",".awl",".db",".udt"}.Contains(Path.GetExtension(result.RequestedSourceName).ToLowerInvariant())) throw new AdapterPreconditionException("External sources must use .scl, .awl, .db or .udt.","filePath");
            result.SourceName=result.RequestedSourceName;
            result.InputSha256=InputHash(input); result.ByteCount=input.Length;
            check(); var before=readNames().OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            if(before.Contains(result.RequestedSourceName,StringComparer.OrdinalIgnoreCase)) throw new AdapterPreconditionException("An external source with this name already exists; choose another source name/file or explicitly delete it first.","filePath");
            result.PlanHash=HashFields(Identity(result).Concat(new[]{result.FilePath,result.InputSha256}).Concat(before));
            RequireReview(result,dryRun,expectedPlanHash,confirm,expectedProjectFile);
            if(dryRun) return result;
            check();
            if(!before.SequenceEqual(readNames().OrderBy(x=>x,StringComparer.Ordinal),StringComparer.Ordinal) || InputHash(input)!=result.InputSha256) throw new AdapterPreconditionException("Source file or target inventory changed after preview.","softwarePath",false);
            Execute(result,()=> {
                result.SourceName=create(result.RequestedSourceName,result.FilePath);
                RequireSourceName(result.SourceName);
                result.SourceNamesAfter=readNames().OrderBy(x=>x,StringComparer.Ordinal).ToArray();
                if(!result.SourceNamesAfter.Contains(result.SourceName,StringComparer.Ordinal)) throw new InvalidOperationException("Created source was not found in the target collection.");
            });
            return result;
        }
        private static string[] ObjectFields(PlcExternalSourceObject item) => new[]{item.Kind,item.Path,item.Name,item.TypeName,item.ProgrammingLanguage,item.IsConsistent.ToString(),item.ModifiedDate.ToString("O")};
        internal static string ObjectKey(PlcExternalSourceObject item) => item.Kind+":"+item.Path;
        internal static PlcExternalSourceGenerationResult Generate(PlcExternalSourceGenerationResult result,bool dryRun,string expectedPlanHash,bool confirm,string expectedProjectFile,Func<PlcExternalSourceObject[]> readObjects,Action check,Action generate,Func<PlcExternalSourceObject[]> readGeneratedObjects,Func<Exception,bool> recoverableGenerationFailure)
        {
            RequireSourceName(result.SourceName);
            check(); result.ObjectsBefore=readObjects();
            result.GenerationOption=result.Release=="14sp1" ? "parameterless" : "None";
            result.ResultBasis=result.Release=="14sp1" ? "inventory-observation-only; native API returns void" : "native-returned-objects-with-path-readback";
            result.PlanHash=HashFields(Identity(result).Concat(new[]{result.SourceIdentity,result.GenerationOption}).Concat(result.ObjectsBefore.OrderBy(ObjectKey,StringComparer.Ordinal).SelectMany(ObjectFields)));
            RequireReview(result,dryRun,expectedPlanHash,confirm,expectedProjectFile);
            if(dryRun) return result;
            check();
            var fresh=readObjects();
            if(HashFields(fresh.OrderBy(ObjectKey,StringComparer.Ordinal).SelectMany(ObjectFields))!=HashFields(result.ObjectsBefore.OrderBy(ObjectKey,StringComparer.Ordinal).SelectMany(ObjectFields))) throw new AdapterPreconditionException("PLC block/type inventory changed after preview.","softwarePath",false);
            result.Attempted=true;
            try { generate(); }
            catch(Exception ex)
            {
                // Only the native generation call has Siemens' documented rollback
                // contract. Readback after a successful call is a separate phase.
                var recoverable=recoverableGenerationFailure(ex);
                result.Status=recoverable ? "failed" : "outcome-unknown";
                result.RequiresSessionReset=!recoverable;
                result.Error=ex.GetType().Name+": "+ex.Message;
                return result;
            }
            Execute(result,()=> {
                result.GeneratedObjects=readGeneratedObjects();
                result.ObjectsAfter=readObjects();
                var previous=result.ObjectsBefore.ToDictionary(ObjectKey,StringComparer.Ordinal);
                result.ObservedChanges=result.ObjectsAfter.Where(item=>!previous.TryGetValue(ObjectKey(item),out var old) || !ObjectFields(old).SequenceEqual(ObjectFields(item),StringComparer.Ordinal)).ToArray();
                foreach(var generated in result.GeneratedObjects)
                    if(!result.ObjectsAfter.Any(item=>ObjectKey(item)==ObjectKey(generated) && item.Name==generated.Name)) throw new InvalidOperationException("A native generated object could not be read back from the PLC.");
            });
            return result;
        }
    }
}
