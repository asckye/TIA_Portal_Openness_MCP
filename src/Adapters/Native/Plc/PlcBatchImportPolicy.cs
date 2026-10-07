using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.PlcFoundation
{
    public sealed class PlcBatchImportRequest
    {
        internal string Release="",Project="",Software="",Directory="",BlockGroup="",TypeGroup="",TagGroup="",Regex="",ExpectedHash="",ExpectedProject="",TechnologyGroup="";
        internal int ProcessId,MaxItems=128;
        internal bool Program,Overwrite,CompileAfter,Confirm,DryRun=true,StopOnImportFailure=true;
        internal string[] Order=new string[0];
        internal string InputParameter=>Program ? "sourceDir" : "dir";
        public PlcBatchImportRequest(string software="",string directory="",string blockGroup="",string typeGroup="",string tagGroup="",string regex="",bool program=false,bool overwrite=false,bool compileAfter=false,bool confirm=false,bool dryRun=true,bool stopOnImportFailure=true,string[]? order=null,string expectedHash="",string expectedProject="",string technologyGroup="",int maxItems=128)
        {
            Software=software;Directory=directory;BlockGroup=blockGroup;TypeGroup=typeGroup;TagGroup=tagGroup;Regex=regex;Program=program;Overwrite=overwrite;CompileAfter=compileAfter;Confirm=confirm;DryRun=dryRun;StopOnImportFailure=stopOnImportFailure;Order=order ?? new string[0];ExpectedHash=expectedHash;ExpectedProject=expectedProject;TechnologyGroup=technologyGroup;MaxItems=maxItems;
        }
    }
    public sealed class PlcBatchImportKnownFailure : Exception
    {
        public PlcBatchImportKnownFailure(string message) : base(message) { }
    }
    internal sealed class PlcBatchImportDependency
    {
        internal string Name="",Kind="";
    }
    internal static class PlcBatchImportPolicy
    {
        internal const long MaximumFileBytes=4*1024*1024,MaximumTotalBytes=32*1024*1024;
        private static string Hash(byte[] bytes) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        private static string Field(string value)=>value.Length+":"+value;
        private static string Identity(PlcBatchImportObject x)=>Field(x.Kind)+Field(x.GroupPath)+Field(x.Name)+Field(x.Number.HasValue ? x.Number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "automatic");
        private static string Namespace(string kind)=>kind=="UDT" ? "type" : kind=="TagTable" ? "table" : "block";
        private static void SafePath(FileSystemInfo entry,string parameter)
        {
            for(FileSystemInfo? current=entry;current!=null;current=current is DirectoryInfo dir ? dir.Parent : ((FileInfo)current).Directory)
                if((current.Attributes & FileAttributes.ReparsePoint)!=0) throw new AdapterPreconditionException("Input path ancestry contains a link/reparse point.",parameter);
        }
        private static string[] Scan(PlcBatchImportRequest request)
        {
            if(!Path.IsPathRooted(request.Directory)) throw new AdapterPreconditionException("Input directory must be absolute and existing.",request.InputParameter);
            var root=new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(request.Directory)); if(!root.Exists) throw new AdapterPreconditionException("Input directory does not exist.",request.InputParameter); SafePath(root,request.InputParameter);
            Regex regex;
            try { regex=new Regex(request.Regex,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)); }
            catch(ArgumentException ex) { throw new AdapterPreconditionException("regexName must be a valid bounded regular expression.","regexName",true,ex); }
            var pending=new Stack<DirectoryInfo>(); pending.Push(root); var files=new List<string>(); int entries=0,dirs=0;
            var aliases=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while(pending.Count>0)
            {
                var dir=pending.Pop(); if(++dirs>1024) throw new AdapterPreconditionException("Directory count exceeds bounded scan.",request.InputParameter);
                foreach(var entry in dir.EnumerateFileSystemInfos())
                {
                    if(++entries>4096) throw new AdapterPreconditionException("Entry count exceeds bounded scan.",request.InputParameter);
                    SafePath(entry,request.InputParameter); if(!aliases.Add(entry.FullName)) throw new AdapterPreconditionException("Case-insensitive input path collision.",request.InputParameter);
                    if(entry is DirectoryInfo child) {if(request.Program) pending.Push(child);continue;}
                    if(!entry.Extension.Equals(".xml",StringComparison.OrdinalIgnoreCase) || !regex.IsMatch(entry.Name)) continue;
                    files.Add(entry.FullName); if(files.Count>request.MaxItems) throw new AdapterPreconditionException("Selected files exceed maxItems; never truncated.","maxItems");
                }
            }
            return files.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        }
        internal static void ValidateOptions(PlcBatchImportRequest request)
        {
            TiaOpenness.Shared.NativeExportPolicy.RequireBatchOptions(request.CompileAfter,request.StopOnImportFailure,request.TechnologyGroup);
            if(request.MaxItems<1 || request.MaxItems>256) throw new AdapterPreconditionException("maxItems must be 1..256.","maxItems");
            if(request.Regex.Length>1024) throw new AdapterPreconditionException("Regex exceeds bounded length.","regexName");
            if(!new[]{"14sp1","15.1","16","17","18","19","20","21"}.Contains(request.Release)) throw new AdapterPreconditionException("Unknown exact release.","releaseKey");
            if(!request.DryRun)
            {
                if(!request.Confirm) throw new AdapterPreconditionException("Apply requires confirm=true.","confirm");
                if(!string.Equals(request.ExpectedProject,request.Project,StringComparison.Ordinal)) throw new AdapterPreconditionException("Apply requires the exact expected project identity.","expectedProjectFile");
                if(request.Order.Length==0) throw new AdapterPreconditionException("Apply requires an explicit complete importOrder.","importOrder");
                if(request.ExpectedHash.Length!=64 || request.ExpectedHash.Any(c=>!"0123456789abcdef".Contains(c))) throw new AdapterPreconditionException("Apply requires expectedPlanHash from a reviewed preview.","expectedPlanHash");
            }
        }
        private static PlcBatchImportObject Parse(Stream stream,PlcBatchImportRequest request,out PlcBatchImportDependency[] dependencies)
        {
            stream.Position=0;
            var settings=new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaximumFileBytes,CloseInput=false};
            // Enforce depth before materializing any XML tree.
            XDocument document;
            try
            {
                using(var reader=XmlReader.Create(stream,settings)) while(reader.Read()) if(reader.Depth>64) throw new AdapterPreconditionException("XML nesting exceeds 64.",request.InputParameter);
                stream.Position=0;
                using(var reader=XmlReader.Create(stream,settings)) document=XDocument.Load(reader);
            }
            catch(XmlException ex) { throw new AdapterPreconditionException("Input must be valid bounded XML.","dir",true,ex); }
            if(document.Root==null || document.Root.Name!=XName.Get("Document")) throw new AdapterPreconditionException("Only ordinary native Document XML is supported.",request.InputParameter);
            var roots=document.Root.Elements().Where(x=>x.Name.LocalName!="Engineering" && x.Name.LocalName!="DocumentInfo").ToArray();
            if(roots.Length!=1) throw new AdapterPreconditionException("Exactly one ordinary root object per file is required; no roots are skipped.",request.InputParameter);
            var node=roots[0]; var kinds=new Dictionary<string,string> {{"SW.Blocks.FC","FC"},{"SW.Blocks.FB","FB"},{"SW.Blocks.OB","OB"},{"SW.Blocks.GlobalDB","GlobalDB"},{"SW.Blocks.InstanceDB","InstanceDB"},{"SW.Types.PlcStruct","UDT"},{"SW.Tags.PlcTagTable","TagTable"}};
            if(node.Name.NamespaceName!="" || !kinds.TryGetValue(node.Name.LocalName,out var kind) || (!request.Program && Namespace(kind)!="block")) throw new AdapterPreconditionException("Unknown or out-of-scope XML root; nothing was skipped.","dir",false);
            var engineering=document.Root.Elements("Engineering").ToArray();
            var expected=request.Release=="14sp1" ? "V14 SP1" : "V"+request.Release;
            // Original V14 markers cannot establish SP1. Exact producer only, no inferred upgrades.
            if(engineering.Length!=1 || (string?)engineering[0].Attribute("version")!=expected) throw new AdapterPreconditionException("XML must identify the exact reviewed release producer; no version rewrite or cross-release inference.","dir",false);
            var attrs=node.Elements("AttributeList").ToArray(); if(attrs.Length!=1) throw new AdapterPreconditionException("Missing or ambiguous object AttributeList.",request.InputParameter);
            var names=attrs[0].Elements("Name").ToArray(); if(names.Length!=1 || string.IsNullOrWhiteSpace(names[0].Value)) throw new AdapterPreconditionException("Missing or ambiguous XML logical name.",request.InputParameter);
            if(document.Descendants().Any(x=>(x.Name.LocalName.IndexOf("Safety",StringComparison.OrdinalIgnoreCase)>=0 || x.Name.LocalName.IndexOf("KnowHow",StringComparison.OrdinalIgnoreCase)>=0 || x.Name.LocalName.IndexOf("Protection",StringComparison.OrdinalIgnoreCase)>=0) && !string.Equals(x.Value,"false",StringComparison.OrdinalIgnoreCase))) throw new AdapterPreconditionException("Protected/Safety XML is outside this candidate.","dir",false);
            var language=attrs[0].Elements("ProgrammingLanguage").Select(x=>x.Value).ToArray();
            if(Namespace(kind)=="block" && (language.Length!=1 || !new[]{"LAD","FBD","STL","SCL","DB"}.Contains(language[0]))) throw new AdapterPreconditionException("Block language is missing or not reviewed for ordinary import.","dir",false);
            if(request.Release=="14sp1" && language.Contains("SCL")) throw new AdapterPreconditionException("V14 SP1 SCL XML is interface-only and cannot restore a complete block.","dir",false);
            if(language.Contains("SCL") && !node.Descendants().Any(x=>x.Name.LocalName=="SW.Blocks.CompileUnit" && x.Descendants().Any(e=>e.Name.LocalName=="StructuredText" && e.HasElements))) throw new AdapterPreconditionException("SCL implementation is absent or unverified; interface-only input cannot restore a complete block.","dir",false);
            var numbers=attrs[0].Elements("Number").ToArray(); int? number=null;
            if(numbers.Length>1) throw new AdapterPreconditionException("Ambiguous block number.",request.InputParameter);
            if(Namespace(kind)!="block" && numbers.Length!=0) throw new AdapterPreconditionException("Number verification for non-block objects is outside this bounded candidate.","dir",false);
            if(numbers.Length==1) {int n;if(!int.TryParse(numbers[0].Value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out n) || n<0) throw new AdapterPreconditionException("Invalid fixed block number.",request.InputParameter);number=n;}
            // Recognized declarations only; this is deliberately not a complete dependency analyzer.
            var references=document.Descendants().Attributes().Where(a=>a.Name.LocalName=="Datatype" || a.Name.LocalName=="DataType").Select(a=>a.Value)
                .Where(v=>v.StartsWith("\"",StringComparison.Ordinal) && v.EndsWith("\"",StringComparison.Ordinal)).Select(v=>v.Substring(1,v.Length-2))
                .Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Select(x=>new PlcBatchImportDependency {Name=x,Kind="UDT"});
            dependencies=references.Concat(document.Descendants().Where(e=>e.Name.LocalName=="InstanceOfName").Select(e=>e.Value).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Select(x=>new PlcBatchImportDependency {Name=x,Kind="FB"})).OrderBy(x=>x.Kind+":"+x.Name,StringComparer.Ordinal).ToArray();
            return new PlcBatchImportObject {Name=names[0].Value,Kind=kind,Number=number,GroupPath=kind=="UDT" ? request.TypeGroup : kind=="TagTable" ? request.TagGroup : request.BlockGroup};
        }
        private static void Verify(PlcBatchImportObject[]? actual,PlcBatchImportObject planned)
        {
            if(actual==null || actual.Length!=1 || actual.Any(x=>x==null || x.Name!=planned.Name || x.Kind!=planned.Kind || x.GroupPath!=planned.GroupPath || (Namespace(x.Kind)=="block" && !x.Number.HasValue) || (planned.Number.HasValue && x.Number!=planned.Number))) throw new InvalidDataException("Native returned identities do not match the reviewed XML object.");
        }
        private static PlcBatchImportItem[] RecoveryOrder(PlcBatchImportItem[] items)
        {
            var pending=items.ToList();var ordered=new List<PlcBatchImportItem>();
            while(pending.Count>0)
            {
                var ready=pending.Where(x=>!x.RecoveryDependencies.Any(d=>pending.Any(p=>d==p.Replaced!.Kind+":"+p.Replaced.Name))).OrderBy(x=>x.Replaced!.Kind=="UDT" ? 0 : x.Replaced.Kind=="TagTable" ? 1 : x.Replaced.Kind=="FB" ? 2 : x.Replaced.Kind=="InstanceDB" ? 4 : 3).ThenBy(x=>x.RelativePath,StringComparer.Ordinal).ToArray();
                if(ready.Length==0) throw new IOException("Recovery dependencies contain a cycle.");
                foreach(var item in ready) {ordered.Add(item);pending.Remove(item);}
            }
            return ordered.ToArray();
        }
        internal static PlcBatchImportResult Run(PlcBatchImportRequest request,IEnumerable<PlcBatchImportObject> existing,Action recheck,Func<FileInfo,PlcBatchImportObject,PlcBatchImportObject[]> import,
            Action<PlcBatchImportObject,FileInfo>? backup=null,Func<FileInfo,PlcBatchImportObject,PlcBatchImportObject[]>? restore=null,Func<string>? recoveryDirectory=null,Action? recoveryPrecheck=null,Func<PlcBatchImportObject,string>? inspectBlocker=null)
        {
            bool issued=false;
            try
            {
                return RunCore(request,existing,recheck,(file,item)=>{issued=true;return import(file,item);},backup,restore,recoveryDirectory,recoveryPrecheck,inspectBlocker);
            }
            catch(AdapterPreconditionException) { throw; }
            catch(Exception error) when(!issued)
            { throw new AdapterPreconditionException("Batch import admission failed before any import: "+error.Message,request.InputParameter,false,error); }
        }
        private static PlcBatchImportResult RunCore(PlcBatchImportRequest request,IEnumerable<PlcBatchImportObject> existing,Action recheck,Func<FileInfo,PlcBatchImportObject,PlcBatchImportObject[]> import,
            Action<PlcBatchImportObject,FileInfo>? backup,Func<FileInfo,PlcBatchImportObject,PlcBatchImportObject[]>? restore,Func<string>? recoveryDirectory,Action? recoveryPrecheck,Func<PlcBatchImportObject,string>? inspectBlocker)
        {
            ValidateOptions(request);
            var inventory=existing.Take(4097).ToArray(); if(inventory.Length>4096) throw new AdapterPreconditionException("Target inventory exceeds 4096 objects.","maxItems");
            var files=TiaOpenness.Shared.NativeInputPolicy.Read(request.InputParameter,()=>Scan(request)); if(files.Length==0) throw new AdapterPreconditionException("No selected XML inputs.",request.InputParameter);
            var root=TiaOpenness.Shared.NativeInputPolicy.FullPath(request.Directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            var byRelative=files.ToDictionary(x=>x.Substring(root.Length).Replace('\\','/'),x=>x,StringComparer.Ordinal);
            string[] order=request.Order.Length==0 ? byRelative.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray() : request.Order.ToArray();
            if(order.Length!=files.Length || order.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=order.Length || order.Any(x=>!byRelative.ContainsKey(x))) throw new AdapterPreconditionException("importOrder must contain every selected relative path exactly once with exact casing.","importOrder");
            var locks=new List<FileStream>();
            try
            {
                var result=new PlcBatchImportResult {Executed=!request.DryRun,ProjectFile=request.Project,SoftwarePath=request.Software,Release=request.Release,Recursive=request.Program};
                var items=new List<PlcBatchImportItem>();long total=0;
                TiaOpenness.Shared.NativeInputPolicy.Read(request.InputParameter,()=>
                {
                foreach(var path in order)
                {
                    var file=new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(byRelative[path]));SafePath(file,request.InputParameter);
                    if(file.Length<=0 || file.Length>MaximumFileBytes) throw new AdapterPreconditionException("Input must be a nonempty bounded regular XML file.",request.InputParameter);
                    var stream=TiaOpenness.Shared.NativeInputPolicy.OpenRead(file.FullName,request.InputParameter);locks.Add(stream);
                    var expectedLength=stream.Length;
                    if(expectedLength>MaximumFileBytes || (total+=expectedLength)>MaximumTotalBytes) throw new AdapterPreconditionException("Input file or aggregate byte limit exceeded.",request.InputParameter);
                    byte[] bytes;
                    using(var memory=new MemoryStream())
                    {
                        var buffer=new byte[8192];int read;
                        while((read=stream.Read(buffer,0,buffer.Length))>0)
                        {
                            if(memory.Length+read>MaximumFileBytes) throw new AdapterPreconditionException("Input grew beyond byte budget.",request.InputParameter);
                            memory.Write(buffer,0,read);
                        }
                        bytes=memory.ToArray();
                    }
                    if(bytes.LongLength!=expectedLength || stream.Length!=expectedLength) throw new AdapterPreconditionException("Input changed while locked.",request.InputParameter);
                    PlcBatchImportObject planned;PlcBatchImportDependency[] dependencies;
                    using(var input=new MemoryStream(bytes,false)) planned=Parse(input,request,out dependencies);
                    if(dependencies.Any(dependency=>!inventory.Concat(items.Select(x=>x.Planned)).Any(x=>x.Name==dependency.Name && x.Kind==dependency.Kind))) throw new AdapterPreconditionException("Recognized type/instance dependency missing or ordered after its consumer; explicit order is not dependency proof.","importOrder");
                    if(items.Any(x=>Namespace(x.Planned.Kind)==Namespace(planned.Kind) && string.Equals(x.Planned.Name,planned.Name,StringComparison.OrdinalIgnoreCase))) throw new AdapterPreconditionException("Duplicate logical input object name: "+planned.Name,request.InputParameter);
                    var collisions=inventory.Where(x=>Namespace(x.Kind)==Namespace(planned.Kind) && string.Equals(x.Name,planned.Name,StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(collisions.Length>0 && (!request.Overwrite || collisions.Length!=1 || collisions[0].Name!=planned.Name || collisions[0].Kind!=planned.Kind || collisions[0].GroupPath!=planned.GroupPath)) throw new AdapterPreconditionException("Existing or cross-group/kind/case collision: "+planned.Name,request.InputParameter);
                    var replaced=collisions.SingleOrDefault();
                    if(request.Overwrite && replaced!=null && inspectBlocker!=null) replaced.BackupBlocker=TiaOpenness.Shared.NativeExportPolicy.InspectBlocker(()=>inspectBlocker(replaced));
                    string NumberSpace(string kind)=>kind=="GlobalDB" || kind=="InstanceDB" ? "DB" : kind;
                    if(planned.Number.HasValue && inventory.Where(x=>x!=replaced).Concat(items.Select(x=>x.Planned)).Any(x=>NumberSpace(x.Kind)==NumberSpace(planned.Kind) && x.Number==planned.Number)) throw new AdapterPreconditionException("Fixed number collision within block-kind namespace.",request.InputParameter);
                    items.Add(new PlcBatchImportItem {RelativePath=path,InputSha256=Hash(bytes),Planned=planned,Action=replaced==null ? "create" : replaced.BackupBlocker=="" ? "replace" : "replace-blocked: "+replaced.BackupBlocker,Status=replaced?.BackupBlocker.Length>0 ? "replace-blocked" : "planned",Failure=replaced?.BackupBlocker ?? "",Replaced=replaced,RecognizedDependencies=dependencies.Select(x=>x.Kind+":"+x.Name).ToArray()});
                }
                    return true;
                });
                result.Items=items.ToArray();
                var canonical=string.Concat(new[]{"batch-import-v1",request.Release,request.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),request.Project,request.Software,root,request.BlockGroup,request.TypeGroup,request.TagGroup,request.Program.ToString(),request.Regex,request.Overwrite ? "Override" : "None","stop-on-first-failure"}.Select(Field));
                canonical+=string.Concat(inventory.Select(Identity).OrderBy(x=>x,StringComparer.Ordinal).Select(Field));
                canonical+=string.Concat(items.Select(x=>Field(x.RelativePath)+Field(x.InputSha256)+Field(Identity(x.Planned))+(request.Overwrite ? Field(x.Action)+Field(x.Replaced==null ? "" : Identity(x.Replaced))+Field(x.Replaced?.BackupBlocker ?? "") : "")));
                result.PlanHash=Hash(Encoding.UTF8.GetBytes(canonical));
                // Approval is optional. Apply admission must precede recovery access and every native export/import.
                if(!request.DryRun && !string.Equals(result.PlanHash,request.ExpectedHash,StringComparison.Ordinal)) throw new AdapterPreconditionException("Plan or project changed; review a fresh preview before applying.","expectedPlanHash");
                if(items.Any(x=>x.Status=="replace-blocked"))
                {
                    if(!request.DryRun) throw new AdapterPreconditionException("Batch replacement backup is blocked: "+string.Join(", ",items.Where(x=>x.Status=="replace-blocked").Select(x=>x.Planned.Name+" ("+x.Failure+")"))+". Run CompilePlcSoftware first and resolve protection before previewing overwrite again.","overwrite",false);
                    result.Executed=false;return result;
                }
                if(items.Any(x=>x.Action=="replace")) recoveryPrecheck?.Invoke();
                if(request.DryRun) return result;
                // All original files remain read locked through every native call. Re-scan prevents added/deleted selections before first mutation.
                if(!files.SequenceEqual(TiaOpenness.Shared.NativeInputPolicy.Read(request.InputParameter,()=>Scan(request)),StringComparer.Ordinal)) throw new AdapterPreconditionException("Input selection changed during planning.",request.InputParameter);
                var replacements=items.Where(x=>x.Action=="replace").ToArray();
                var recoveryLocks=new List<FileStream>();
                try
                {
                    if(replacements.Length>0)
                    {
                        if(backup==null || restore==null || recoveryDirectory==null) throw new AdapterPreconditionException("Overwrite requires recovery export and restoration support.","overwrite",false);
                        PlcBatchImportItem failedBackup=replacements[0];
                        try
                        {
                            result.RecoveryDirectory=Path.GetFullPath(recoveryDirectory());
                            SafePath(new DirectoryInfo(result.RecoveryDirectory),"overwrite");
                            long recoveryBytes=0;
                            foreach(var item in replacements)
                            {
                                failedBackup=item;
                                recheck();
                                var output=new FileInfo(Path.Combine(result.RecoveryDirectory,items.IndexOf(item).ToString("D3",System.Globalization.CultureInfo.InvariantCulture)+".xml"));
                                item.RecoveryPath=output.FullName;backup(item.Replaced!,output);SafePath(output,"overwrite");
                                var stream=new FileStream(output.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);recoveryLocks.Add(stream);
                                if(stream.Length<1 || stream.Length>MaximumFileBytes || (recoveryBytes+=stream.Length)>MaximumTotalBytes) throw new IOException("Recovery export exceeds the admitted byte budget.");
                                byte[] recovery;
                                using(var memory=new MemoryStream()) {stream.CopyTo(memory);recovery=memory.ToArray();}
                                item.RecoverySha256=Hash(recovery);
                                using(var input=new MemoryStream(recovery,false))
                                {
                                    var target=Parse(input,request,out var dependencies);
                                    if(Identity(target)!=Identity(item.Replaced!)) throw new IOException("Recovery export identity differs from the replacement target.");
                                    item.RecoveryDependencies=dependencies.Select(x=>x.Kind+":"+x.Name).ToArray();
                                }
                                item.RestoreStatus="backup-ready";
                            }
                            // Validate recovery dependency order before any mutation, including cycles in old objects.
                            RecoveryOrder(replacements);
                        }
                        catch(Exception) /* swallow(privacy): retain a bounded backup failure and recovery paths without native exception text; no import has run. */
                        {
                            result.RecoveryFailure="backup-export-or-validation-failed-before-import";
                            foreach(var item in items) item.Status="not-attempted";
                            failedBackup.Status="failed";failedBackup.Failure="backup-failed";
                            return result;
                        }
                    }
                    bool stopped=false;
                    for(int i=0;i<items.Count;i++)
                    {
                        var item=items[i];if(stopped){item.Status="not-attempted";continue;}
                        bool returned=false;
                        try
                        {
                            recheck();item.Attempted=true;
                            var actual=import(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(byRelative[item.RelativePath])),item.Planned);returned=true;
                            item.ReturnedObjects=actual ?? new PlcBatchImportObject[0];
                            Verify(actual,item.Planned);
                            item.Status="imported";
                        }
                        catch(Exception) when(i==0 && !item.Attempted) { throw; }
                        catch(Exception error) /* swallow(native-fallback): a failed recheck after prior imports or an issued import stops the batch and retains recovery evidence */
                        {
                            bool unknown=item.Attempted && (!request.Overwrite || !returned && !(error is PlcBatchImportKnownFailure));
                            item.Status="failed";item.Failure=unknown ? "native-outcome-uncertain" : item.Attempted ? "native-import-or-verification-failed" : "target-recheck-failed";
                            result.NativeOutcomeUnknown=unknown;result.RequiresSessionReset=unknown || !request.Overwrite;
                            stopped=true;
                            if(request.Overwrite && replacements.Length>0)
                            {
                                foreach(var old in RecoveryOrder(replacements))
                                {
                                    if(!old.Attempted) continue;
                                    if(old==item && unknown) {old.RestoreStatus="not-attempted-unknown-outcome";continue;}
                                    if(old.RecoveryDependencies.Any(d=>replacements.Any(p=>p.Attempted && d==p.Replaced!.Kind+":"+p.Replaced.Name && p.RestoreStatus is "restore-failed" or "not-attempted-unknown-outcome" or "not-attempted-dependency-unverified")))
                                    {old.RestoreStatus="not-attempted-dependency-unverified";continue;}
                                    try
                                    {
                                        recheck();
                                        var recoveryStream=recoveryLocks[Array.IndexOf(replacements,old)];recoveryStream.Position=0;
                                        using(var sha=SHA256.Create()) if(BitConverter.ToString(sha.ComputeHash(recoveryStream)).Replace("-","").ToLowerInvariant()!=old.RecoverySha256) throw new IOException("Recovery bytes changed.");
                                        var actual=restore!(new FileInfo(old.RecoveryPath),old.Replaced!);Verify(actual,old.Replaced!);
                                        old.RestoreStatus="restored";
                                        if(old.Status=="imported") old.Status="rolled-back";
                                    }
                                    catch(Exception) /* swallow(privacy): preserve restoration uncertainty and recovery evidence without native exception text. */
                                    {old.RestoreStatus="restore-failed";old.RestoreFailure="restoration-outcome-unverified";result.NativeOutcomeUnknown=true;result.RequiresSessionReset=true;}
                                }
                            }
                        }
                    }
                }
                finally {foreach(var stream in recoveryLocks) stream.Dispose();}
                return result;
            }
            finally {foreach(var stream in locks) stream.Dispose();}
        }
    }
}
