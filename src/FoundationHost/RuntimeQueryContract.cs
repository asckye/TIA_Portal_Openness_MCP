using System.Globalization;
using System.Text.Json.Nodes;

namespace TiaMcp.FoundationHost;

internal static class RuntimeQueryContract
{
    private static readonly string[] Releases={"14sp1","15.1","16","17","18","19","20","21"};
    internal static JsonNode Validate(string tool,JsonNode? result,int? expectedProcessId=null)
    {
        try
        {
            if(result is not JsonObject o || !Releases.Contains(Text(o,"ReleaseKey"),StringComparer.Ordinal)) throw new InvalidDataException();
            if(tool=="GetState")
            {
                Keys(o,"ReleaseKey","IsAttached","ProcessId","ProjectFile","OwnsProject","IsLocalSession","IdentityStatus","RuntimeConnectionStatus");
                var attached=Bool(o,"IsAttached"); var owns=Bool(o,"OwnsProject"); var local=Bool(o,"IsLocalSession");
                if(o["ProcessId"]!=null) Pid(o,"ProcessId");
                if(attached!=(o["ProcessId"]!=null) || (!attached && o["ProjectFile"]!=null) || (o["ProjectFile"]==null && (owns||local))) throw new InvalidDataException();
                NullableText(o,"ProjectFile");
                if(Text(o,"IdentityStatus")!="unverified-cached-pid" || Text(o,"RuntimeConnectionStatus")!="not-probed") throw new InvalidDataException();
            }
            else if(tool=="ListPortalProcessProjects")
            {
                Keys(o,"ReleaseKey","Processes","IdentityStrategy");
                if(Text(o,"IdentityStrategy")!="os-pid-and-start-time-observation-only" || o["Processes"] is not JsonArray rows || rows.Count>1024) throw new InvalidDataException();
                var ids=new HashSet<int>();
                foreach(var row in rows) { var p=Process(row); if(!ids.Add(p)) throw new InvalidDataException(); }
            }
            else if(tool=="DiagnosePortalConnectReadiness")
            {
                Keys(o,"ReleaseKey","ProcessId","ProcessFound","Readiness","PermissionStatus","Reason","Process");
                var pid=Pid(o,"ProcessId"); var found=Bool(o,"ProcessFound");
                if(expectedProcessId.HasValue && pid!=expectedProcessId.Value) throw new InvalidDataException();
                if(Text(o,"PermissionStatus")!="not-probed" || Text(o,"Readiness")!=(found?"unknown":"not-found")) throw new InvalidDataException();
                Text(o,"Reason");
                if(found ? Process(o["Process"])!=pid : o["Process"]!=null) throw new InvalidDataException();
            }
            else throw new InvalidDataException();
            return o.DeepClone();
        }
        catch(Exception) { throw new InvalidDataException("Worker returned invalid runtime query details."); }
    }
    private static int Process(JsonNode? node)
    {
        if(node is not JsonObject o) throw new InvalidDataException();
        Keys(o,"ProcessId","SnapshotAcquisitionTime","ProjectPath","OsStartTimeUtc","OsIdentityStatus");
        var pid=Pid(o,"ProcessId");
        if(!DateTime.TryParseExact(Text(o,"SnapshotAcquisitionTime"),"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out _)) throw new InvalidDataException();
        NullableText(o,"ProjectPath"); NullableText(o,"OsStartTimeUtc");
        var status=Text(o,"OsIdentityStatus");
        if(status=="unknown") { if(o["OsStartTimeUtc"]!=null) throw new InvalidDataException(); }
        else if(status=="observed-not-bound")
        {
            var time=Text(o,"OsStartTimeUtc");
            if(!time.EndsWith("Z",StringComparison.Ordinal) || !DateTime.TryParseExact(time,"O",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var date) || date.Kind!=DateTimeKind.Utc) throw new InvalidDataException();
        }
        else throw new InvalidDataException();
        return pid;
    }
    private static void Keys(JsonObject o,params string[] names)
    { if(o.Count!=names.Length || names.Any(k=>!o.ContainsKey(k))) throw new InvalidDataException(); }
    private static string Text(JsonObject o,string key)
    { if(o[key] is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) throw new InvalidDataException(); return s; }
    private static void NullableText(JsonObject o,string key) { if(o[key]!=null) Text(o,key); }
    private static bool Bool(JsonObject o,string key)
    { if(o[key] is not JsonValue v || !v.TryGetValue<bool>(out var b)) throw new InvalidDataException(); return b; }
    private static int Pid(JsonObject o,string key)
    { if(o[key] is not JsonValue v || !v.TryGetValue<int>(out var n) || n<=0) throw new InvalidDataException(); return n; }
}
