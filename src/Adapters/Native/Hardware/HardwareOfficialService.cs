using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Siemens.Engineering;
namespace TiaMcp.Adapters.Hardware
{
    internal static class HardwareOfficialService
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
        internal static Dictionary<string, object?> Result(object? result)
        {
            int visited=0;
            object? Capture(object? value,int depth) {
                if(value==null) return null;
                if(++visited>5000 || depth>12) throw new InvalidOperationException("Native result exceeds diagnostic bounds; result not complete.");
                if(HardwareScalarEvidence.Scalar(value.GetType())) return HardwareAddressingPolicy.ScalarValue(value);
                if(value is IEnumerable && value is not string) return new List<object?>(HardwareGroupOperations.Items(value).Select(v=>Capture(v,depth+1)).ToArray());
                var row=HardwareScalarEvidence.Read(value);
                foreach(var field in new[]{"Messages","Errors","Warnings","Results"}) {
                    var prop=value.GetType().GetProperty(field);
                    if(prop?.GetMethod?.IsPublic==true && prop.GetIndexParameters().Length==0) row[field]=Capture(prop.GetValue(value),depth+1);
                }
                return row;
            }
            var captured=Capture(result,0);
            return new Dictionary<string, object?> { ["nativeResult"]=captured,["apiCallSuccess"]=true,["dataComplete"]=false,
                ["nativeFailureDetected"]=Failed(captured),
                ["scope"]="Native scalar result and bounded Messages/Errors/Warnings/Results. Complex fields remain excluded; API return is not validation or test success." };
        }
        internal static void AttachResult(Dictionary<string, object?> meta, object? result)
        {
            var evidence = Result(result); meta["result"] = evidence; meta["apiCallSuccess"] = true;
            var enumType = result?.GetType().GetProperty("State")?.PropertyType.FullName;
            if (enumType != null && TiaMcp.Adapters.Contracts.NativeResultStates.EnumTypes.Contains(enumType))
            {
                var row = evidence["nativeResult"] as Dictionary<string, object?>;
                var values = row != null && row.TryGetValue("values", out var scalar) ? scalar as Dictionary<string, object?> : row;
                string? state = values != null && values.TryGetValue("State", out var raw) ? raw?.ToString() : null;
                meta["nativeResultReturned"] = true; meta["nativeState"] = state;
                meta["mayHaveChanged"] = meta.TryGetValue("mayHaveChanged", out var changed) && Equals(changed, true);
                meta["logFilePath"] = null;
                meta["nativeMessages"] = row != null && row.TryGetValue("Messages", out var messages) ? messages : null;
                meta["nativeStateType"] = enumType;
                bool ok = TiaMcp.Adapters.Contracts.NativeResultStates.Classify(enumType, state) is TiaMcp.Adapters.Contracts.NativeStateKind.Success or TiaMcp.Adapters.Contracts.NativeStateKind.Warning;
                meta["nativeSuccessVerified"] = ok; if (!ok) meta["operationSuccess"] = false;
            }
            if (Equals(evidence["nativeFailureDetected"], true)) meta["operationSuccess"] = false;
        }
        private static bool Failed(object? node)
        {
            if (node is bool ok) return !ok;
            if (node is IEnumerable<object?> array) return array.Any(x => x is Dictionary<string, object?> && Failed(x));
            if (node is not Dictionary<string, object?> row) return false;
            var values = row.TryGetValue("values", out var value) ? value as Dictionary<string, object?> ?? row : row;
            if (values.TryGetValue("State", out var state) && state is string name && new[] { "Error", "Failed", "Failure", "Aborted", "Canceled", "Cancelled" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
            if (values.TryGetValue("ErrorCount", out var count) && Convert.ToInt32(count) > 0) return true;
            if (row.TryGetValue("Errors", out var errors) && errors is System.Collections.ICollection list && list.Count > 0) return true;
            return new[] { "Messages", "Results" }.Any(key => row.TryGetValue(key, out var child) && child != null && Failed(child));
        }
    }
}
