namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        public static ResponseStringList GetHmiScreens(string softwarePath)
            => ((HmiExchangeTools)EngineServices.Get(typeof(HmiExchangeTools))).GetHmiScreens(softwarePath);

        public static ResponseStringList GetHmiTagTables(string softwarePath)
            => ((HmiExchangeTools)EngineServices.Get(typeof(HmiExchangeTools))).GetHmiTagTables(softwarePath);

        public static ResponseStringList GetHmiTags(string softwarePath,
            string tagTableName = "")
            => ((HmiExchangeTools)EngineServices.Get(typeof(HmiExchangeTools))).GetHmiTags(softwarePath, tagTableName);

        public static ResponseStringList GetHmiConnections(string softwarePath)
            => ((HmiExchangeTools)EngineServices.Get(typeof(HmiExchangeTools))).GetHmiConnections(softwarePath);

        public static ResponseExportFile ExportHmiTagTable(string softwarePath,
            string tagTableName,
            string exportPath)
            => ((HmiExchangeTools)EngineServices.Get(typeof(HmiExchangeTools))).ExportHmiTagTable(softwarePath, tagTableName, exportPath);

        public static ResponseMessage ImportHmiScreen(string softwarePath,
            string folderPath,
            string importPath)
            => ((HmiExchangeTools)EngineServices.Get(typeof(HmiExchangeTools))).ImportHmiScreen(softwarePath, folderPath, importPath);

        public static ResponseMessage ImportHmiTagTable(string softwarePath,
            string folderPath,
            string importPath)
            => ((HmiExchangeTools)EngineServices.Get(typeof(HmiExchangeTools))).ImportHmiTagTable(softwarePath, folderPath, importPath);

        public static ResponseHmiProgramInfo GetHmiProgramInfo(string softwarePath)
            => ((HmiDescribeTools)EngineServices.Get(typeof(HmiDescribeTools))).GetHmiProgramInfo(softwarePath);

        public static ResponseObjectDescribe DescribeHmiTagTable(string softwarePath,
            string tagTableName,
            int maxMembers = 200)
            => ((HmiDescribeTools)EngineServices.Get(typeof(HmiDescribeTools))).DescribeHmiTagTable(softwarePath, tagTableName, maxMembers);

        public static ResponseObjectDescribe DescribeHmiTag(string softwarePath,
            string tagTableName,
            string tagName,
            int maxMembers = 200)
            => ((HmiDescribeTools)EngineServices.Get(typeof(HmiDescribeTools))).DescribeHmiTag(softwarePath, tagTableName, tagName, maxMembers);

        public static ResponseObjectDescribe DescribeHmiScreenItem(string softwarePath,
            string screenName,
            string itemName,
            int maxMembers = 200)
            => ((HmiDescribeTools)EngineServices.Get(typeof(HmiDescribeTools))).DescribeHmiScreenItem(softwarePath, screenName, itemName, maxMembers);

        internal static ResponseCompileDiagnose CompileAndDiagnoseHmiCore(string softwarePath, string password)
            => CompileAndDiagnoseCore(softwarePath, password);
    }
}
