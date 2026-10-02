using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TiaMcp.PlcFoundation;
using TiaMcp.LegacyHost;

int checks=0;
void Check(bool value,string label) { if(!value) throw new Exception(label); checks++; }
void Reject(Action action,string label) { try { action(); } catch(ArgumentException) { checks++; return; } catch(InvalidOperationException) { checks++; return; } catch(InvalidDataException) { checks++; return; } throw new Exception("Expected rejection: "+label); }
PlcSoftwareTreeNode Node(string name,string path,string display,params PlcSoftwareTreeNode[] children) => new() { Name=name,Path=path,Display=display,Kind="group",Children=children };
var sections=new[]{Node("Program blocks","devices/PLC/CPU/blocks","Program blocks",Node("A/B","devices/PLC/CPU/blocks/A%2FB","A/B [FC1, SCL]"),Node("Empty","devices/PLC/CPU/blocks/Empty","Empty")),Node("PLC data types","devices/PLC/CPU/types","PLC data types")};
var text=PlcSoftwareReadPolicy.Render("PLC",sections);
Check(text==string.Join(Environment.NewLine,new[]{"PLC [PLC Software]","├── Program blocks","│   ├── A/B [FC1, SCL]","│   └── Empty","└── PLC data types",""}),"tree formatting and empty groups");
Check(PlcSoftwareReadPolicy.Paths(sections).Length==4,"all exact identities");
Check(PlcSoftwareReadPolicy.ChildPath("root","A/B %") == "root/A%2FB%20%25","escaped path");
Reject(()=>PlcSoftwareReadPolicy.Render("",sections),"empty software name");
Reject(()=>PlcSoftwareReadPolicy.Render("PLC",new[]{sections[0],sections[0]}),"duplicate identity");
var deep=Node("leaf","deep/0","leaf"); for(int i=1;i<132;i++) deep=Node("node","deep/"+i,"node",deep);
Reject(()=>PlcSoftwareReadPolicy.Render("PLC",new[]{deep}),"depth guard");
var candidates=new[]{new PlcReadCandidate<int>{Value=1,ExactPath="devices/D/CPU",Device="D",Host="CPU"},new PlcReadCandidate<int>{Value=2,ExactPath="devices/E/CPU",Device="E",Host="CPU"}};
Check(PlcReadPathPolicy.Resolve(candidates,"devices/D/CPU")==1,"exact selection");
Reject(()=>PlcReadPathPolicy.Resolve(candidates,"CPU"),"ambiguous alias");
Reject(()=>PlcReadPathPolicy.Resolve(candidates,"Missing"),"missing software");
Reject(()=>PlcReadPathPolicy.Resolve(candidates,"D/CPU/unused"),"unused segment");
var options=new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase,DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull};
JsonObject Info()=>JsonNode.Parse("""{"Name":"PLC","Attributes":[{"Name":"Custom","Value":{"KeepCase":1},"AccessMode":"Read"}],"Description":null,"Meta":{"softwarePath":"devices/D/CPU","scope":"ordinary PLC attributes","unreadableAttributes":[]}}""")!.AsObject();
var input=Info(); var result=SoftwareReadContract.Wrap("GetSoftwareInfo",input,options);
Check(result["name"]!.GetValue<string>()=="PLC" && result["Name"]==null,"wire casing");
Check(result["description"]==null && !result.ContainsKey("description"),"ignore null");
Check(result["attributes"]![0]!["value"]!["KeepCase"]!.GetValue<int>()==1,"opaque attribute value");
Check(result["meta"]!["typedApiCompilation"]!.GetValue<string>()=="pending-windows-rebuild","honest validation status");
Check(input["Meta"]!["success"]==null,"no input mutation");
Reject(()=>SoftwareReadContract.Wrap("Unknown",Info(),options),"unknown operation");
Reject(()=>SoftwareReadContract.Wrap("GetSoftwareInfo",null,options),"null result");
var bad=Info(); bad["Attributes"]=null; Reject(()=>SoftwareReadContract.Wrap("GetSoftwareInfo",bad,options),"missing attributes");
bad=Info(); bad["Unexpected"]=true; Reject(()=>SoftwareReadContract.Wrap("GetSoftwareInfo",bad,options),"unexpected fields");
JsonObject Tree()=>JsonNode.Parse("""{"Tree":"PLC [PLC Software]\n└── Program blocks\n","Meta":{"softwarePath":"devices/D/CPU","scope":"user groups","paths":[{"name":"Program blocks","path":"devices/D/CPU/blocks","kind":"block-group","objectPath":"","selectorParameter":"groupPath"}],"unavailableDisplayAttributes":[]}}""")!.AsObject();
Check(SoftwareReadContract.Wrap("GetSoftwareTree",Tree(),options)["tree"]!.GetValue<string>().StartsWith("PLC"),"tree envelope");
bad=Tree(); bad["Tree"]=""; Reject(()=>SoftwareReadContract.Wrap("GetSoftwareTree",bad,options),"empty tree");
bad=Tree(); bad["Meta"]!["paths"]!.AsArray().Add(bad["Meta"]!["paths"]![0]!.DeepClone()); Reject(()=>SoftwareReadContract.Wrap("GetSoftwareTree",bad,options),"duplicate paths");
bad=Tree(); bad["Meta"]!["paths"]![0]!["path"]="devices/E/CPU/blocks"; Reject(()=>SoftwareReadContract.Wrap("GetSoftwareTree",bad,options),"foreign PLC path");
var engine=new PlcFoundationEngine();
engine.Software.BlockGroup.Items.Add(new Siemens.Engineering.SW.Blocks.PlcBlock { Name="A/B",FailNumber=true });
engine.Software.BlockGroup.Groups.Add(new Siemens.Engineering.SW.Blocks.PlcBlockGroup { Name="Empty" });
var snapshot=engine.ReadSoftwareTree("D");
var snapshotJson=JsonSerializer.SerializeToNode(snapshot)!;
Check(snapshot.Tree.Contains("A/B [PlcBlock?, SCL]") && snapshot.Tree.Contains("Empty"),"actual read method renders snapshot and optional fallback");
Check(snapshotJson["Meta"]!["unavailableDisplayAttributes"]!.AsArray().Count==1,"optional failure is explicit");
Check(snapshotJson["Meta"]!["paths"]!.AsArray().Any(p=>p!["path"]!.GetValue<string>()=="devices/D/CPU/blocks/A%2FB"),"actual read method exact escaped path");
engine.Software.Attributes=new[]{new PlcAttributeValue { Name="Restricted",Value="<unreadable: SecurityException>",AccessMode="Read" }};
var infoJson=JsonSerializer.SerializeToNode(engine.ReadSoftwareInfo("D"))!;
Check(infoJson["Meta"]!["unreadableAttributes"]![0]!.GetValue<string>()=="Restricted","attribute failure is explicit");
Reject(()=>engine.ReadSoftwareInfo("missing"),"actual software selection failure");
engine.Software.BlockGroup.FailEnumeration=true;
try { engine.ReadSoftwareTree("D"); throw new Exception("Incomplete native tree accepted"); } catch(IOException) { checks++; }
engine.Software.BlockGroup.FailEnumeration=false;
engine.Software.TypeGroup=null;
Reject(()=>engine.ReadSoftwareTree("D"),"unavailable type root is not incomplete success");
Check(snapshotJson["Meta"]!["paths"]!.AsArray().Any(p=>p!["objectPath"]!.GetValue<string>()=="A%2FB" && p["selectorParameter"]!.GetValue<string>()=="blockPath"),"root-relative selectors reusable by detail tools");
Reject(()=>PlcSoftwareReadPolicy.Render("PLC",Enumerable.Range(0,10001).Select(i=>Node("n","p/"+i,"n")).ToArray()),"total node budget");
Reject(()=>PlcSoftwareReadPolicy.Render("PLC",new[]{Node("n","p",new string('x',1024*1024))}),"snapshot character budget");
bad=Tree(); bad["Meta"]!["paths"]![0]!["path"]="devices/D/CPU/blocksEvil"; Reject(()=>SoftwareReadContract.Wrap("GetSoftwareTree",bad,options),"path root segment boundary");
var sameBlock=Node("Same","root/Same","Same [FC1, SCL]"); sameBlock.Kind="block";
var sameGroup=Node("Same","root/Same","Same"); sameGroup.Kind="block-group";
Check(PlcSoftwareReadPolicy.Render("PLC",new[]{sameBlock,sameGroup}).Contains("Same [FC1, SCL]"),"group/object same name distinguished by kind");
foreach(var change in new[]{("kind","foreign"),("objectPath","Other/Block"),("selectorParameter","blockPath")})
{
    bad=Tree(); bad["Meta"]!["paths"]![0]![change.Item1]=change.Item2;
    Reject(()=>SoftwareReadContract.Wrap("GetSoftwareTree",bad,options),"malformed selector "+change.Item1);
}
bad=Tree(); bad["Meta"]!["paths"]![0]!.AsObject().Remove("objectPath"); Reject(()=>SoftwareReadContract.Wrap("GetSoftwareTree",bad,options),"missing object selector");
Console.WriteLine($"PASS {checks} software read pure-contract checks. Siemens SDK compilation and native execution NOT RUN.");
