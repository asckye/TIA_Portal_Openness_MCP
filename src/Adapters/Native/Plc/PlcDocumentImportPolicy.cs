using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TiaMcp.PlcFoundation
{
    internal sealed class PlcDocumentImportRequest
    {
        internal string Release="",Project="",Software="",Group="",Directory="",Name="",ExpectedHash="",ExpectedProject="",TargetIdentity="";
        internal int ProcessId;
        internal bool DryRun=true,Confirm,Overwrite;
    }
    internal sealed class PlcDocumentImportNative
    {
        internal string State="unknown";
        internal bool Success,ExistsVerified;
        internal string[] Identities=new string[0],Messages=new string[0];
    }
    internal static class PlcDocumentImportPolicy
    {
        internal const int MaximumBytes=4*1024*1024;
        internal static string Hash(byte[] bytes) {using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        private static string Field(string text)=>text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+text;
        internal static void ValidateOptions(PlcDocumentImportRequest r)
        {
            if(r.Release!="20" && r.Release!="21") throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Document import requires exact release 20 or 21.","export-plan",false);
            if(r.Overwrite) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Only documented reject-existing ImportDocumentOptions.None is admitted.","export-plan",false);
            PlcDocumentExportPolicy.Name(r.Name);
            if(r.Name.Length>128 || !PlcDocumentDeclaration.Identifier(r.Name)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Basename must be one ASCII identifier, 1..128 characters.");
            if(r.Group!=PlcExchangePolicy.ObjectPath(r.Group,true)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact canonical group path required.");
            if(!r.DryRun && (!r.Confirm || r.ExpectedHash.Length!=64 || r.ExpectedHash.Any(c=>!"0123456789abcdef".Contains(c)))) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Apply requires confirm and exact preview hash.");
        }
        internal static void SafePath(FileSystemInfo entry)
        {
            for(FileSystemInfo? current=entry;current!=null;current=current is DirectoryInfo dir?dir.Parent:((FileInfo)current).Directory)
                if((current.Attributes & FileAttributes.ReparsePoint)!=0) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Document input ancestry contains a reparse point.");
        }
        internal static void ValidateDirectory(string directory)
        {
            directory=TiaOpenness.Shared.NativeInputPolicy.NormalizeSeparators(directory);
            if(directory.Length>1024 || directory.Length<4 || directory[1]!=':' || directory[2]!='\\' || directory.IndexOf('/')>=0 || !((directory[0]>='A' && directory[0]<='Z') || (directory[0]>='a' && directory[0]<='z')) || directory.EndsWith("\\",StringComparison.Ordinal) || MutationIdentityPolicy.AbsoluteFile(directory)!=directory) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Canonical bounded local Windows directory required; aliases, ADS, reserved names and trailing separators are refused.");
        }
        internal static string[] Scan(PlcDocumentImportRequest r)
        {
            if(Path.DirectorySeparatorChar!='\\') throw new PlatformNotSupportedException("Document input requires local Windows paths.");
            r.Directory=TiaOpenness.Shared.NativeInputPolicy.NormalizeSeparators(r.Directory);
            var directory=r.Directory;ValidateDirectory(directory);
            if(directory.Length>1024 || directory.Length<3 || directory[1]!=':' || directory[2]!='\\' || directory.IndexOf('/')>=0 || !char.IsLetter(directory[0]) || directory.StartsWith("\\",StringComparison.Ordinal) || Path.GetFullPath(directory)!=directory || directory.EndsWith("\\",StringComparison.Ordinal)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Canonical existing local Windows directory required, without trailing separator.");
            var root=new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(directory)); if(!root.Exists) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Input directory missing.");SafePath(root);
            var entries=root.EnumerateFileSystemInfos().Take(4097).ToArray();
            if(entries.Length>4096) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Directory inventory exceeds 4096 entries.");
            var selected=entries.Where(x=>string.Equals(Path.GetFileNameWithoutExtension(x.Name),r.Name,StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach(var entry in selected) {SafePath(entry);if(entry is DirectoryInfo || (entry.Name!=r.Name+".s7dcl" && entry.Name!=r.Name+".s7res")) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Ambiguous case/stem or alternate-format document input.");}
            if(selected.Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=selected.Length || !selected.Any(x=>x.Name==r.Name+".s7dcl") || (r.Release=="20" && !selected.Any(x=>x.Name==r.Name+".s7res"))) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Missing or ambiguous document pair; V20 resource is required.");
            return selected.Select(x=>x.FullName).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        }
        internal static byte[] Read(Stream stream)
        {
            if(!stream.CanRead || !stream.CanSeek || stream.Length<1 || stream.Length>MaximumBytes) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Document input must contain 1..4194304 bytes.");
            var size=stream.Length;stream.Position=0;
            using(var memory=new MemoryStream())
            {
                var buffer=new byte[8192];int count;
                while((count=stream.Read(buffer,0,buffer.Length))>0) {if(memory.Length+count>MaximumBytes) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Input grew beyond byte limit.");memory.Write(buffer,0,count);}
                if(size!=memory.Length || size!=stream.Length) throw new IOException("Input changed while read locked.");
                return memory.ToArray();
            }
        }
        private static string[] Inventory(Func<IEnumerable<string>> get)
        {
            var inventory=get().Take(4097).ToArray();
            if(inventory.Length>4096 || inventory.Any(x=>string.IsNullOrEmpty(x) || x.Length>4096) || inventory.Distinct(StringComparer.Ordinal).Count()!=inventory.Length) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Incomplete, ambiguous or oversized ordinary block inventory.");
            return inventory.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        }
        internal static string InventoryItem(string group,string name,string kind,string identity)=>Field(group)+Field(name)+Field(kind)+Field(identity);
        internal static PlcDocumentImportResult Run(PlcDocumentImportRequest r,Func<string[]> scan,Func<string,Stream> open,Func<IEnumerable<string>> inventory,Func<string,bool> collision,Action recheck,Func<PlcDocumentImportNative> import)
        {
            ValidateOptions(r);
            if(r.ProcessId<=0 || string.IsNullOrEmpty(r.TargetIdentity) || string.IsNullOrEmpty(r.Project) || string.IsNullOrEmpty(r.Software)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Explicit project/process/software/target identity required.");
            if(!r.DryRun && r.ExpectedProject!=r.Project) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact expectedProjectFile required.");
            var paths=scan();
            if(paths.Length<1 || paths.Length>2 || !paths.Contains(Path.Combine(r.Directory,r.Name+".s7dcl")) || paths.Any(x=>x!=Path.Combine(r.Directory,r.Name+".s7dcl") && x!=Path.Combine(r.Directory,r.Name+".s7res")) || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Length || (r.Release=="20" && paths.Length!=2)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Incomplete or ambiguous document pair.");
            var streams=new List<Stream>();
            PlcDocumentImportResult? outcome=null;
            try
            {
                var bytes=new Dictionary<string,byte[]>();
                foreach(var path in paths) {var stream=open(path);streams.Add(stream);bytes.Add(path,Read(stream));}
                var code=bytes[Path.Combine(r.Directory,r.Name+".s7dcl")];
                var name=PlcDocumentDeclaration.Parse(code);
                if(name!=r.Name) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Declared document identity must exactly match requested basename; identity is never inferred from filename.");
                var snapshot=Inventory(inventory);
                if(collision(name)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Existing case-folded block name in the complete ordinary inventory.");
                byte[]? resource;bytes.TryGetValue(Path.Combine(r.Directory,r.Name+".s7res"),out resource);
                var result=new PlcDocumentImportResult {Release=r.Release,ProjectFile=r.Project,ProcessId=r.ProcessId,TargetIdentity=r.TargetIdentity,SoftwarePath=r.Software,GroupPath=r.Group,InputDirectory=r.Directory,DeclaredName=name,CodeSha256=Hash(code),ResourceSha256=resource==null?"":Hash(resource),Inventory=snapshot};
                outcome=result;
                var canonical=new[]{result.Policy,r.Release,r.Project,r.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),r.TargetIdentity,r.Software,r.Group,r.Directory,name,result.Kind,result.Language,result.NativeOptions,result.CodeSha256,result.ResourceSha256,result.Validation,result.InstalledUpdate};
                result.PlanHash=Hash(Encoding.UTF8.GetBytes(string.Concat(canonical.Select(Field))+string.Concat(snapshot.Select(Field))));
                if(r.DryRun) return result;
                if(r.ExpectedHash!=result.PlanHash) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Plan changed; review a fresh complete preview.");
                recheck();
                if(!snapshot.SequenceEqual(Inventory(inventory),StringComparer.Ordinal) || collision(name) || !paths.SequenceEqual(scan(),StringComparer.Ordinal)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Target/input inventory changed since preview.");
                for(int i=0;i<streams.Count;i++) if(Hash(Read(streams[i]))!=Hash(bytes[paths[i]])) throw new IOException("Input bytes changed after preview.");
                // From this point every exception is potentially partially mutating, even a null/non-Success result.
                result.Executed=true;result.Attempted=true;result.MayHaveChanged=true;result.NativeState="unknown";
                try
                {
                    var native=import();
                    if(native==null) throw new InvalidDataException("Native result was null.");
                    result.NativeState=native.State;result.ImportedIdentities=native.Identities;result.Messages=native.Messages;
                    if(!native.Success || native.Identities.Length!=1 || native.Identities[0]!=InventoryItem(r.Group,name,"GlobalDB","returned") || !native.ExistsVerified) throw new InvalidDataException("Native Success, one exact GlobalDB identity and exact target existence were not established.");
                    result.Status="imported";result.ExistsVerified=true;
                }
                catch(Exception) /* swallow(native-fallback): an attempted import can have mutated the project, so failure returns unknown outcome and requires reset */ {result.Status="unknown";result.Error="native-outcome-uncertain-inspect-before-new-session";result.RequiresSessionReset=true;}
                return result;
            }
            finally
            {
                Exception? closeFailure=null;
                foreach(var stream in streams)try {stream.Dispose();}catch(Exception ex) {closeFailure=ex;}
                if(closeFailure!=null)
                {
                    if(outcome!=null && outcome.Attempted) {outcome.Status="unknown";outcome.ExistsVerified=false;outcome.Error="native-outcome-uncertain-inspect-before-new-session";outcome.RequiresSessionReset=true;}
                    else throw new IOException("Input handle cleanup failed before native import.",closeFailure);
                }
            }
        }
    }
    // Complete admission grammar for a deliberately tiny DB subset, NOT a Siemens compiler.
    internal static class PlcDocumentDeclaration
    {
        internal static bool Identifier(string s)=>s.Length>0 && s.Length<=128 && ((s[0]>='A'&&s[0]<='Z')||(s[0]>='a'&&s[0]<='z')||s[0]=='_') && s.All(c=>(c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_');
        internal static string Parse(byte[] bytes)
        {
            if(bytes.Length<1 || bytes.Length>PlcDocumentImportPolicy.MaximumBytes) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Code size outside bounds.");
            int start=bytes.Length>=3 && bytes[0]==239 && bytes[1]==187 && bytes[2]==191?3:0;
            for(int i=start;i<bytes.Length;i++) if(bytes[i]>126 || (bytes[i]<32 && bytes[i]!=9 && bytes[i]!=10 && bytes[i]!=13)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Only ASCII code, optionally a UTF-8 BOM, is admitted; no transcoding.");
            var p=new Parser(Encoding.ASCII.GetString(bytes,start,bytes.Length-start));return p.Parse();
        }
        private sealed class Parser
        {
            private readonly string text;private int at,tokens;private string token="";private bool quoted;
            internal Parser(string text) {this.text=text;Next();}
            private void Next()
            {
                while(at<text.Length) {if(char.IsWhiteSpace(text[at])) {at++;continue;}if(text[at]=='/' && at+1<text.Length && text[at+1]=='/') {while(at<text.Length && text[at]!='\n' && text[at]!='\r')at++;continue;}break;}
                if(++tokens>100000) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Code exceeds token budget.");
                quoted=false;if(at==text.Length) {token="";return;}
                var c=text[at++];
                if(c=='"') {quoted=true;int begin=at;while(at<text.Length && text[at]!='"') {if(text[at]=='$'||text[at]=='\\'||text[at]=='\n'||text[at]=='\r')throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Escaped/multiline strings are outside admission grammar.");at++;}if(at==text.Length)throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Unterminated quoted token.");token=text.Substring(begin,at-begin);at++;return;}
                if(char.IsLetterOrDigit(c)||c=='_') {int begin=at-1;while(at<text.Length && (char.IsLetterOrDigit(text[at])||text[at]=='_'))at++;token=text.Substring(begin,at-begin);return;}
                if(c==':'&&at<text.Length&&text[at]=='='){at++;token=":=";return;}
                if("{}:;+-".IndexOf(c)<0) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Unknown syntax, block comments, string literals, expressions and escapes are outside admission grammar.");
                token=c.ToString();
            }
            private bool Is(string value)=>!quoted && token==value;
            private void Require(string value) {if(!Is(value))throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Expected admitted token "+value+".");Next();}
            private string Name() {var result=token;if(!Identifier(result) || new[]{"DATA_BLOCK","END_DATA_BLOCK","VAR","END_VAR","FUNCTION","FUNCTION_BLOCK","TYPE","ORGANIZATION_BLOCK","BEGIN"}.Contains(result.ToUpperInvariant()))throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Ordinary identifier required.");Next();return result;}
            internal string Parse()
            {
                if(Is("{"))
                {
                    Next();var keys=new HashSet<string>(StringComparer.Ordinal);
                    while(!Is("}"))
                    {
                        var key=token;if(quoted || !new[]{"S7_Optimized","S7_StandardRetain","S7_Version"}.Contains(key) || !keys.Add(key))throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Unknown/duplicate pragma; safety, language, MLC and manual-number pragmas excluded.");
                        Next();Require(":=");if(!quoted)throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Quoted pragma value required.");
                        if(key=="S7_Version") {var pieces=token.Split('.');if(pieces.Length!=2 || pieces.Any(x=>x.Length<1 || x.Length>3 || x.Any(c=>c<'0'||c>'9')))throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Bounded version pragma required.");}
                        else if(token!="TRUE"&&token!="FALSE")throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Boolean pragma required.");
                        Next();if(Is(";"))Next();else if(!Is("}"))throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Pragma separator required.");
                    }
                    Require("}");
                }
                Require("DATA_BLOCK");var name=Name();Require("VAR");var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int count=0;
                while(!Is("END_VAR"))
                {
                    if(++count>1024)throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("DB exceeds 1024 scalar declarations.");
                    var variable=Name();if(!names.Add(variable))throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Duplicate scalar name.");Require(":");
                    var type=token;if(quoted || !new[]{"Bool","SInt","USInt","Int","UInt","DInt","UDInt","LInt","ULInt"}.Contains(type))throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Only primitive Bool/integer scalar DB declarations admitted.");Next();
                    if(Is(":="))
                    {
                        Next();if(type=="Bool") {if(quoted || (token!="true"&&token!="false"&&token!="TRUE"&&token!="FALSE"))throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Boolean literal required.");Next();}
                        else {if(Is("+")||Is("-"))Next();if(quoted || token.Length<1 || token.Length>20 || token.Any(c=>c<'0'||c>'9'))throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Decimal integer literal required; native range checking remains pending.");Next();}
                    }
                    Require(";");
                }
                if(count==0)throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("At least one scalar declaration required.");
                Require("END_VAR");Require("END_DATA_BLOCK");if(token!="" || quoted)throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Additional declarations or trailing tokens refused.");return name;
            }
        }
    }
}
