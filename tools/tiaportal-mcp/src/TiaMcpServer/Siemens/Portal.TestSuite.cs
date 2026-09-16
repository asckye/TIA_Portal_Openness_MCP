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
        private (object Group,object Collection,string Namespace,string Executor) ExactTestSuite(string category)
        {
            var shape=category switch {
                "styleGuide"=>("StyleGuideGroup","RuleSets","StyleGuide","RuleSetExecutor"),
                "application"=>("ApplicationTestGroup","TestCases","ApplicationTest","TestCaseExecutor"),
                "system"=>("SystemTestGroup","SystemTestCases","SystemTest","SystemTestCaseExecutor"),
                _=>throw new ArgumentException("category must be styleGuide/application/system.")};
            var service=OfficialServiceAccess.Require(_project!,"Siemens.Engineering.TestSuite.TestSuiteService","Siemens.Engineering.TestSuite");
            var group=EngineeringGroupOperations.Get(service,shape.Item1);
            return (group,EngineeringGroupOperations.Get(group,shape.Item2),"Siemens.Engineering.TestSuite."+shape.Item3,shape.Item4);
        }
        public ResponseMessage ReadTestSuiteCases(string category,string name="",int offset=0,int limit=100)
            =>RunHmiStepTool("ReadTestSuiteCases",meta=>{
                if(offset<0||limit<1||limit>500)throw new ArgumentException("offset>=0, limit 1..500 required.");
                var root=ExactTestSuite(category);
                var all=string.IsNullOrEmpty(name) ? EngineeringGroupOperations.Items(root.Collection).ToArray() : new[]{EngineeringGroupOperations.Find(root.Collection,name) ?? throw new InvalidOperationException("Exact test/rule set not found.")};
                meta["records"]=new JsonArray(all.Skip(offset).Take(limit).Select(x=>(JsonNode)EngineeringObjectAddress.Read(x)).ToArray());
                meta["expectedCount"]=all.Length;meta["actualCount"]=Math.Max(0,Math.Min(limit,all.Length-offset));
                meta["nextOffset"]=offset+limit<all.Length ? offset+limit : (int?)null;meta["truncated"]=offset+limit<all.Length;
                meta["apiCallSuccess"]=true;meta["dataComplete"]=false;meta["scope"]="Native case/rule-set scalar properties; definition export is separate. Live offset pagination.";
                return "Native Siemens Test Suite cases read; no test executed.";
            });
        public ResponseMessage ExchangeTestSuiteCase(string category,string action,string name,string filePath="",string importOptions="None",string loadOptions="",bool dryRun=true)
            =>RunHmiStepTool("ExchangeTestSuiteCase",meta=>{
                if(!new[]{"import","export","delete"}.Contains(action))throw new ArgumentException("action must be import/export/delete.");
                var root=ExactTestSuite(category);var target=EngineeringGroupOperations.Find(root.Collection,name);
                if(action!="import"&&target==null)throw new InvalidOperationException("Exact test/rule set not found.");
                using var access=action!="export"&&!dryRun ? AcquireHmiEditAccess() : null;
                FileInfo? file=null;System.Reflection.MethodInfo? load=null;object? options=null;object? loadValue=null;
                if(action=="export")file=NativeFileOutput.Plan(filePath);
                if(action=="import") {
                    file=new FileInfo(filePath);if(!file.Exists)throw new FileNotFoundException("Test definition file not found.");
                    load=root.Collection.GetType().GetMethods().SingleOrDefault(m=>m.Name=="LoadFromFile"&&m.GetParameters().Length==3&&m.GetParameters()[0].ParameterType==typeof(FileInfo)) ?? throw new NotSupportedException("Native test definition import unavailable.");
                    options=EngineeringScalarProperties.ConvertValue(JsonValue.Create(importOptions),load.GetParameters()[1].ParameterType);
                    loadValue=EngineeringScalarProperties.ConvertValue(JsonValue.Create(loadOptions),load.GetParameters()[2].ParameterType);
                }
                meta["dryRun"]=dryRun;meta["mayHaveChanged"]=false;meta["mayHaveWrittenFiles"]=false;meta["expectedName"]=name;
                if(dryRun)return "Test Suite definition exchange preview; import may contain multiple definitions and native import options govern replacements.";
                meta["mayHaveChanged"]=action!="export";meta["mayHaveWrittenFiles"]=action=="export";
                object? result;
                if(action=="import")result=EngineeringGroupOperations.Call(root.Collection,"LoadFromFile",load!.GetParameters().Select(p=>p.ParameterType).ToArray(),file!,options!,loadValue!);
                else if(action=="export")result=EngineeringGroupOperations.Call(target!,"SaveToFile",new[]{typeof(FileInfo)},file!);
                else result=EngineeringGroupOperations.Call(target!,"Delete",Type.EmptyTypes);
                OfficialServiceAccess.AttachResult(meta,result);
                if(action=="export")meta["file"]=NativeFileOutput.Verify(file!);
                else {
                    var after=EngineeringGroupOperations.Find(root.Collection,name);
                    if(action=="delete" ? after!=null : after==null)throw new InvalidOperationException("Expected test definition presence after native operation was not verified.");
                    meta["expectedPresenceVerified"]=true;if(after!=null)meta["after"]=EngineeringScalarProperties.Read(after);
                }
                return "Native test definition operation returned; presence/file checks do not prove test semantics. No test run or automatic save.";
            });
        public ResponseMessage RunTestSuiteCase(string category,string name,bool confirmExternalExecution=false,bool dryRun=true)
            =>RunHmiStepTool("RunTestSuiteCase",meta=>{
                var root=ExactTestSuite(category);var target=EngineeringGroupOperations.Find(root.Collection,name) ?? throw new InvalidOperationException("Exact test/rule set not found; unbounded execution refused.");
                var executor=OfficialServiceAccess.Require(root.Group,root.Namespace+"."+root.Executor,"Siemens.Engineering.TestSuite");
                var method=executor.GetType().GetMethod("Run",new[]{target.GetType()}) ?? throw new NotSupportedException("Native single-case execution unavailable.");
                meta["before"]=EngineeringObjectAddress.Read(target);meta["dryRun"]=dryRun;meta["mayHaveExternalEffects"]=false;
                if(dryRun)return "Native test preview. Application/system tests may start simulation or communicate with configured servers; execution needs confirmExternalExecution=true. No test executed.";
                if(category!="styleGuide"&&!confirmExternalExecution)throw new InvalidOperationException("Explicit confirmation of configured simulation/server effects required.");
                meta["mayHaveExternalEffects"]=category!="styleGuide";
                var result=EngineeringGroupOperations.Call(executor,"Run",new[]{target.GetType()},target);
                OfficialServiceAccess.AttachResult(meta,result);
                var state=EngineeringGroupOperations.Get(result,"State").ToString();meta["nativeState"]=state;
                bool passed=state=="Success"&&!meta["result"]!["nativeFailureDetected"]!.GetValue<bool>();
                meta["testPassed"]=passed;meta["operationSuccess"]=passed;
                return "Native Test Suite execution returned. Check testPassed and complete diagnostics; no automatic project save or Portal close.";
            });
    }
}
