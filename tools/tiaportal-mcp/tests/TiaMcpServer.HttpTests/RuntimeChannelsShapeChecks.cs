using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Shape checks for the RuntimeChannels family. These channels do not use Openness;
// the native dependency is the official Siemens.Simatic.S7.Webserver.API client that
// ships next to the server exe. Every type/member S7WebApiChannel.cs calls is listed
// here, plus the tool-level safety defaults (preview + confirm flags).
internal static class RuntimeChannelsShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly api;
        try { api = Assembly.Load("Siemens.Simatic.S7.Webserver.API"); }
        catch (FileNotFoundException)
        {
            var path = Path.Combine(Path.GetDirectoryName(server.Location)!, "Siemens.Simatic.S7.Webserver.API.dll");
            check(File.Exists(path), "Siemens.Simatic.S7.Webserver.API.dll ships next to the server");
            api = Assembly.LoadFrom(path);
        }
        check(api.GetName().Version!.Major == 3, "S7 Webserver API client major version 3 (" + api.GetName().Version + ")");
        Type T(string n) => api.GetType(n, true)!;
        bool Has(Type t, string name, params Type[] sig) => t.GetMethod(name, sig) != null;

        const string ns = "Siemens.Simatic.S7.Webserver.API.";
        var handler = T(ns + "Services.RequestHandling.ApiHttpClientRequestHandler");
        var requestFactory = T(ns + "Services.RequestHandling.IApiRequestFactory");
        var responseChecker = T(ns + "Services.RequestHandling.IApiResponseChecker");
        var splitter = T(ns + "Services.RequestHandling.IApiRequestSplitter");
        // The test project does not reference System.Net.Http; resolve HttpClient through the handler's own constructor.
        var ctor = handler.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 5
            && c.GetParameters()[0].ParameterType.FullName == "System.Net.Http.HttpClient"
            && c.GetParameters()[1].ParameterType == requestFactory
            && c.GetParameters()[2].ParameterType == responseChecker
            && c.GetParameters()[3].ParameterType == splitter
            && c.GetParameters()[4].ParameterType.FullName == "Microsoft.Extensions.Logging.ILogger");
        check(ctor != null, "ApiHttpClientRequestHandler(HttpClient, IApiRequestFactory, IApiResponseChecker, IApiRequestSplitter, ILogger)");
        var httpClientType = ctor!.GetParameters()[0].ParameterType;
        check(T(ns + "Services.RequestHandling.ApiRequestFactory").GetConstructors().Any(c => c.GetParameters().Length == 3), "ApiRequestFactory(IIdGenerator, IApiRequestParameterChecker, ILogger)");
        check(T(ns + "Services.RequestHandling.ApiResponseChecker").GetConstructor(Type.EmptyTypes) != null, "ApiResponseChecker()");
        check(T(ns + "Services.RequestHandling.ApiRequestParameterChecker").GetConstructor(Type.EmptyTypes) != null, "ApiRequestParameterChecker()");
        check(T(ns + "Services.RequestHandling.ApiRequestSplitterByBytes").GetConstructors().Any(c => c.GetParameters().Length == 1), "ApiRequestSplitterByBytes(ILogger)");
        check(T(ns + "Services.IdGenerator.GUIDGenerator").GetConstructor(Type.EmptyTypes) != null, "GUIDGenerator()");

        var redundancy = T(ns + "Enums.ApiPlcRedundancyId");
        var opMode = T(ns + "Enums.ApiPlcOperatingMode");
        var representation = typeof(Nullable<>).MakeGenericType(T(ns + "Enums.ApiPlcDataRepresentation"));
        var ct = typeof(System.Threading.CancellationToken);
        check(Enum.GetNames(redundancy).Contains("StandardPLC"), "ApiPlcRedundancyId.StandardPLC");
        foreach (var name in new[] { "Run", "Stop", "Startup", "Hold", "Unknown" })
            check(Enum.GetNames(opMode).Contains(name), "ApiPlcOperatingMode." + name);

        check(Has(handler, "ApiLoginAsync", typeof(string), typeof(string), typeof(bool?), ct), "ApiLoginAsync(string,string,bool?,CancellationToken)");
        check(Has(handler, "ApiLogoutAsync", ct), "ApiLogoutAsync(CancellationToken)");
        check(Has(handler, "ApiPingAsync", ct), "ApiPingAsync(CancellationToken)");
        check(Has(handler, "ApiVersionAsync", ct), "ApiVersionAsync(CancellationToken)");
        check(Has(handler, "ApiGetPlcCpuTypeAsync", ct), "ApiGetPlcCpuTypeAsync(CancellationToken)");
        check(Has(handler, "PlcReadOperatingModeAsync", redundancy, ct), "PlcReadOperatingModeAsync(ApiPlcRedundancyId,CancellationToken)");
        check(Has(handler, "PlcRequestChangeOperatingModeAsync", opMode, redundancy, ct), "PlcRequestChangeOperatingModeAsync(ApiPlcOperatingMode,ApiPlcRedundancyId,CancellationToken)");
        check(Has(handler, "PlcReadModeSelectorStateAsync", redundancy, ct), "PlcReadModeSelectorStateAsync(ApiPlcRedundancyId,CancellationToken)");
        check(Has(handler, "PlcReadSystemTimeAsync", ct), "PlcReadSystemTimeAsync(CancellationToken)");
        check(Has(handler, "PlcReadRuntimeInformationAsync", ct), "PlcReadRuntimeInformationAsync(CancellationToken)");
        check(Has(handler, "PlcReadMemoryInformationAsync", ct), "PlcReadMemoryInformationAsync(CancellationToken)");
        check(Has(handler, "PlcProgramWriteAsync", typeof(string), typeof(object), representation, ct), "PlcProgramWriteAsync(string,object,ApiPlcDataRepresentation?,CancellationToken)");
        var read = handler.GetMethods().FirstOrDefault(m => m.Name == "PlcProgramReadAsync" && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[2].ParameterType == ct);
        check(read != null, "PlcProgramReadAsync<T>(string,ApiPlcDataRepresentation?,CancellationToken)");

        foreach (var (type, prop) in new[] {
            ("Models.Responses.ApiSingleStringResponse", "Result"),
            ("Models.Responses.ApiDoubleResponse", "Result"),
            ("Models.Responses.ApiTrueOnSuccessResponse", "Result"),
            ("Models.Responses.ApiReadOperatingModeResponse", "Result"),
            ("Models.Responses.ApiLoginResponse", "Result"),
            ("Models.Responses.ResponseResults.ApiTokenResult", "Token"),
            ("Models.Responses.ApiPlcReadModeSelectorStateResponse", "Result"),
            ("Models.Responses.ResponseResults.ApiPlcReadModeSelectorStateResult", "Mode_Selector"),
            ("Models.Responses.ApiPlcReadSystemTimeResponse", "Result") })
            check(T(ns + type).GetProperty(prop) != null, type + "." + prop);
        check(T(ns + "Models.Responses.ResponseResults.ApiPlcReadSystemTimeResult").GetField("Timestamp")?.FieldType == typeof(DateTime), "ApiPlcReadSystemTimeResult.Timestamp is a DateTime field");
        check(T(ns + "Exceptions.InvalidHttpRequestException").IsSubclassOf(typeof(Exception)), "InvalidHttpRequestException (HTTP 401 => re-login path)");
        var handlerType = httpClientType.Assembly.GetType("System.Net.Http.HttpClientHandler");
        check(handlerType?.GetProperty("ServerCertificateCustomValidationCallback") != null, "HttpClientHandler.ServerCertificateCustomValidationCallback (per-handler certificate policy, net48)");

        var tools = server.GetType("TiaMcpServer.ModelContextProtocol.McpServer", true)!;
        foreach (var name in new[] { "ReadPlcWebVars", "ReadPlcWebDiagnostics", "ReadUnifiedRuntimeTags", "ReadUnifiedRuntimeAlarms" })
            check(tools.GetMethod(name) != null && tools.GetMethod(name)!.GetParameters().All(p => p.Name != "dryRun"), name + " exposed (read-only, no dryRun)");
        foreach (var name in new[] { "WritePlcWebVars", "WriteUnifiedRuntimeTags", "UnifiedOpenPipeRequest" })
        {
            var method = tools.GetMethod(name)!;
            check(Equals(method.GetParameters().Single(p => p.Name == "dryRun").DefaultValue, true), name + " defaults to preview");
            check(Equals(method.GetParameters().Single(p => p.Name == "confirmWrite").DefaultValue, false), name + " requires explicit confirmWrite");
        }
        var modeTool = tools.GetMethod("SetPlcWebOperatingMode")!;
        check(Equals(modeTool.GetParameters().Single(p => p.Name == "dryRun").DefaultValue, true), "SetPlcWebOperatingMode defaults to preview");
        check(Equals(modeTool.GetParameters().Single(p => p.Name == "confirmModeChange").DefaultValue, false), "SetPlcWebOperatingMode requires explicit confirmModeChange");
        foreach (var name in new[] { "ReadPlcWebVars", "WritePlcWebVars", "ReadPlcWebDiagnostics", "SetPlcWebOperatingMode" })
            check(Equals(tools.GetMethod(name)!.GetParameters().Single(p => p.Name == "ignoreCertificateErrors").DefaultValue, false), name + " validates certificates by default");
    }
}
