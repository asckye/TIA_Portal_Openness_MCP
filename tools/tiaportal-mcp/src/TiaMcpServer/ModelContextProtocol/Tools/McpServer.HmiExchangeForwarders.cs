namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {

        internal static ResponseCompileDiagnose CompileAndDiagnoseHmiCore(string softwarePath, string password)
            => CompileAndDiagnoseCore(softwarePath, password);
    }
}
