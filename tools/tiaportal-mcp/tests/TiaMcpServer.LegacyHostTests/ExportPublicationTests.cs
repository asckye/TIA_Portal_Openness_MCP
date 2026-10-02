using System.Text.Json;
using ModelContextProtocol;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;

internal static class ExportPublicationTests
{
    internal static void Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"tia-publication-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string xml="<Document><Engineering version=\"V17\" /></Document>";
        FileInfo Output(string name)=>new(Path.Combine(root,name+".xml"));
        try
        {
            string? stagedDirectory=null;
            var good=Output("success");
            var recovery=PlcExportPublication.Publish(good,f=>{
                stagedDirectory=f.DirectoryName;
                check(f.FullName!=good.FullName && f.Directory!.Parent!.FullName==root,"Native callback uses independent sibling staging directory");
                check(f.Name==good.Name && !File.Exists(good.FullName),"Native export keeps requested basename and final destination unpublished");
                File.WriteAllText(f.FullName,xml);
            });
            check(recovery==null && !Directory.Exists(stagedDirectory),"Successful export removes only empty staging directory");
            check(File.ReadAllText(good.FullName)==xml,"Successful no-overwrite publication preserves XML bytes");
            bool called=false;
            try { PlcExportPublication.Publish(good,f=>called=true); throw new Exception("Existing destination accepted"); }
            catch(ArgumentException) { check(!called && File.ReadAllText(good.FullName)==xml,"Existing destination rejected before callback and unchanged"); }

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
                            if(!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Concurrent test rendezvous failed.");
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
            var preview=V17MutationEnvelope.Wrap("ExportBlock","Mutation",JsonSerializer.SerializeToNode(result),true);
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
