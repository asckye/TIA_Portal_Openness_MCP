using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class UnifiedHmiService : UnifiedHmiPortService
    {
        public UnifiedHmiService() { }
        internal UnifiedHmiService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage EnsureStartStopUnifiedHmi(
            string hmiSoftwarePath,
            string screenName = "Main",
            string tagTableName = UnifiedHmiDefaults.DefaultTagTableName,
            string plcName = "PLC_1",
            string connectionName = "HMI_Connection_1")
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "SetUnifiedHmiRuntimeState", HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, TagTableName = tagTableName, PlcName =
                        plcName, ConnectionName = connectionName, DryRun = false
                });
        public ResponseMessage EnsureUnifiedHmiScreen(string hmiSoftwarePath, string screenName, uint width = 0, uint height = 0, bool dryRun = true)
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "EnsureUnifiedHmiScreen", HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, Width = width, Height = height, DryRun =
                        dryRun
                });
        public ResponseMessage EnsureUnifiedHmiTagTable(string hmiSoftwarePath, string tagTableName, bool dryRun = true)
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "EnsureUnifiedHmiTagTable", HmiSoftwarePath = hmiSoftwarePath, TagTableName = tagTableName, DryRun = dryRun });
        public ResponseMessage EnsureUnifiedHmiTag(string hmiSoftwarePath, string tagTableName, string tagName, string hmiDataType = "", string plcName =
                "", string plcTag = "", string connectionName = "", string address = "", bool requireVerifiedBinding = true, bool dryRun = true)
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "EnsureUnifiedHmiTag", HmiSoftwarePath = hmiSoftwarePath, TagTableName = tagTableName, TagName = tagName, HmiDataType =
                        hmiDataType, PlcName = plcName, PlcTag = plcTag, ConnectionName = connectionName, Address = address, RequireVerifiedBinding =
                        requireVerifiedBinding, DryRun = dryRun
                });
        public ModelContextProtocol
            .ResponseObjectDescribe EnsureUnifiedHmiConnection(string hmiSoftwarePath, string connectionName = "HMI_Connection_1", string plcName = "PLC_1",
                bool dryRun = true)
            => Invoke<ModelContextProtocol
                .ResponseObjectDescribe>(new UnifiedHmiRequest {
                    Operation = "EnsureUnifiedHmiConnection", HmiSoftwarePath = hmiSoftwarePath, ConnectionName = connectionName, PlcName = plcName, DryRun =
                        dryRun
                });
        public ResponseMessage EnsureUnifiedHmiScreenItem(string hmiSoftwarePath, string screenName, string itemName, string itemType = "Button", int left = 0,
                int top = 0, uint width = 120, uint height = 40, string text = "", bool dryRun = true)
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "EnsureUnifiedHmiScreenItem", HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, ItemName = itemName, ItemType =
                        itemType, Left = left, Top = top, Width = width, Height = height, Text = text, DryRun = dryRun
                });
        public ResponseMessage ReadUnifiedHmiTexts(string hmiSoftwarePath, string screenName, string itemName, string textProperty)
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "GetUnifiedHmiTexts", HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, ItemName = itemName, TextProperty =
                        textProperty, DryRun = true
                });
        public ResponseMessage ApplyUnifiedHmiScreenDesignJson(string hmiSoftwarePath, string screenName, string designJson, bool strict = true, bool dryRun =
                true)
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "ApplyUnifiedHmiScreenDesign", HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, DesignJson = designJson, Strict =
                        strict, DryRun = dryRun
                });
        public ResponseMessage BindUnifiedHmiButtonPressedTag(string hmiSoftwarePath, string screenName, string buttonName, string tagName, bool dryRun = true)
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "BindUnifiedHmiButtonPressedTag", HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, ButtonName = buttonName, TagName =
                        tagName, DryRun = dryRun
                });
        public List<string> ListUnifiedHmiApiTypes(string nameContains = "", int limit = 500)
            => Invoke<List<string>>(new UnifiedHmiRequest { Operation = "ListUnifiedHmiApiTypes", NameContains = nameContains, Limit = limit, DryRun = true });
        public ResponseMessage EnsureUnifiedHmiButtonEventHandler(string hmiSoftwarePath, string screenName, string buttonName, string eventType, bool dryRun =
                true)
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "EnsureUnifiedHmiButtonEventHandler", HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, ButtonName = buttonName,
                        EventType = eventType, DryRun = dryRun
                });
        public ModelContextProtocol
            .ResponseObjectDescribe DescribeUnifiedHmiButtonEventScript(string hmiSoftwarePath, string screenName, string buttonName, string eventType,
                int maxMembers = 200)
            => Invoke<ModelContextProtocol
                .ResponseObjectDescribe>(new UnifiedHmiRequest {
                    Operation = "DescribeUnifiedHmiButtonEventScript", HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, ButtonName = buttonName,
                        EventType = eventType, MaxMembers = maxMembers, DryRun = true
                });
        public ResponseMessage SetUnifiedHmiButtonEventScriptCode(string hmiSoftwarePath, string screenName, string buttonName, string eventType,
                string scriptCode, string? globalDefinitionAreaScriptCode = null, bool? async = null, bool syntaxCheck = false)
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "SetUnifiedHmiButtonEventScriptCode", HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, ButtonName = buttonName,
                        EventType = eventType, ScriptCode = scriptCode, GlobalDefinitionAreaScriptCode = globalDefinitionAreaScriptCode, Async = @async,
                        SyntaxCheck = syntaxCheck, DryRun = false
                });
        public ResponseMessage ApplyUnifiedHmiButtonAction(string software, string screen, string button, string eventType, string scriptCode, bool syntaxCheck)
            => Invoke<ResponseMessage>(new UnifiedHmiRequest { Operation = "EnsureUnifiedHmiButtonAction", HmiSoftwarePath = software, ScreenName = screen,
                ButtonName = button, EventType = eventType, ScriptCode = scriptCode, SyntaxCheck = syntaxCheck, DryRun = false });
        public ResponseMessage EnsureUnifiedHmiDynamization(string hmiSoftwarePath, string screenName, string itemName, string propertyName,
                string dynamizationType = "", bool dryRun = true)
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "EnsureUnifiedHmiDynamization", HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, ItemName = itemName, PropertyName =
                        propertyName, DynamizationType = dynamizationType, DryRun = dryRun
                });
        public ResponseMessage BindUnifiedHmiTagDynamization(string hmiSoftwarePath, string screenName, string itemName, string propertyName, string tagName,
                string dataType = "", string plcTag = "", string address = "", bool dryRun = true)
            =>
                Invoke<ResponseMessage>(new UnifiedHmiRequest {
                    Operation = "BindUnifiedHmiTagDynamization", HmiSoftwarePath = hmiSoftwarePath, ScreenName = screenName, ItemName = itemName, PropertyName =
                        propertyName, TagName = tagName, DataType = dataType, PlcTag = plcTag, Address = address, DryRun = dryRun
                });
    }
}
