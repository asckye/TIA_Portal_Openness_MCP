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
                // 2.7.46: importOptions only matters for the two imports; an empty value on export was parsed anyway and threw.
                var options=write ? (ImportOptions)EngineeringScalarProperties.ConvertValue(JsonValue.Create(string.IsNullOrWhiteSpace(importOptions) ? "None" : importOptions),typeof(ImportOptions))! : ImportOptions.None;
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
    }
}
