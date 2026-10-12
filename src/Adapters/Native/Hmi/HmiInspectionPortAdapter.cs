using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
using System;
using System.Collections.Generic;
using System.Linq;
namespace TiaMcp.Adapters.Native.Hmi {
    internal interface IHmiInspectionSession
    {
        public object ResolveHmiSoftwareOrThrow(string path);
        public HmiMessage RunHmiStepTool(string tool, Func<HmiObject, string> action, bool requiresProject = true);
        public void RecordHmiReadFault(HmiObject meta);
    }
    internal sealed class HmiInspectionPortAdapter { private readonly IHmiInspectionSession _session; public HmiInspectionPortAdapter(IHmiInspectionSession session) => _session = session;
public HmiMessage ReadHmiScreenSnapshot(string softwarePath,string screenPath,int maxDepth=6,int maxNodes=2000)
            => _session.RunHmiStepTool("GetHmiScreenSnapshot",meta=>{
                meta["softwarePath"]=softwarePath;meta["screenPath"]=screenPath;
                meta["readOnly"] = true;
                var trace = new HmiReadTrace();
                trace.Step("start", softwarePath + ":" + screenPath);
                try
                {
                    trace.Step("before", "resolveSoftware:" + softwarePath);
                    var software = _session.ResolveHmiSoftwareOrThrow(softwarePath);
                    trace.Step("after", "resolveSoftware:" + softwarePath);
                    trace.Step("before", "resolveScreen:" + screenPath);
                    var screen = HmiExactAccess.Screen(software, screenPath);
                    trace.Step("after", "resolveScreen:" + screenPath);
                    var snapshot = HmiSnapshot.Capture(screen, maxDepth, maxNodes, 65536, trace.Step);
                    meta["snapshot"] = snapshot;
                    foreach (var key in new[] { "apiCallSuccess", "dataComplete", "traversalComplete", "truncated", "readFailureCount", "quarantinedCount", "connectionUnavailable", "expectedCount", "actualCount", "nextCursor" })
                        meta[key] = snapshot[key]?.DeepClone();
                    meta["operationSuccess"] = snapshot["apiCallSuccess"]?.DeepClone();
                    trace.AddTo(meta);
                    if (snapshot["connectionUnavailable"]!.GetValue<bool>())
                    {
                        meta["failurePhase"] = trace.Phase;
                        meta["failurePath"] = snapshot["failurePath"]?.DeepClone();
                        _session.RecordHmiReadFault(meta);
                    }
                    trace.Step("complete", "apiCallSuccess=" + snapshot["apiCallSuccess"] + ";dataComplete=" + snapshot["dataComplete"]);
                    return snapshot["connectionUnavailable"]!.GetValue<bool>()
                        ? "HMI connection became unavailable; partial evidence retained and further snapshot reads blocked. No automatic retry or rebind."
                        : "Read-only screen snapshot; inspect dataComplete, quarantine and coverage. This is not an importable native backup.";
                }
                catch (Exception ex) { trace.Step("failed", trace.LastAttemptedPath ?? screenPath, ex); throw; }
                finally { trace.AddTo(meta); }
            });

public HmiMessage ListHmiScreenPaths(string softwarePath,int offset=0,int limit=100)
            => _session.RunHmiStepTool("ListHmiScreenPaths",meta=>{
                if(offset<0||limit<1||limit>500)throw new PlcSoftwareException("InvalidParams","offset >= 0 and limit 1..500 required.");
                var screens=HmiExactAccess.Screens(_session.ResolveHmiSoftwareOrThrow(softwarePath));
                meta["total"]=screens.Count;meta["offset"]=offset;
                meta["paths"]=new HmiArray(screens.Skip(offset).Take(limit).Select(x=>HmiValues.Value(x.Path)).ToArray());
                meta["nextOffset"]=offset+limit<screens.Count?(object?)HmiValues.Value(offset+limit):null;
                return "Complete group paths, with URI-escaped name segments; leading / selects an exact path.";
            });
} }
