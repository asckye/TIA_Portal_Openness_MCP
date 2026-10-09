using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Siemens.Engineering;
namespace TiaMcpServer.Siemens
{
    internal static class OfficialServiceAccess
    {
        // Names supplied by dedicated adapters, never arbitrary client type names.
        internal static object Require(object owner,string typeName,string assemblyName)
        {
            var type=typeof(IEngineeringObject).Assembly.GetType(typeName) ?? AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType(typeName)).FirstOrDefault(t=>t!=null);
            if(type==null) {
                try { type=Assembly.Load(assemblyName).GetType(typeName); }
                catch(FileNotFoundException ex) { throw new NotSupportedException("Official optional component is not installed/resolvable: "+assemblyName,ex); }
            }
            if(type==null || !typeof(IEngineeringService).IsAssignableFrom(type)) throw new NotSupportedException("Official service unavailable in installed API: "+typeName);
            if(owner is not IEngineeringServiceProvider) throw new NotSupportedException("Selected object is not a service provider.");
            try {return typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(type).Invoke(owner,null) ?? throw new NotSupportedException("Service is unsupported on selected object: "+typeName);}
            catch(TargetInvocationException ex) {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();throw;}
        }
        internal static JsonObject Result(object? result)
        {
            int visited=0;
            JsonNode? Capture(object? value,int depth) {
                if(value==null) return null;
                if(++visited>5000 || depth>12) throw new InvalidOperationException("Native result exceeds diagnostic bounds; result not complete.");
                if(EngineeringScalarProperties.Scalar(value.GetType())) return EngineeringScalarProperties.Json(value);
                if(value is IEnumerable && value is not string) return new JsonArray(EngineeringGroupOperations.Items(value).Select(v=>Capture(v,depth+1)).ToArray());
                var row=EngineeringScalarProperties.Read(value);
                foreach(var field in new[]{"Messages","Errors","Warnings","Results"}) {
                    var prop=value.GetType().GetProperty(field);
                    if(prop?.GetMethod?.IsPublic==true && prop.GetIndexParameters().Length==0) row[field]=Capture(prop.GetValue(value),depth+1);
                }
                return row;
            }
            var captured=Capture(result,0);
            return new JsonObject { ["nativeResult"]=captured,["apiCallSuccess"]=true,["dataComplete"]=false,
                ["nativeFailureDetected"]=NativeOutcome.Failed(captured),
                ["scope"]="Native scalar result and bounded Messages/Errors/Warnings/Results. Complex fields remain excluded; API return is not validation or test success." };
        }
        internal static void AttachResult(JsonObject meta,object? result)
        {
            var evidence=Result(result);meta["result"]=evidence;meta["apiCallSuccess"]=true;
            var enumType = result?.GetType().GetProperty("State")?.PropertyType.FullName;
            if (enumType != null && TiaMcp.Adapters.Contracts.NativeResultStates.EnumTypes.Contains(enumType))
            {
                var state = evidence["nativeResult"]?["values"]?["State"] ?? evidence["nativeResult"]?["State"];
                TiaMcp.Logic.V4.NativeResultState.Record(meta, state?.ToString(), meta["mayHaveChanged"]?.GetValue<bool>() == true,
                    messages: evidence["nativeResult"]?["Messages"]);
                meta["nativeStateType"] = enumType;
                meta["nativeSuccessVerified"] = TiaMcp.Logic.V4.NativeResultState.Succeeded(meta);
                if (!TiaMcp.Logic.V4.NativeResultState.Succeeded(meta)) meta["operationSuccess"] = false;
            }
            if(evidence["nativeFailureDetected"]!.GetValue<bool>())meta["operationSuccess"]=false;
        }
    }
}
