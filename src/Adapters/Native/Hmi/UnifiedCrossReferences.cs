using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
using System;
using System.Linq;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class UnifiedCrossReferences
    {
        internal static HmiMessage Read(IUnifiedSession session, string softwarePath, string objectPathJson, string filter = "AllObjects")
            => session.RunHmiStepTool("GetUnifiedCrossReferences", meta => {
                UnifiedObjectAddress.Parse(objectPathJson);
                var refusal = PlcCrossReferencePolicy.PolicyRefusal(
                    Environment.GetEnvironmentVariable(PlcCrossReferencePolicy.NativeQueryOptInVariable), unified: true);
                if (refusal != null)
                {
                    meta["status"] = "PRECONDITION_FAILED"; meta["operationSuccess"] = false;
                    meta["queried"] = false; meta["apiCallSuccess"] = false; meta["dataComplete"] = false;
                    meta["reason"] = refusal;
                    return refusal;
                }
                var target = UnifiedObjectAddress.Resolve(session.ExactUnifiedRoot(softwarePath), objectPathJson);
                var serviceType = typeof(global::Siemens.Engineering.IEngineeringObject).Assembly.GetType("Siemens.Engineering.CrossReference.CrossReferenceService", true)!;
                var provider = typeof(global::Siemens.Engineering.IEngineeringServiceProvider);
                if (!provider.IsInstanceOfType(target)) throw new NotSupportedException("Selected object is not an engineering service provider.");
                var service = provider.GetMethod("GetService")!.MakeGenericMethod(serviceType).Invoke(target, null)
                    ?? throw new NotSupportedException("CrossReferenceService unavailable on selected object.");
                var method = serviceType.GetMethods().Single(m => m.Name == "GetCrossReferences" && m.GetParameters().Length == 1);
                var filterValue = UnifiedRequestScalar.ConvertValue(HmiValues.Value(filter), method.GetParameters()[0].ParameterType);
                var raw = method.Invoke(service, new[] { filterValue }) ?? throw new InvalidOperationException("Null native result.");
                var rows = new HmiArray(); int visited = 0;
                foreach (var source in PlcGroupOperations.Items(PlcGroupOperations.Get(raw, "Sources")))
                {
                    if (++visited > 20000) throw new InvalidOperationException("Cross-reference traversal limit; collection not complete.");
                    var s = UnifiedRequestScalar.Read(source); var refs = new HmiArray(); s["references"] = refs;
                    foreach (var reference in PlcGroupOperations.Items(PlcGroupOperations.Get(source, "References")))
                    {
                        if (++visited > 20000) throw new InvalidOperationException("Cross-reference traversal limit; collection not complete.");
                        var r = UnifiedRequestScalar.Read(reference); var locations = new HmiArray(); r["locations"] = locations;
                        foreach (var location in PlcGroupOperations.Items(PlcGroupOperations.Get(reference, "Locations")))
                        {
                            if (++visited > 20000) throw new InvalidOperationException("Cross-reference traversal limit; collection not complete.");
                            locations.Add(UnifiedRequestScalar.Read(location));
                        }
                        refs.Add(r);
                    }
                    rows.Add(s);
                }
                meta["records"] = rows; meta["actualCount"] = rows.Count; meta["traversalComplete"] = true;
                meta["apiCallSuccess"] = true; meta["dataComplete"] = false;
                meta["scope"] = "Decoded native cross references. Completeness of dynamic script names and every native result field is not asserted.";
                return "Unified native cross references read; no script execution or runtime expression expansion.";
            });
    }
}
