using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.Adapters.Native.Hmi
{
    internal static class HmiFiles
    {
        internal static HmiObject Export(object target, string path, string exportOptions = "WithDefaults") => new HmiObject(EngineeringFileExport.Export(target, path, true, HmiValues.ExportFailure ?? throw new InvalidOperationException("HMI export failure recorder unavailable."), HmiExchangePolicy.ExportOption(exportOptions)));
        internal static FileInfo RequireExistingInputFile(string path, string parameter)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException(parameter + " must be an absolute file path.");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) throw new FileNotFoundException(parameter + " does not exist or is empty.", path);
            return file;
        }
        internal static HmiObject FileRow(FileInfo file) => new HmiObject(PlcSourceFileOutput.Row(file));
        internal static HmiObject Result(object? result)
        {
            int visited=0;
            object? Capture(object? value,int depth) {
                if(value==null) return null;
                if(++visited>5000 || depth>12) throw new InvalidOperationException("Native result exceeds diagnostic bounds; result not complete.");
                if(HmiScalar.Scalar(value.GetType())) return HmiScalar.Json(value);
                if(value is IEnumerable && value is not string) return new HmiArray(PlcGroupOperations.Items(value).Select(v=>Capture(v,depth+1)).ToArray());
                var row=HmiScalar.Read(value);
                foreach(var field in new[]{"Messages","Errors","Warnings","Results"}) {
                    var prop=value.GetType().GetProperty(field);
                    if(prop?.GetMethod?.IsPublic==true && prop.GetIndexParameters().Length==0) row[field]=Capture(prop.GetValue(value),depth+1);
                }
                return row;
            }
            var captured=Capture(result,0);
            return new HmiObject { ["nativeResult"]=captured,["apiCallSuccess"]=true,["dataComplete"]=false,
                ["nativeFailureDetected"]=Failed(captured),
                ["scope"]="Native scalar result and bounded Messages/Errors/Warnings/Results. Complex fields remain excluded; API return is not validation or test success." };
        }
        internal static void AttachResult(HmiObject meta, object? result)
        {
            var evidence = Result(result); meta["result"] = evidence; meta["apiCallSuccess"] = true;
            var enumType = result?.GetType().GetProperty("State")?.PropertyType.FullName;
            if (enumType != null && NativeResultStates.EnumTypes.Contains(enumType))
            {
                var captured = evidence["nativeResult"];
                var state = captured?.Field("values")?.Field("State") ?? captured?.Field("State");
                meta["nativeResultReturned"] = true; meta["nativeState"] = state?.ToString();
                meta["mayHaveChanged"] = meta["mayHaveChanged"] is true;
                meta["logFilePath"] = null; meta["nativeMessages"] = captured?.Field("Messages")?.DeepClone();
                meta["nativeStateType"] = enumType;
                var kind = NativeResultStates.Classify(enumType, state?.ToString());
                bool success = kind == NativeStateKind.Success || kind == NativeStateKind.Warning;
                meta["nativeSuccessVerified"] = success;
                if (!success) meta["operationSuccess"] = false;
            }
            if (evidence["nativeFailureDetected"] is true) meta["operationSuccess"] = false;
        }
        private static bool Failed(object? value)
        {
            if (value is bool ok) return !ok;
            if (value is IList list) return list.Cast<object?>().Any(x => x is IDictionary<string, object?> && Failed(x));
            if (value is not IDictionary<string, object?> row) return false;
            var values = row.Field("values") as IDictionary<string, object?> ?? row;
            if (values.Field("State") is string state && new[] { "Error", "Failed", "Failure", "Aborted", "Canceled", "Cancelled" }.Contains(state, StringComparer.OrdinalIgnoreCase)) return true;
            if (values.Field("ErrorCount") is int count && count > 0) return true;
            if (row.Field("Errors") is IList errors && errors.Count > 0) return true;
            return new[] { "Messages", "Results" }.Any(k => row.Field(k) != null && Failed(row.Field(k)));
        }
    }
}
