using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;

// Native members used by the 2.7.42 phase 6 ⑥-③ CFC tools (Siemens/Services/CfcService.cs), verified member by member against the installed
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
        var portal=EngineSurface.For(server);
        foreach(var tool in new[]{"ExchangeCfcCharts","ManageCfcChartProtection"}) check(portal.Method(tool)!=null,"CfcService."+tool+" exists");
        check(portal.Method("ExchangeCfcCharts")!.GetParameters().Any(p=>p.Name=="chartNamesJson"),"ExchangeCfcCharts takes chartNamesJson (SelectiveExport, 2.7.42 typed retrofit)");
        foreach(var tool in new[]{"ExchangeCfcCharts","ManageCfcChartProtection"}) check(Equals(portal.Method(tool)!.GetParameters().Single(p=>p.Name=="skipChartPreflight").DefaultValue,false),tool+" runs the CompleteExport preflight by default (2.7.43: TIA V21 crashed on a PLC without charts)");
        check(Program.FindServerType(server, "TiaMcpServer.Siemens.CfcLogic").GetMethod("InspectExport",BindingFlags.NonPublic|BindingFlags.Static)!=null,"CfcLogic.InspectExport parses the exchange ZIP (chart inventory)");

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var service = server.GetType("TiaMcpServer.Siemens.Services.CfcService", true)!;
        var tools = server.GetType("TiaMcpServer.ModelContextProtocol.CfcTools", true)!;
        foreach (var type in new[] { service, tools })
            check(type.IsSealed && !type.GetInterfaces().Any(item => item.Name == "IDisposable" || item.Name == "IAsyncDisposable"),
                type.FullName + " is a non-disposable singleton class");
        var provider = (IServiceProvider)server.GetType("TiaMcpServer.EngineServices", true)!.GetProperty("Provider", all)!.GetValue(null)!;
        var session = provider.GetService(server.GetType("TiaMcpServer.Siemens.IEngineeringSession", true)!);
        check(session != null && ReferenceEquals(session, provider.GetService(server.GetType("TiaMcpServer.Siemens.Portal", true)!)),
            "IEngineeringSession resolves the registered Portal singleton");
        foreach (var name in new[] { "ExchangeCfcCharts", "ManageCfcChartProtection" })
        {
            var method = portal.Method(name);
            var tool = portal.Tool(name);
            var target = portal.Target(method);
            check(method.DeclaringType == service && tool.DeclaringType == tools && !tool.IsStatic
                && ReferenceEquals(target, portal.Target(method)) && ReferenceEquals(portal.Target(tool), portal.Target(tool)),
                "CFC surface resolves the service and tool singletons: " + name);
            check(ReferenceEquals(service.GetField("_session", all)!.GetValue(target), session)
                && ReferenceEquals(tools.GetField("_cfc", all)!.GetValue(portal.Target(tool)), target),
                "CFC tool uses the service with the shared session: " + name);
            bool callsService = EngineSurface.MethodFamily(tool).Where(callback =>
                callback.Name.StartsWith("<" + tool.Name + ">", StringComparison.Ordinal)).Any(callback => {
                    var il = callback.GetMethodBody()!.GetILAsByteArray()!;
                    return Enumerable.Range(0, Math.Max(0, il.Length - 4)).Any(index =>
                        (il[index] == 0x28 || il[index] == 0x6f) && BitConverter.ToInt32(il, index + 1) == method.MetadataToken);
                });
            EngineSurface.CheckIl(check, callsService, "CfcTools calls CfcService: " + name, tool, method);
            check(server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!.GetMethod(name, all) == null,
                "CFC tool needs no static CLI forwarder: " + name);
        }
    }
}
