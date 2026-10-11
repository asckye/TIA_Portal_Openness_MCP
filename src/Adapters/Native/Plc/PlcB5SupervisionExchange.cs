using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using TiaMcpServer.ModelContextProtocol;
using EngineeringGroupOperations = TiaMcp.Adapters.Native.Plc.PlcGroupOperations;
using EngineeringScalarProperties = TiaMcp.Adapters.Native.Plc.PlcB5Scalar;
using NativeResultState = TiaMcp.Adapters.Native.Plc.PlcB5Result;
using NativeFileOutput = TiaMcp.Adapters.Native.Plc.PlcSourceFileOutput;
using ResponseMeta = TiaMcp.Adapters.Native.Plc.PlcB5Meta;
using PortalException = TiaMcp.Adapters.Contracts.PlcSoftwareException;

namespace TiaMcp.Adapters.Native.Plc
{
    internal sealed partial class PlcB5Adapter
    {
#if PLC_SUPERVISION
        public PlcB5Reply ExchangePlcSupervisions(string softwarePath,string action,string filePath,string importOptions="None",bool dryRun=true)
            =>RunTool("ExchangePlcSupervisions",meta=>{
                try
                {
                var method=action switch {"export"=>"ExportSupervisionsToXlsx","import"=>"ImportSupervisionsFromXlsx","importSettings"=>"ImportSupervisionSettingsFromXlsx",_=>throw new ArgumentException("action must be export/import/importSettings.")};
                bool write=action!="export"; using var access=!dryRun&&write ? _session.AcquireHmiEditAccess() : null;
                var plc=_session.ExactPlcForEngineering(softwarePath,!dryRun&&write);
                var service=RequireOfficialService(plc,"Siemens.Engineering.SW.Supervision.SupervisionProvider","Siemens.Engineering.Step7");
                var file=write ? new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath)) : NativeFileOutput.Plan(filePath);
                if(write&&!file.Exists) throw new FileNotFoundException("Supervision input not found.");
                // importOptions applies only to the two imports; export does not parse it.
                var options=write ? (ImportOptions)EngineeringScalarProperties.ConvertValue(PlcB5Scalar.Value(string.IsNullOrWhiteSpace(importOptions) ? "None" : importOptions),typeof(ImportOptions))! : ImportOptions.None;
                var signature=write ? new[]{typeof(FileInfo),typeof(ImportOptions)} : new[]{typeof(FileInfo)};
                if(service.GetType().GetMethod(method,signature)==null) throw new NotSupportedException("Native supervision exchange signature unavailable.");
                meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;meta["mayHaveWrittenFiles"]=false;
                if(dryRun)return "ProDiag native exchange preview; no import/export performed.";
                meta["mayHaveChanged"]=write;meta["mayHaveWrittenFiles"]=!write;
                var result=write ? EngineeringGroupOperations.Call(service,method,signature,file,options) : EngineeringGroupOperations.Call(service,method,signature,file);
                AttachResult(meta,result);
                var state=result?.GetType().GetProperty("State")?.GetValue(result)?.ToString();
                meta["nativeLogFile"]=(result?.GetType().GetProperty("LogFilePath")?.GetValue(result) as FileInfo)?.FullName;
                meta["nativeState"]=state;meta["nativeSuccessVerified"]=TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(TiaMcp.Adapters.Contracts.NativeResultStates.SupervisionXlsx, state);
                if(!TiaMcp.Adapters.Contracts.NativeResultStates.Succeeded(TiaMcp.Adapters.Contracts.NativeResultStates.SupervisionXlsx, state)) meta["operationSuccess"]=false;
                if(!write)meta["file"]=NativeFileOutput.Verify(file);
                return "Native ProDiag exchange returned; inspect native state and diagnostics. No save/compile/download.";
                }
                catch (Exception ex) { RecordOptionalFailure(meta, ex); throw; }
            });

#endif
    }
}
