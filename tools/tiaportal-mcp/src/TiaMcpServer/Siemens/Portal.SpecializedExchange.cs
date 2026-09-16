using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ExchangePlcSupervisions(string softwarePath,string action,string filePath,string importOptions="None",bool dryRun=true)
            =>RunHmiStepTool("ExchangePlcSupervisions",meta=>{
                var method=action switch {"export"=>"ExportSupervisionsToXlsx","import"=>"ImportSupervisionsFromXlsx","importSettings"=>"ImportSupervisionSettingsFromXlsx",_=>throw new ArgumentException("action must be export/import/importSettings.")};
                bool write=action!="export"; using var access=!dryRun&&write ? AcquireHmiEditAccess() : null;
                var plc=ExactPlcForEngineering(softwarePath,!dryRun&&write);
                var service=OfficialServiceAccess.Require(plc,"Siemens.Engineering.SW.Supervision.SupervisionProvider","Siemens.Engineering.Step7");
                var file=write ? new FileInfo(filePath) : NativeFileOutput.Plan(filePath);
                if(write&&!file.Exists) throw new FileNotFoundException("Supervision input not found.");
                var options=(ImportOptions)EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOptions),typeof(ImportOptions))!;
                var signature=write ? new[]{typeof(FileInfo),typeof(ImportOptions)} : new[]{typeof(FileInfo)};
                if(service.GetType().GetMethod(method,signature)==null) throw new NotSupportedException("Native supervision exchange signature unavailable.");
                meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;meta["mayHaveWrittenFiles"]=false;
                if(dryRun)return "ProDiag native exchange preview; no import/export performed.";
                meta["mayHaveChanged"]=write;meta["mayHaveWrittenFiles"]=!write;
                var result=write ? EngineeringGroupOperations.Call(service,method,signature,file,options) : EngineeringGroupOperations.Call(service,method,signature,file);
                OfficialServiceAccess.AttachResult(meta,result);
                var state=result?.GetType().GetProperty("State")?.GetValue(result)?.ToString();
                meta["nativeLogFile"]=(result?.GetType().GetProperty("LogFilePath")?.GetValue(result) as FileInfo)?.FullName;
                meta["nativeState"]=state;meta["nativeSuccessVerified"]=state=="Success" || state=="Info";
                if(state=="Error" || state=="Failed" || state=="Failure") meta["operationSuccess"]=false;
                if(!write)meta["file"]=NativeFileOutput.Verify(file);
                return "Native ProDiag exchange returned; inspect native state and diagnostics. No save/compile/download.";
            });
        public ResponseMessage ExchangeCfcCharts(string softwarePath,string action,string filePath,string modelVersion,long filter,bool unattended=true,bool deleteAtTarget=false,bool dryRun=true)
            =>RunHmiStepTool("ExchangeCfcCharts",meta=>{
                if(action!="export"&&action!="import") throw new ArgumentException("action must be export/import.");
                if(string.IsNullOrWhiteSpace(modelVersion))throw new ArgumentException("Explicit S7TIA exchange model version required.");
                bool write=action=="import";using var access=write&&!dryRun ? AcquireHmiEditAccess() : null;
                var plc=ExactPlcForEngineering(softwarePath,write&&!dryRun);
                var service=OfficialServiceAccess.Require(plc,"Siemens.Engineering.SW.FunctionCharts.ChartProviderS7","Siemens.Engineering.CFC");
                var file=write ? new FileInfo(filePath) : NativeFileOutput.Plan(filePath);
                if(write&&!file.Exists)throw new FileNotFoundException("CFC input file not found.");
                var signature=write ? new[]{typeof(string),typeof(string),typeof(long),typeof(bool),typeof(bool)} : new[]{typeof(string),typeof(string),typeof(long),typeof(bool)};
                var method=write ? "Import" : "CompleteExport";
                if(service.GetType().GetMethod(method,signature)==null)throw new NotSupportedException("Native CFC exchange signature unavailable.");
                meta["dryRun"]=dryRun;meta["deleteAtTarget"]=deleteAtTarget;meta["mayHaveChanged"]=false;meta["mayHaveWrittenFiles"]=false;
                if(dryRun)return "Native CFC exchange preview. Import deleteAtTarget removes objects omitted from the input when explicitly enabled.";
                meta["mayHaveChanged"]=write;meta["mayHaveWrittenFiles"]=!write;
                var result=write ? EngineeringGroupOperations.Call(service,method,signature,file.FullName,modelVersion,filter,unattended,deleteAtTarget) : EngineeringGroupOperations.Call(service,method,signature,file.FullName,modelVersion,filter,unattended);
                OfficialServiceAccess.AttachResult(meta,result);if(result is bool accepted && !accepted)meta["operationSuccess"]=false;
                if(!write)meta["file"]=NativeFileOutput.Verify(file);
                return "Native CFC operation returned; no independent chart semantic verification or automatic save/compile/download.";
            });
    }
}
