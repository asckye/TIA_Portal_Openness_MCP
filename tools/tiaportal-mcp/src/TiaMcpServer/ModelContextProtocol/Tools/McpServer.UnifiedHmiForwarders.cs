namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        public static ResponseMessage EnsureUnifiedHmiScreen(
            string hmiSoftwarePath,
            string screenName,
            uint width = 0,
            uint height = 0)
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).EnsureUnifiedHmiScreen(hmiSoftwarePath, screenName, width, height);

        public static ResponseMessage EnsureUnifiedHmiTagTable(
            string hmiSoftwarePath,
            string tagTableName)
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).EnsureUnifiedHmiTagTable(hmiSoftwarePath, tagTableName);

        public static ResponseMessage EnsureUnifiedHmiTag(
            string hmiSoftwarePath,
            string tagTableName,
            string tagName,
            string hmiDataType = "Bool",
            string plcName = "PLC_1",
            string plcTag = "",
            string connectionName = "",
            string address = "",
            bool requireVerifiedBinding = true)
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).EnsureUnifiedHmiTag(hmiSoftwarePath, tagTableName, tagName, hmiDataType, plcName, plcTag, connectionName, address, requireVerifiedBinding);

        public static ResponseObjectDescribe EnsureUnifiedHmiConnection(
            string hmiSoftwarePath,
            string connectionName = "HMI_Connection_1",
            string plcName = "PLC_1")
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).EnsureUnifiedHmiConnection(hmiSoftwarePath, connectionName, plcName);

        public static ResponseMessage EnsureUnifiedHmiScreenItem(
            string hmiSoftwarePath,
            string screenName,
            string itemName,
            string itemType = "Button",
            int left = 0,
            int top = 0,
            uint width = 120,
            uint height = 40,
            string text = "")
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).EnsureUnifiedHmiScreenItem(hmiSoftwarePath, screenName, itemName, itemType, left, top, width, height, text);

        public static ResponseMessage ApplyUnifiedHmiScreenDesignJson(
            string hmiSoftwarePath,
            string screenName,
            string designJson,
            bool strict = true)
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, designJson, strict);

        public static ResponseMessage BindUnifiedHmiButtonPressedTag(
            string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string tagName)
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).BindUnifiedHmiButtonPressedTag(hmiSoftwarePath, screenName, buttonName, tagName);

        public static ResponseMessage EnsureUnifiedHmiButtonEventHandler(
            string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType)
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).EnsureUnifiedHmiButtonEventHandler(hmiSoftwarePath, screenName, buttonName, eventType);

        public static ResponseObjectDescribe DescribeUnifiedHmiButtonEventScript(
            string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType,
            int maxMembers = 200)
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).DescribeUnifiedHmiButtonEventScript(hmiSoftwarePath, screenName, buttonName, eventType, maxMembers);

        public static ResponseMessage SetUnifiedHmiButtonEventScriptCode(
            string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType,
            string scriptCode,
            string globalDefinitionAreaScriptCode = "",
            bool async = false,
            bool syntaxCheck = false)
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).SetUnifiedHmiButtonEventScriptCode(hmiSoftwarePath, screenName, buttonName, eventType, scriptCode, globalDefinitionAreaScriptCode, async, syntaxCheck);

        public static ResponseMessage BuildUnifiedHmiButtonActionScript(
            string actionKind,
            string eventType,
            string targetTag = "",
            string targetScreen = "",
            string targetPopup = "")
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).BuildUnifiedHmiButtonActionScript(actionKind, eventType, targetTag, targetScreen, targetPopup);

        public static ResponseMessage EnsureUnifiedHmiButtonAction(
            string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType,
            string actionKind,
            string targetTag,
            bool syntaxCheck = false)
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).EnsureUnifiedHmiButtonAction(hmiSoftwarePath, screenName, buttonName, eventType, actionKind, targetTag, syntaxCheck);

        public static ResponseMessage BindUnifiedHmiTagDynamization(
            string hmiSoftwarePath,
            string screenName,
            string itemName,
            string propertyName,
            string tagName,
            string dataType = "Bool",
            string plcTag = "",
            string address = "")
            => ((UnifiedHmiTools)EngineServices.Get(typeof(UnifiedHmiTools))).BindUnifiedHmiTagDynamization(hmiSoftwarePath, screenName, itemName, propertyName, tagName, dataType, plcTag, address);
    }
}
