using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;
namespace TiaMcp.LegacyHost;
internal static class BatchDocumentImportContract
{
 internal static string PlanHash(JsonArray items)
 {
  string Field(string text)=>text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+text;
  var text=Field("batch-document-global-db-v1")+string.Concat(items.SelectMany(x=>new[]{x!["Name"]!.GetValue<string>(),x["Preview"]!["PlanHash"]!.GetValue<string>()}).Select(Field));
  return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
 }
 internal static JsonObject Validate(JsonNode? payload,JsonObject request)
 {
  if(payload is not JsonObject result)throw new InvalidDataException("Missing batch document result.");
  void Shape(JsonObject obj,params string[] keys){if(obj.Count!=keys.Length||obj.Any(x=>!keys.Contains(x.Key)))throw new InvalidDataException("Unexpected batch document shape.");}
  string Text(JsonObject obj,string key)=>obj[key] is JsonValue v&&v.TryGetValue<string>(out var text)?text:throw new InvalidDataException("Invalid batch document text.");
  bool Flag(string key)=>result[key] is JsonValue v&&v.TryGetValue<bool>(out var flag)?flag:throw new InvalidDataException("Invalid batch document flag.");
  Shape(result,"Policy","PlanHash","Status","Attempted","MayHaveChanged","RequiresSessionReset","Error","Items");
  var dry=request["dryRun"]?.GetValue<bool>()??true;
  if(request["fileNamesWithoutExtension"] is not JsonArray names || names.Count<1||names.Count>16||names.Select(x=>x!.GetValue<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=names.Count)throw new InvalidDataException("Explicit unique manifest required.");
  if(result["Items"] is not JsonArray items||items.Count!=names.Count||Text(result,"Policy")!="batch-document-global-db-v1"||Text(result,"PlanHash")!=PlanHash(items))throw new InvalidDataException("Batch manifest/hash conflict.");
  var status=Text(result,"Status");
  if(dry?(status!="planned"||Flag("Attempted")||Flag("MayHaveChanged")||Flag("RequiresSessionReset")||Text(result,"Error")!=""):(status is not ("imported" or "unknown")||!Flag("Attempted")||!Flag("MayHaveChanged")||Flag("RequiresSessionReset")!=(status=="unknown")||Text(result,"Error")!=(status=="unknown"?"batch-outcome-uncertain-no-replay-or-rollback":"")))throw new InvalidDataException("Conflicting batch outcome.");
  if(!dry && (request["confirm"]?.GetValue<bool>()!=true||request["expectedPlanHash"]?.GetValue<string>()!=Text(result,"PlanHash")))throw new InvalidDataException("Batch confirmation differs.");
  bool stopped=false,failed=false;string? inventory=null,target=null;int succeeded=0;JsonObject? first=null;string[]? expectedInventory=null;
  for(int i=0;i<items.Count;i++)
  {
   if(items[i] is not JsonObject item)throw new InvalidDataException("Invalid batch item.");Shape(item,"Name","Status","Preview","Outcome","PostInventory");
   var name=Text(item,"Name");if(name!=names[i]!.GetValue<string>())throw new InvalidDataException("Manifest order differs.");
   var single=(JsonObject)request.DeepClone();single.Remove("fileNamesWithoutExtension");single["fileNameWithoutExtension"]=name;single["dryRun"]=true;
   var preview=DocumentImportContract.Validate(item["Preview"],single);
   if(first==null) {first=preview;expectedInventory=preview["Inventory"]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray();}
   foreach(var key in new[]{"ProjectFile","ProcessId","Release","TargetIdentity","SoftwarePath","GroupPath","InputDirectory"})if(!JsonNode.DeepEquals(first[key],preview[key]))throw new InvalidDataException("Mixed batch target/session identity.");
   if(item["PostInventory"] is not JsonArray post || post.Count>4096 || post.Any(x=>x is not JsonValue v || !v.TryGetValue<string>(out var text) || text.Length<1||text.Length>4096))throw new InvalidDataException("Invalid post-import inventory evidence.");
   var postText=post.Select(x=>x!.GetValue<string>()).ToArray();
   if(!postText.SequenceEqual(postText.OrderBy(x=>x,StringComparer.Ordinal))||postText.Distinct(StringComparer.Ordinal).Count()!=postText.Length)throw new InvalidDataException("Ambiguous post-import inventory.");
   var snapshot=preview["Inventory"]!.ToJsonString();var identity=preview["TargetIdentity"]!.ToJsonString();
   if((inventory!=null&&snapshot!=inventory)||(target!=null&&identity!=target))throw new InvalidDataException("Batch lacks one complete initial target inventory.");inventory=snapshot;target=identity;
   if(!dry&&preview["ProjectFile"]!.GetValue<string>()!=request["expectedProjectFile"]?.GetValue<string>())throw new InvalidDataException("Batch project differs.");
   var state=Text(item,"Status");
   if(state=="not-attempted") {if(item["Outcome"]!=null||post.Count!=0)throw new InvalidDataException("Unattempted item has outcome.");stopped=true;continue;}
   if(dry||stopped||failed||state is not ("succeeded" or "failed"))throw new InvalidDataException("Batch continued after stop or false preview mutation.");
   if(state=="failed"){failed=true;stopped=true;}
   if(item["Outcome"] is JsonObject outcome)
   {
    if(!outcome["Inventory"]!.AsArray().Select(x=>x!.GetValue<string>()).SequenceEqual(expectedInventory!,StringComparer.Ordinal))throw new InvalidDataException("Executed inventory differs from confirmed evolution.");
    single["dryRun"]=false;single["confirm"]=true;single["expectedPlanHash"]=outcome["PlanHash"]?.DeepClone();single["expectedProjectFile"]=preview["ProjectFile"]?.DeepClone();DocumentImportContract.Validate(outcome,single);
    foreach(var key in new[]{"CodeSha256","ResourceSha256","TargetIdentity","ProjectFile","ProcessId","Release"})if(!JsonNode.DeepEquals(preview[key],outcome[key]))throw new InvalidDataException("Executed set differs from reviewed manifest.");
    if(state=="succeeded"&&outcome["Status"]!.GetValue<string>()!="imported")throw new InvalidDataException("False per-set success.");
   }
   else if(state=="succeeded")throw new InvalidDataException("Success missing native evidence.");
   if(state=="succeeded")
   {
    var added=postText.Except(expectedInventory!,StringComparer.Ordinal).ToArray();
    string Field(string value)=>value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+value;
    var prefix=Field(preview["GroupPath"]!.GetValue<string>())+Field(name)+Field("GlobalDB");
    if(expectedInventory!.Except(postText,StringComparer.Ordinal).Any()||added.Length!=1||!added[0].StartsWith(prefix,StringComparison.Ordinal))throw new InvalidDataException("Unverified post-success delta.");
    var tail=added[0].Substring(prefix.Length);var colon=tail.IndexOf(':');if(colon<1||!int.TryParse(tail.Substring(0,colon),out var length)||length<1||tail.Length-colon-1!=length)throw new InvalidDataException("Missing stable imported object identity.");
    expectedInventory=postText;succeeded++;
   }
  }
  if(status=="imported"&&succeeded!=items.Count)throw new InvalidDataException("False complete batch success.");
  if(!dry&&succeeded==0&&!failed)throw new InvalidDataException("Attempted batch lacks attempted item evidence.");
  return result;
 }
}
