using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;

// Native members used by the 2.7.42 phase 6 ⑥-③ CFC tools (Portal.Cfc.cs), verified member by member against the installed
// V20 / V21 PublicAPI (Siemens.Engineering.CFC is a separate assembly on both versions; identical surface).
internal static class CfcShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var step7=Api("Siemens.Engineering.Step7","Siemens.Engineering");
        var cfc=Api("Siemens.Engineering.CFC","Siemens.Engineering");              // V20: inside the monolithic Siemens.Engineering.dll
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
        void Method(Assembly a,string t,string name,Type[] signature,string? returns=null)
        {
            var m=T(a,t).GetMethod(name,signature);
            check(m!=null && (returns==null || m.ReturnType.Name==returns),t+"."+name+"("+string.Join(",",signature.Select(x=>x.Name))+")"+(returns==null?"":" -> "+returns));
        }
        const string ns="Siemens.Engineering.SW.FunctionCharts.";
        var text=typeof(string); var i64=typeof(long); var boolean=typeof(bool); var secure=typeof(SecureString);
        check(T(core,"Siemens.Engineering.IEngineeringService").IsAssignableFrom(T(cfc,ns+"ChartProviderS7")),"ChartProviderS7 is an IEngineeringService (PlcSoftware.GetService)");
        check(T(cfc,ns+"ChartProviderS7").IsSubclassOf(T(cfc,ns+"ChartProvider")),"ChartProviderS7 derives from ChartProvider");
        check(T(core,"Siemens.Engineering.IEngineeringServiceProvider").IsAssignableFrom(T(step7,"Siemens.Engineering.SW.PlcSoftware")),"PlcSoftware is a service provider");
        Method(cfc,ns+"ChartProvider","CompleteExport",new[]{text,text,i64,boolean},"Void");
        Method(cfc,ns+"ChartProvider","SelectiveExport",new[]{text,typeof(string[]),text,i64,boolean},"Void");
        Method(cfc,ns+"ChartProvider","Import",new[]{text,text,i64,boolean,boolean},"Void");
        Method(cfc,ns+"ChartProvider","AddChartProtection",new[]{text,text},"Boolean");
        Method(cfc,ns+"ChartProvider","ChangeChartProtection",new[]{text,secure,text},"Boolean");
        Method(cfc,ns+"ChartProvider","GetChartProtection",new[]{text},"String");
        Method(cfc,ns+"ChartProvider","RemoveChartProtection",new[]{text,secure},"Boolean");
        Method(cfc,ns+"ChartProviderS7","ExportInstructionData",new[]{text},"Void");

        // ---- server side ----
        var portal=server.GetType("TiaMcpServer.Siemens.Portal",true)!;
        foreach(var tool in new[]{"ExchangeCfcCharts","ManageCfcChartProtection"}) check(portal.GetMethod(tool)!=null,"Portal."+tool+" exists");
        check(portal.GetMethod("ExchangeCfcCharts")!.GetParameters().Any(p=>p.Name=="chartNamesJson"),"ExchangeCfcCharts takes chartNamesJson (SelectiveExport, 2.7.42 typed retrofit)");
        foreach(var tool in new[]{"ExchangeCfcCharts","ManageCfcChartProtection"}) check(Equals(portal.GetMethod(tool)!.GetParameters().Single(p=>p.Name=="skipChartPreflight").DefaultValue,false),tool+" runs the CompleteExport preflight by default (2.7.43: TIA V21 crashed on a PLC without charts)");
        check(server.GetType("TiaMcpServer.Siemens.CfcLogic",true)!.GetMethod("InspectExport",BindingFlags.NonPublic|BindingFlags.Static)!=null,"CfcLogic.InspectExport parses the exchange ZIP (chart inventory)");
    }
}
