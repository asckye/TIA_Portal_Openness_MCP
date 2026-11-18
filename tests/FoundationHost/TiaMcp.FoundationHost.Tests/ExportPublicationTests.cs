using TiaMcp.Engine.Tests.Shared;
using System.Text.Json;
using ModelContextProtocol;
using TiaMcp.FoundationHost;
using TiaMcp.Adapters;

internal static class ExportPublicationTests
{
    internal static void Run(Action<bool,string> check,Action<string> skip)
    {
        if(Environment.OSVersion.Platform!=PlatformID.Win32NT)
        {
            string unsupported=Path.Combine(Path.GetTempPath(),"tia-unsupported-publication-"+Guid.NewGuid().ToString("N"));
            bool invoked=false;
            try { PlcExportPublication.Publish(new FileInfo(Path.Combine(unsupported,"output.xml")),_=>invoked=true); throw new Exception("Unsupported platform accepted publication."); }
            catch(PlatformNotSupportedException)
            {
                check(!invoked,"Unsupported publication refuses before export callback");
                check(!Directory.Exists(unsupported),"Unsupported publication creates no filesystem artifacts");
            }
            skip("Windows atomic publication filesystem cases require a Windows host; Linux fail-closed guard verified.");
        }
        var root=Path.Combine(Path.GetTempPath(),"tia-publication-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string xml="<Document><Engineering version=\"V17\" /></Document>";
        FileInfo Output(string name)=>new(Path.Combine(root,name+".xml"));
        try
        {
            if(Environment.OSVersion.Platform==PlatformID.Win32NT)
            {
            foreach(bool bom in new[]{false,true})
            foreach(string newline in new[]{"\n","\r\n"})
            {
                string Native(string created,string product)=>
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>"+newline+
                    "<Document ID=\"0\">"+newline+"  <Engineering version=\"V17\" />"+newline+
                    "  <DocumentInfo marker=\"keep\"><Created zone=\"UTC\">"+created+"</Created><ExportSetting>None</ExportSetting>"+
                    "<InstalledProducts source=\"native\">"+product+"</InstalledProducts><!--keep--><Other>untouched</Other></DocumentInfo>"+newline+
                    "  <SW.Blocks.FC ID=\"1\"><AttributeList><Name>块</Name><Text>line1&#xD;line2</Text></AttributeList>"+
                    "<ObjectList><Member UId=\"7\"><![CDATA[unchanged]]></Member></ObjectList></SW.Blocks.FC>"+newline+"</Document>";
                var encoding=new System.Text.UTF8Encoding(bom);
                byte[] Bytes(string text)=>encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
                string Hash(byte[] bytes)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                var first=Output("normalized-"+bom+"-"+newline.Length+"-first");
                var second=Output("normalized-"+bom+"-"+newline.Length+"-second");
                string source=Native("2026-01-01T12:00:00Z","<Product version=\"17\" />");
                bool firstNormalized=false,secondNormalized=false;
                PlcExportPublication.Publish(first,f=>File.WriteAllBytes(f.FullName,Bytes(source)),true,value=>firstNormalized=value);
                PlcExportPublication.Publish(second,f=>File.WriteAllBytes(f.FullName,
                    Bytes(Native("2026-11-17T23:59:59Z","<Product version=\"21\" /><Product version=\"18\" />"))),true,value=>secondNormalized=value);
                var firstBytes=File.ReadAllBytes(first.FullName); var secondBytes=File.ReadAllBytes(second.FullName);
                check(firstNormalized && secondNormalized && firstBytes.SequenceEqual(secondBytes) && Hash(firstBytes)==Hash(secondBytes),
                    "Volatile-only differences publish identical bytes and hashes, BOM="+bom+", newline="+newline.Length);
                check(firstBytes.Take(3).SequenceEqual(new byte[]{0xef,0xbb,0xbf})==bom,
                    "Normalization preserves native UTF-8 BOM choice "+bom+"/"+newline.Length);
                string text=File.ReadAllText(first.FullName);
                check(newline=="\r\n" ? !text.Replace("\r\n","").Contains('\n') && text.Contains("\r\n") : !text.Contains('\r') && text.Contains('\n'),
                    "Normalization preserves physical newline style "+bom+"/"+newline.Length);
                var expected=System.Xml.Linq.XDocument.Parse(source);
                expected.Root!.Element("DocumentInfo")!.Element("Created")!.Value="2000-01-01T00:00:00.0000000Z";
                expected.Root.Element("DocumentInfo")!.Element("InstalledProducts")!.RemoveNodes();
                check(System.Xml.Linq.XNode.DeepEquals(expected,System.Xml.Linq.XDocument.Load(first.FullName)),
                    "Normalization preserves Engineering, ID/UId, attributes, comments, CDATA and all other nodes "+bom+"/"+newline.Length);
                var native=Output("native-"+bom+"-"+newline.Length); bool normalized=true;
                PlcExportPublication.Publish(native,f=>File.WriteAllBytes(f.FullName,Bytes(source)),false,value=>normalized=value);
                check(!normalized && File.ReadAllBytes(native.FullName).SequenceEqual(Bytes(source)),
                    "Disabled stripping publishes the exact native bytes "+bom+"/"+newline.Length);
                var missing=Output("missing-info-"+bom+"-"+newline.Length);
                string noInfo=source.Replace("<DocumentInfo", "<Nested><DocumentInfo").Replace("</DocumentInfo>","</DocumentInfo></Nested>");
                normalized=true;
                PlcExportPublication.Publish(missing,f=>File.WriteAllBytes(f.FullName,Bytes(noInfo)),true,value=>normalized=value);
                check(!normalized && File.ReadAllBytes(missing.FullName).SequenceEqual(Bytes(noInfo)),
                    "Only a direct DocumentInfo child is normalized; missing header keeps exact bytes "+bom+"/"+newline.Length);
                var absentCreated=Output("missing-created-"+bom+"-"+newline.Length);
                string missingCreated=source.Replace("<Created zone=\"UTC\">2026-01-01T12:00:00Z</Created>","");
                PlcExportPublication.Publish(absentCreated,f=>File.WriteAllBytes(f.FullName,Bytes(missingCreated)),true);
                var noCreatedDocument=System.Xml.Linq.XDocument.Load(absentCreated.FullName);
                var noCreatedExpected=System.Xml.Linq.XDocument.Parse(missingCreated);
                noCreatedExpected.Root!.Element("DocumentInfo")!.Element("InstalledProducts")!.RemoveNodes();
                check(noCreatedDocument.Root!.Element("DocumentInfo")!.Element("Created")==null
                    && System.Xml.Linq.XNode.DeepEquals(noCreatedExpected,noCreatedDocument),
                    "Missing Created remains absent while installed products are normalized "+bom+"/"+newline.Length);
                var writeFailure=Output("normalized-write-failure-"+bom+"-"+newline.Length);
                string? nativeStage=null,partialSibling=null; bool reportedNormalized=false;
                try
                {
                    PlcExportPublication.Publish(writeFailure,f=>{nativeStage=f.FullName;File.WriteAllBytes(f.FullName,Bytes(source));},true,
                        value=>reportedNormalized=value,writeNormalized:(file,content,writerEncoding)=>{
                            partialSibling=file.FullName;
                            check(file.FullName!=nativeStage && file.DirectoryName==Path.GetDirectoryName(nativeStage)
                                && File.ReadAllBytes(nativeStage!).SequenceEqual(Bytes(source)),
                                "Normalization writer receives a sibling and native bytes remain intact "+bom+"/"+newline.Length);
                            File.WriteAllText(file.FullName,content.Substring(0,content.Length/2),writerEncoding);
                            throw new IOException("simulated partial normalized write failure");
                        });
                    throw new Exception("Simulated normalization write failure was accepted");
                }
                catch(IOException failure)
                {
                    check((string?)failure.Data["exportPhase"]=="normalize-output" && !File.Exists(writeFailure.FullName)
                        && !reportedNormalized,"Failed normalization write never publishes or reports normalization "+bom+"/"+newline.Length);
                    check((string?)failure.Data["stagedFile"]==nativeStage && File.ReadAllBytes(nativeStage!).SequenceEqual(Bytes(source))
                        && (string?)failure.Data["stagedSha256"]==Hash(Bytes(source)).ToLowerInvariant(),
                        "Failed normalization write retains exact native export and its SHA-256 evidence "+bom+"/"+newline.Length);
                    check(partialSibling!=null && File.Exists(partialSibling)
                        && !File.ReadAllBytes(partialSibling).SequenceEqual(Bytes(source))
                        && Path.GetDirectoryName(partialSibling)==(string?)failure.Data["recoveryDirectory"],
                        "Failed partial normalized sibling is retained separately for recovery "+bom+"/"+newline.Length);
                }
                var raceOutput=Output("normalized-race-"+bom+"-"+newline.Length);
                try
                {
                    PlcExportPublication.Publish(raceOutput,f=>{File.WriteAllBytes(f.FullName,Bytes(source));File.WriteAllText(raceOutput.FullName,
                            "competitor");},true);
                    throw new Exception("Normalized publication overwrote a competing writer");
                }
                catch(IOException failure)
                {
                    check(Hash(File.ReadAllBytes((string)failure.Data["stagedFile"]!)).ToLowerInvariant()==(string?)failure.Data["stagedSha256"]
                        && File.ReadAllBytes((string)failure.Data["stagedFile"]!).SequenceEqual(firstBytes),
                        "Publication failure hashes the normalized staged bytes "+bom+"/"+newline.Length);
                }
            }
            bool noHeader=true;
            var unchanged=Output("no-document-info");
            PlcExportPublication.Publish(unchanged,f=>File.WriteAllText(f.FullName,xml),true,value=>noHeader=value);
            check(!noHeader && File.ReadAllBytes(unchanged.FullName).SequenceEqual(System.Text.Encoding.UTF8.GetBytes(xml)),
                "Document without DocumentInfo is unchanged and reports false");
            var duplicate=Output("duplicate-document-info");
            try
            {
                PlcExportPublication.Publish(duplicate,f=>File.WriteAllText(f.FullName,"<Document><DocumentInfo/><DocumentInfo/></Document>"),true);
                throw new Exception("Duplicate DocumentInfo accepted");
            }
            catch(IOException failure)
            {
                check((string?)failure.Data["exportPhase"]=="normalize-output" && !duplicate.Exists,
                    "Ambiguous duplicate DocumentInfo is retained without publication");
            }
            string? stagedDirectory=null;
            var good=Output("success");
            var recovery=PlcExportPublication.Publish(good,f=>{
                stagedDirectory=f.DirectoryName;
                check(f.FullName!=good.FullName && f.Directory!.Parent!.FullName==root,"Native callback uses independent sibling staging directory");
                check(f.Name == "export.xml" && !File.Exists(good.FullName),
                    "Native export uses export.xml and leaves the final destination unpublished");
                File.WriteAllText(f.FullName,xml);
            });
            check(recovery==null && !Directory.Exists(stagedDirectory),"Successful export removes only empty staging directory");
            check(File.ReadAllText(good.FullName)==xml,"Successful no-overwrite publication preserves XML bytes");
            bool called=false;
            try { PlcExportPublication.Publish(good,f=>called=true); throw new Exception("Existing destination accepted"); }
            catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) { check(!called && File.ReadAllText(good.FullName)==xml,"Existing destination rejected before callback and unchanged"); } catch(ArgumentException) { check(!called && File.ReadAllText(good.FullName)==xml,"Existing destination rejected before callback and unchanged"); }

            IOException Failure(string name,Action<FileInfo,FileInfo> callback,string phase)
            {
                var output=Output(name);
                try { PlcExportPublication.Publish(output,f=>callback(f,output)); throw new Exception("Invalid publication accepted"); }
                catch(IOException ex)
                {
                    check((string?)ex.Data["outputFile"]==output.FullName && (string?)ex.Data["exportPhase"]==phase,"Recovery evidence identifies target and failure phase "+name);
                    check(Directory.Exists((string)ex.Data["recoveryDirectory"]!),"Failure retains staging directory "+name);
                    return ex;
                }
            }
            var race=Failure("race",(f,o)=>{File.WriteAllText(f.FullName,xml);File.WriteAllText(o.FullName,"competing writer");},"publish");
            check(File.ReadAllText(Output("race").FullName)=="competing writer","Destination created during native callback is never overwritten");
            check(File.ReadAllText((string)race.Data["stagedFile"]!)==xml && ((string)race.Data["stagedSha256"]!).Length==64,"Losing export retains validated bytes and hash");
            using(var barrier=new Barrier(2))
            {
                var tasks=new[]{"first","second"}.Select(label=>Task.Run(()=>{
                    try
                    {
                        PlcExportPublication.Publish(Output("simultaneous"),f=>{
                            File.WriteAllText(f.FullName,"<Document producer=\""+label+"\"/>");
                            if(!barrier.SignalAndWait(TestDeadlines.Hang)) throw new TimeoutException("Concurrent test rendezvous failed.");
                        });
                        return (label,error:(IOException?)null);
                    }
                    catch(IOException ex) { return (label,error:ex); }
                })).ToArray();
                Task.WaitAll(tasks);
                var outcomes=tasks.Select(t=>t.Result).ToArray();
                check(outcomes.Count(x=>x.error==null)==1 && outcomes.Count(x=>x.error!=null)==1,"Exactly one simultaneous publisher succeeds");
                var winner=outcomes.Single(x=>x.error==null); var loser=outcomes.Single(x=>x.error!=null);
                check(File.ReadAllText(Output("simultaneous").FullName).Contains(winner.label),"Atomic publication retains only winning publisher bytes");
                check((string?)loser.error!.Data["exportPhase"]=="publish" && File.ReadAllText((string)loser.error.Data["stagedFile"]!).Contains(loser.label),"Concurrent loser retains its own recovery bytes without overwrite");
            }
            Failure("directory-race",(f,o)=>{File.WriteAllText(f.FullName,xml);Directory.CreateDirectory(o.FullName);},"publish");
            check(Directory.Exists(Output("directory-race").FullName),"Competing target directory is unchanged");
            var error=Failure("native-failure",(f,o)=>{File.WriteAllText(f.FullName,"partial");throw new InvalidOperationException("secret-password-and-project-content");},"native-export");
            check(!error.Message.Contains("secret") && !error.Data.Values.Cast<object>().Any(v=>v.ToString()!.Contains("secret")),"Public failure message and evidence exclude native exception secrets");
            check(File.ReadAllText((string)error.Data["stagedFile"]!)=="partial" && !File.Exists(Output("native-failure").FullName),"Partial native output retained without final file");
            foreach(var pair in new[]{("empty",""),("malformed","<Document>"),("root","<Other/>"),("dtd","<!DOCTYPE Document [<!ENTITY x 'bad'>]><Document>&x;</Document>")})
            {
                Failure(pair.Item1,(f,o)=>File.WriteAllText(f.FullName,pair.Item2),"validate-output");
                check(!File.Exists(Output(pair.Item1).FullName),"Invalid output never published "+pair.Item1);
            }
            Failure("missing",(f,o)=>{},"validate-output");
            check(!File.Exists(Output("missing").FullName),"Missing callback output never published");
            var sidecar=PlcExportPublication.Publish(Output("sidecar"),f=>{File.WriteAllText(f.FullName,xml);File.WriteAllText(Path.Combine(f.DirectoryName!,"additional.txt"),"evidence");});
            check(sidecar!=null && File.ReadAllText(Path.Combine(sidecar,"additional.txt"))=="evidence" && File.Exists(Output("sidecar").FullName),"Successful publication preserves unexpected sidecar and returns recovery location");

            }
            foreach(var release in new[]{"14sp1","15.1","16","17","18","19","20","21"})
            {
                var capability=PlcBlockXmlPolicy.Export(release,"SCL");
                check(release=="14sp1" ? capability.Content=="interface-only" && capability.FullProgramRestoreSupported==false : capability.FullProgramRestoreSupported==null,"Per-release export completeness is explicit without unsupported full-restore claims "+release);
                var old=Output("old-input"); File.WriteAllText(old.FullName,"<Document><Engineering version=\"V14 SP1\"/><SW.Blocks.FC><AttributeList><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>");
                var input=PlcBlockXmlPolicy.Import(release,old.FullName);
                try { input.RequireImport(); throw new Exception("Interface-only XML allowed as program import"); }
                catch(NotSupportedException) { check(input.FullProgramRestoreSupported==false,"V14 SCL source cannot restore/replace a full program into "+release); }
            }
            var current=Output("current-input");File.WriteAllText(current.FullName,"<Document><Engineering version=\"V17\"/><ProgrammingLanguage>SCL</ProgrammingLanguage></Document>");
            check(PlcBlockXmlPolicy.Import("17",current.FullName).ImportAllowed,"Modern source is not falsely labeled interface-only; other import gates remain");
            check(!PlcBlockXmlPolicy.Import("14sp1",current.FullName).ImportAllowed,"Modern SCL XML cannot bypass V14 SP1 target limitation");
            File.WriteAllText(current.FullName,"<Document><ProgrammingLanguage>SCL</ProgrammingLanguage></Document>");
            check(!PlcBlockXmlPolicy.Import("17",current.FullName).ImportAllowed,"SCL XML without producer evidence cannot imply full program content");
            File.WriteAllText(current.FullName,"<Document/>");
            check(!PlcBlockXmlPolicy.Import("14sp1",current.FullName).ImportAllowed,"V14 SP1 unknown block format rejected");
            var result=new PlcMutationResult { Executed=false,ProjectFile=@"C:\Projects\P.ap14" };
            PlcBlockXmlPolicy.Export("14sp1","SCL").Apply(result);
            var preview=V17MutationEnvelope.Wrap("ExportPlcBlock","Mutation",JsonSerializer.SerializeToNode(result),true);
            string Wire(string name)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name)??name;
            var meta=preview[Wire("Meta")]!;
            check(meta[Wire("XmlContent")]!.GetValue<string>()=="interface-only" && !meta[Wire("FullProgramRestoreSupported")]!.GetValue<bool>(),"MCP preview exposes interface-only and no-full-restore capability");
            check(meta[Wire("Warnings")]![0]!.GetValue<string>().Contains("not a complete program backup"),"MCP preview carries the SCL implementation warning");
        }
        finally
        {
            // Only this newly allocated test sandbox is removed, never supplied paths.
            if(Path.GetFileName(root).StartsWith("tia-publication-tests-",StringComparison.Ordinal) && Path.GetDirectoryName(root)==Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(root,true);
        }
    }
}
