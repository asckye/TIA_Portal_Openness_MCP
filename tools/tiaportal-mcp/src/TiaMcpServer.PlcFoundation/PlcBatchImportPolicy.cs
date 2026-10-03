using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.PlcFoundation
{
    internal sealed class PlcBatchImportRequest
    {
        internal string Release="",Project="",Software="",Directory="",BlockGroup="",TypeGroup="",TagGroup="",Regex="",ExpectedHash="",ExpectedProject="",TechnologyGroup="";
        internal int ProcessId,MaxItems=128;
        internal bool Program,Overwrite,CompileAfter,Confirm,DryRun=true,StopOnImportFailure=true;
        internal string[] Order=new string[0];
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
        private static void SafePath(FileSystemInfo entry)
        {
            for(FileSystemInfo? current=entry;current!=null;current=current is DirectoryInfo dir ? dir.Parent : ((FileInfo)current).Directory)
                if((current.Attributes & FileAttributes.ReparsePoint)!=0) throw new ArgumentException("Input path ancestry contains a link/reparse point.");
        }
        private static string[] Scan(PlcBatchImportRequest request)
        {
            if(!Path.IsPathRooted(request.Directory)) throw new ArgumentException("Input directory must be absolute and existing.");
            var root=new DirectoryInfo(request.Directory); if(!root.Exists) throw new ArgumentException("Input directory does not exist."); SafePath(root);
            var regex=new Regex(request.Regex,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
            var pending=new Stack<DirectoryInfo>(); pending.Push(root); var files=new List<string>(); int entries=0,dirs=0;
            var aliases=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while(pending.Count>0)
            {
                var dir=pending.Pop(); if(++dirs>1024) throw new ArgumentException("Directory count exceeds bounded scan.");
                foreach(var entry in dir.EnumerateFileSystemInfos())
                {
                    if(++entries>4096) throw new ArgumentException("Entry count exceeds bounded scan.");
                    SafePath(entry); if(!aliases.Add(entry.FullName)) throw new ArgumentException("Case-insensitive input path collision.");
                    if(entry is DirectoryInfo child) {if(request.Program) pending.Push(child);continue;}
                    if(!entry.Extension.Equals(".xml",StringComparison.OrdinalIgnoreCase) || !regex.IsMatch(entry.Name)) continue;
                    files.Add(entry.FullName); if(files.Count>request.MaxItems) throw new ArgumentException("Selected files exceed maxItems; never truncated.");
                }
            }
            return files.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        }
        internal static void ValidateOptions(PlcBatchImportRequest request)
        {
            if(request.Overwrite) throw new NotSupportedException("This candidate supports overwrite=false (native None) only; replacements require separate recovery coverage.");
            if(request.CompileAfter) throw new NotSupportedException("compileAfter is blocked before import: project-wide compile coverage is not established.");
            if(!request.StopOnImportFailure) throw new NotSupportedException("This candidate stops on every failure; continuation is not supported.");
            if(!string.IsNullOrEmpty(request.TechnologyGroup)) throw new NotSupportedException("Technology objects are outside bounded batch import scope.");
            if(request.MaxItems<1 || request.MaxItems>256) throw new ArgumentException("maxItems must be 1..256.");
            if(request.Regex.Length>1024) throw new ArgumentException("Regex exceeds bounded length.");
            if(!new[]{"14sp1","15.1","16","17","18","19","20","21"}.Contains(request.Release)) throw new ArgumentException("Unknown exact release.");
            if(!request.DryRun && (!request.Confirm || !string.Equals(request.ExpectedProject,request.Project,StringComparison.Ordinal) || request.Order.Length==0 || request.ExpectedHash.Length!=64)) throw new ArgumentException("Apply requires confirm, exact project identity, explicit complete importOrder and expectedPlanHash.");
        }
        private static PlcBatchImportObject Parse(Stream stream,PlcBatchImportRequest request,out PlcBatchImportDependency[] dependencies)
        {
            stream.Position=0;
            var settings=new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaximumFileBytes,CloseInput=false};
            // Enforce depth before materializing any XML tree.
            using(var reader=XmlReader.Create(stream,settings)) while(reader.Read()) if(reader.Depth>64) throw new ArgumentException("XML nesting exceeds 64.");
            stream.Position=0; XDocument document;
            using(var reader=XmlReader.Create(stream,settings)) document=XDocument.Load(reader);
            if(document.Root==null || document.Root.Name!=XName.Get("Document")) throw new ArgumentException("Only ordinary native Document XML is supported.");
            var roots=document.Root.Elements().Where(x=>x.Name.LocalName!="Engineering" && x.Name.LocalName!="DocumentInfo").ToArray();
            if(roots.Length!=1) throw new ArgumentException("Exactly one ordinary root object per file is required; no roots are skipped.");
            var node=roots[0]; var kinds=new Dictionary<string,string> {{"SW.Blocks.FC","FC"},{"SW.Blocks.FB","FB"},{"SW.Blocks.OB","OB"},{"SW.Blocks.GlobalDB","GlobalDB"},{"SW.Blocks.InstanceDB","InstanceDB"},{"SW.Types.PlcStruct","UDT"},{"SW.Tags.PlcTagTable","TagTable"}};
            if(node.Name.NamespaceName!="" || !kinds.TryGetValue(node.Name.LocalName,out var kind) || (!request.Program && Namespace(kind)!="block")) throw new NotSupportedException("Unknown or out-of-scope XML root; nothing was skipped.");
            var engineering=document.Root.Elements("Engineering").ToArray();
            var expected=request.Release=="14sp1" ? "V14 SP1" : "V"+request.Release;
            // Original V14 markers cannot establish SP1. Exact producer only, no inferred upgrades.
            if(engineering.Length!=1 || (string?)engineering[0].Attribute("version")!=expected) throw new NotSupportedException("XML must identify the exact reviewed release producer; no version rewrite or cross-release inference.");
            var attrs=node.Elements("AttributeList").ToArray(); if(attrs.Length!=1) throw new ArgumentException("Missing or ambiguous object AttributeList.");
            var names=attrs[0].Elements("Name").ToArray(); if(names.Length!=1 || string.IsNullOrWhiteSpace(names[0].Value)) throw new ArgumentException("Missing or ambiguous XML logical name.");
            if(document.Descendants().Any(x=>(x.Name.LocalName.IndexOf("Safety",StringComparison.OrdinalIgnoreCase)>=0 || x.Name.LocalName.IndexOf("KnowHow",StringComparison.OrdinalIgnoreCase)>=0 || x.Name.LocalName.IndexOf("Protection",StringComparison.OrdinalIgnoreCase)>=0) && !string.Equals(x.Value,"false",StringComparison.OrdinalIgnoreCase))) throw new NotSupportedException("Protected/Safety XML is outside this candidate.");
            var language=attrs[0].Elements("ProgrammingLanguage").Select(x=>x.Value).ToArray();
            if(Namespace(kind)=="block" && (language.Length!=1 || !new[]{"LAD","FBD","STL","SCL","DB"}.Contains(language[0]))) throw new NotSupportedException("Block language is missing or not reviewed for ordinary import.");
            if(request.Release=="14sp1" && language.Contains("SCL")) throw new NotSupportedException("V14 SP1 SCL XML is interface-only and cannot restore a complete block.");
            if(language.Contains("SCL") && !node.Descendants().Any(x=>x.Name.LocalName=="SW.Blocks.CompileUnit" && x.Descendants().Any(e=>e.Name.LocalName=="StructuredText" && e.HasElements))) throw new NotSupportedException("SCL implementation is absent or unverified; interface-only input cannot restore a complete block.");
            var numbers=attrs[0].Elements("Number").ToArray(); int? number=null;
            if(numbers.Length>1) throw new ArgumentException("Ambiguous block number.");
            if(Namespace(kind)!="block" && numbers.Length!=0) throw new NotSupportedException("Number verification for non-block objects is outside this bounded candidate.");
            if(numbers.Length==1) {int n;if(!int.TryParse(numbers[0].Value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out n) || n<0) throw new ArgumentException("Invalid fixed block number.");number=n;}
            // Recognized declarations only; this is deliberately not a complete dependency analyzer.
            var references=document.Descendants().Attributes().Where(a=>a.Name.LocalName=="Datatype" || a.Name.LocalName=="DataType").Select(a=>a.Value)
                .Where(v=>v.StartsWith("\"",StringComparison.Ordinal) && v.EndsWith("\"",StringComparison.Ordinal)).Select(v=>v.Substring(1,v.Length-2))
                .Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Select(x=>new PlcBatchImportDependency {Name=x,Kind="UDT"});
            dependencies=references.Concat(document.Descendants().Where(e=>e.Name.LocalName=="InstanceOfName").Select(e=>e.Value).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Select(x=>new PlcBatchImportDependency {Name=x,Kind="FB"})).OrderBy(x=>x.Kind+":"+x.Name,StringComparer.Ordinal).ToArray();
            return new PlcBatchImportObject {Name=names[0].Value,Kind=kind,Number=number,GroupPath=kind=="UDT" ? request.TypeGroup : kind=="TagTable" ? request.TagGroup : request.BlockGroup};
        }
        internal static PlcBatchImportResult Run(PlcBatchImportRequest request,IEnumerable<PlcBatchImportObject> existing,Action recheck,Func<FileInfo,PlcBatchImportObject,PlcBatchImportObject[]> import)
        {
            ValidateOptions(request);
            var inventory=existing.Take(4097).ToArray(); if(inventory.Length>4096) throw new ArgumentException("Target inventory exceeds 4096 objects.");
            var files=Scan(request); if(files.Length==0) throw new ArgumentException("No selected XML inputs.");
            var root=Path.GetFullPath(request.Directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            var byRelative=files.ToDictionary(x=>x.Substring(root.Length).Replace('\\','/'),x=>x,StringComparer.Ordinal);
            string[] order=request.Order.Length==0 ? byRelative.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray() : request.Order.ToArray();
            if(order.Length!=files.Length || order.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=order.Length || order.Any(x=>!byRelative.ContainsKey(x))) throw new ArgumentException("importOrder must contain every selected relative path exactly once with exact casing.");
            var locks=new List<FileStream>();
            try
            {
                var result=new PlcBatchImportResult {Executed=!request.DryRun,ProjectFile=request.Project,SoftwarePath=request.Software,Release=request.Release,Recursive=request.Program};
                var items=new List<PlcBatchImportItem>();long total=0;
                foreach(var path in order)
                {
                    var file=new FileInfo(byRelative[path]);SafePath(file);
                    if(file.Length<=0 || file.Length>MaximumFileBytes) throw new ArgumentException("Input must be a nonempty bounded regular XML file.");
                    var stream=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);locks.Add(stream);
                    var expectedLength=stream.Length;
                    if(expectedLength>MaximumFileBytes || (total+=expectedLength)>MaximumTotalBytes) throw new ArgumentException("Input file or aggregate byte limit exceeded.");
                    byte[] bytes;
                    using(var memory=new MemoryStream())
                    {
                        var buffer=new byte[8192];int read;
                        while((read=stream.Read(buffer,0,buffer.Length))>0)
                        {
                            if(memory.Length+read>MaximumFileBytes) throw new ArgumentException("Input grew beyond byte budget.");
                            memory.Write(buffer,0,read);
                        }
                        bytes=memory.ToArray();
                    }
                    if(bytes.LongLength!=expectedLength || stream.Length!=expectedLength) throw new ArgumentException("Input changed while locked.");
                    PlcBatchImportObject planned;PlcBatchImportDependency[] dependencies;
                    using(var input=new MemoryStream(bytes,false)) planned=Parse(input,request,out dependencies);
                    if(dependencies.Any(dependency=>!inventory.Concat(items.Select(x=>x.Planned)).Any(x=>x.Name==dependency.Name && x.Kind==dependency.Kind))) throw new ArgumentException("Recognized type/instance dependency missing or ordered after its consumer; explicit order is not dependency proof.");
                    if(inventory.Concat(items.Select(x=>x.Planned)).Any(x=>Namespace(x.Kind)==Namespace(planned.Kind) && string.Equals(x.Name,planned.Name,StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Duplicate or existing logical object name, including cross-group collision: "+planned.Name);
                    string NumberSpace(string kind)=>kind=="GlobalDB" || kind=="InstanceDB" ? "DB" : kind;
                    if(planned.Number.HasValue && inventory.Concat(items.Select(x=>x.Planned)).Any(x=>NumberSpace(x.Kind)==NumberSpace(planned.Kind) && x.Number==planned.Number)) throw new ArgumentException("Fixed number collision within block-kind namespace.");
                    items.Add(new PlcBatchImportItem {RelativePath=path,InputSha256=Hash(bytes),Planned=planned,RecognizedDependencies=dependencies.Select(x=>x.Kind+":"+x.Name).ToArray()});
                }
                result.Items=items.ToArray();
                var canonical=string.Concat(new[]{"batch-import-v1",request.Release,request.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),request.Project,request.Software,root,request.BlockGroup,request.TypeGroup,request.TagGroup,request.Program.ToString(),request.Regex,"None","stop-on-first-failure"}.Select(Field));
                canonical+=string.Concat(inventory.Select(Identity).OrderBy(x=>x,StringComparer.Ordinal).Select(Field));
                canonical+=string.Concat(items.Select(x=>Field(x.RelativePath)+Field(x.InputSha256)+Field(Identity(x.Planned))));
                result.PlanHash=Hash(Encoding.UTF8.GetBytes(canonical));
                if(request.DryRun) return result;
                if(!string.Equals(result.PlanHash,request.ExpectedHash,StringComparison.Ordinal)) throw new ArgumentException("Plan changed; preview and review the full manifest again.");
                // All original files remain read locked through every native call. Re-scan prevents added/deleted selections before first mutation.
                if(!files.SequenceEqual(Scan(request),StringComparer.Ordinal)) throw new ArgumentException("Input selection changed during planning.");
                bool stopped=false;
                for(int i=0;i<items.Count;i++)
                {
                    var item=items[i];if(stopped){item.Status="not-attempted";continue;}
                    try
                    {
                        recheck(); item.Attempted=true;
                        var actual=import(new FileInfo(byRelative[item.RelativePath]),item.Planned);
                        item.ReturnedObjects=actual ?? new PlcBatchImportObject[0];
                        if(actual==null || actual.Length!=1 || actual.Any(x=>x==null || x.Name!=item.Planned.Name || x.Kind!=item.Planned.Kind || x.GroupPath!=item.Planned.GroupPath || (Namespace(x.Kind)=="block" && !x.Number.HasValue) || (item.Planned.Number.HasValue && x.Number!=item.Planned.Number))) throw new InvalidDataException("Native returned identities do not match the reviewed XML object.");
                        item.Status="imported";
                    }
                    catch(Exception)
                    {
                        item.Status="failed";item.Failure=item.Attempted ? "native-outcome-uncertain" : "target-recheck-failed";
                        stopped=true;result.RequiresSessionReset=true;
                    }
                }
                return result;
            }
            finally {foreach(var stream in locks) stream.Dispose();}
        }
    }
}
