using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Hmi;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class UnifiedHmiTools
    {
        private const string DefaultTagTableName = "默认变量表";
        private readonly UnifiedHmiService _service;

        public UnifiedHmiTools(UnifiedHmiService service) => _service = service;

        [McpServerTool(Name = "SetUnifiedHmiRuntimeState"), Description("[L2][HMI-Unified] SHORTCUT for motor start/stop HMI. Ensures HMI_Connection_1 uses the correct PLC driver (1200/1500 vs 300/400 from CPU TypeIdentifier), 4 HMI tags (StartPB/StopPB/EStop/RunOut) with symbolic PLC binding, and a simple styled Main screen. Requires: ConnectPortal + OpenProject + PLC + Unified HMI. Call after EnsureUnifiedHmiScreen if you need a fixed screen size. Idempotent. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult EnsureStartStopUnifiedHmiV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name (default 'Main')")] string screenName = "Main",
            [Description("tagTableName: target HMI tag table name (default '默认变量表')")] string tagTableName = DefaultTagTableName,
            [Description("plcName: PLC software path / device name for connection + tag mapping (default 'PLC_1')")] string plcName = "PLC_1",
            [Description("connectionName: Unified HMI connection object name (default 'HMI_Connection_1')")] string connectionName = "HMI_Connection_1")
            => UnifiedHmiContract.Run("SetUnifiedHmiRuntimeState", true, true, () => EnsureStartStopUnifiedHmi(hmiSoftwarePath, screenName, tagTableName, plcName, connectionName));

        public ResponseMessage EnsureStartStopUnifiedHmi(string hmiSoftwarePath,
            string screenName = "Main",
            string tagTableName = DefaultTagTableName,
            string plcName = "PLC_1",
            string connectionName = "HMI_Connection_1")
        {
            try
            {
                var res = _service.EnsureStartStopUnifiedHmi(hmiSoftwarePath, screenName, tagTableName, plcName, connectionName);
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring unified HMI start/stop: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiScreen"), Description("[L2][HMI-Unified] Create or verify a WinCC Unified HMI screen exists. Requires: ConnectPortal + OpenProject + Unified HMI. Idempotent. After creating a screen, add tags with EnsureUnifiedHmiTag, add controls with EnsureUnifiedHmiScreenItem, or apply a complete layout with ApplyUnifiedHmiScreenDesign. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult EnsureUnifiedHmiScreenV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("width: optional screen width, 0 means keep current")] uint width = 0,
            [Description("height: optional screen height, 0 means keep current")] uint height = 0)
            => UnifiedHmiContract.Run("EnsureUnifiedHmiScreen", true, true, () => EnsureUnifiedHmiScreen(hmiSoftwarePath, screenName, width, height));

        public ResponseMessage EnsureUnifiedHmiScreen(string hmiSoftwarePath,
            string screenName,
            uint width = 0,
            uint height = 0)
        {
            try
            {
                return _service.EnsureUnifiedHmiScreen(hmiSoftwarePath, screenName, width, height);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI screen '{screenName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiTagTable"), Description("[L2][HMI-Unified] Create or verify a Unified HMI tag table exists. Requires: ConnectPortal + OpenProject + Unified HMI. Idempotent. Create tag tables before adding tags with EnsureUnifiedHmiTag. Use the exact tag table name returned by the project. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult EnsureUnifiedHmiTagTableV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("tagTableName: target HMI tag table name")] string tagTableName)
            => UnifiedHmiContract.Run("EnsureUnifiedHmiTagTable", true, true, () => EnsureUnifiedHmiTagTable(hmiSoftwarePath, tagTableName));

        public ResponseMessage EnsureUnifiedHmiTagTable(string hmiSoftwarePath,
            string tagTableName)
        {
            try
            {
                return _service.EnsureUnifiedHmiTagTable(hmiSoftwarePath, tagTableName);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI tag table '{tagTableName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiTag"), Description("[L2][HMI-Unified] Create or verify a Unified HMI external tag. For PLC-backed tags pass plcTag and address in the same call; the address must read back in Address/LogicalAddress, e.g. %DB200.DBX0.0. Requires: ConnectPortal + OpenProject + EnsureUnifiedHmiConnection + EnsureUnifiedHmiTagTable. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult EnsureUnifiedHmiTagV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("tagTableName: target HMI tag table name")] string tagTableName,
            [Description("tagName: HMI tag name")] string tagName,
            [Description("hmiDataType: HMI data type, e.g. Bool, Int, Real, String")] string hmiDataType = "Bool",
            [Description("plcName: PLC name for symbolic binding")] string plcName = "PLC_1",
            [Description("plcTag: PLC tag name/path; empty means same as tagName")] string plcTag = "",
            [Description("connectionName: HMI connection name; empty keeps current/auto")] string connectionName = "",
            [Description("address: optional absolute PLC address, e.g. %DB200.DBX0.0. When supplied it is written as the verified HMI runtime address while plcTag remains available as the symbolic reference.")] string address = "",
            [Description("requireVerifiedBinding: stable public generation should keep this true. Set false only for intentional internal HMI-only tags used by local validation/probes.")] bool requireVerifiedBinding = true)
            => UnifiedHmiContract.Run("EnsureUnifiedHmiTag", true, true, () => EnsureUnifiedHmiTag(hmiSoftwarePath, tagTableName, tagName, hmiDataType, plcName, plcTag, connectionName, address, requireVerifiedBinding));

        public ResponseMessage EnsureUnifiedHmiTag(string hmiSoftwarePath,
            string tagTableName,
            string tagName,
            string hmiDataType = "Bool",
            string plcName = "PLC_1",
            string plcTag = "",
            string connectionName = "",
            string address = "",
            bool requireVerifiedBinding = true)
        {
            try
            {
                return _service.EnsureUnifiedHmiTag(hmiSoftwarePath, tagTableName, tagName, hmiDataType, plcName, plcTag, connectionName, address, requireVerifiedBinding);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI tag '{tagName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiConnection"), Description("[L2][HMI-Unified] Create or verify the PLC↔HMI communication connection (HMI_Connection_1 by default). Requires: ConnectPortal + OpenProject + both PLC and Unified HMI devices. Must exist before PLC-backed HMI tags can exchange data. Call before EnsureUnifiedHmiTag with plcTag binding. UNIFIED PANELS ONLY: on Classic/Comfort/Basic panels (KTP Basic, TP/KTP Comfort) this connection cannot be created via Openness (CommunicationConnections service is not exposed); if the project needs end-to-end HMI automation, use a WinCC Unified panel instead of a classic one. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult EnsureUnifiedHmiConnectionV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("connectionName: HMI connection name")] string connectionName = "HMI_Connection_1",
            [Description("plcName: PLC software/device symbolic name")] string plcName = "PLC_1")
            => UnifiedHmiContract.Run("EnsureUnifiedHmiConnection", true, true, () => EnsureUnifiedHmiConnection(hmiSoftwarePath, connectionName, plcName));

        public ResponseObjectDescribe EnsureUnifiedHmiConnection(string hmiSoftwarePath,
            string connectionName = "HMI_Connection_1",
            string plcName = "PLC_1")
        {
            try
            {
                var res = _service.EnsureUnifiedHmiConnection(hmiSoftwarePath, connectionName, plcName);
                res.Meta = ResponseMeta.Basic(DateTime.Now, true);
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI connection '{connectionName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiScreenItem"), Description("[L2][HMI-Unified] Create or verify a single Unified HMI control (button, lamp, IO field, etc.) on a screen. Requires: ConnectPortal + OpenProject + EnsureUnifiedHmiScreen. itemType: Button, Rectangle (lamp/indicator/background — has NO text), Text (static text label = HmiText), IOField (value display/entry), or full CLR type name. For a text caption/label ALWAYS use Text, never Rectangle (a Rectangle has no Text property and renders blank if given text). For a complete screen layout use ApplyUnifiedHmiScreenDesign instead. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult EnsureUnifiedHmiScreenItemV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("itemName: screen item name")] string itemName,
            [Description("itemType: Button, Rectangle/Lamp, IOField, or full CLR type name")] string itemType = "Button",
            [Description("left: X position")] int left = 0,
            [Description("top: Y position")] int top = 0,
            [Description("width: item width")] uint width = 120,
            [Description("height: item height")] uint height = 40,
            [Description("text: optional button/display text")] string text = "")
            => UnifiedHmiContract.Run("EnsureUnifiedHmiScreenItem", true, true, () => EnsureUnifiedHmiScreenItem(hmiSoftwarePath, screenName, itemName, itemType, left, top, width, height, text));

        public ResponseMessage EnsureUnifiedHmiScreenItem(string hmiSoftwarePath,
            string screenName,
            string itemName,
            string itemType = "Button",
            int left = 0,
            int top = 0,
            uint width = 120,
            uint height = 40,
            string text = "")
        {
            try
            {
                return _service.EnsureUnifiedHmiScreenItem(hmiSoftwarePath, screenName, itemName, itemType, left, top, width, height, text);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI screen item '{itemName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetUnifiedHmiTexts"), Description("[L0][HMI-Unified] Read language codes and raw text without serializing native language objects. Requires ConnectPortal + OpenProject. Supports Text, AlternateText and ToolTipText when present on the control. Check ok and meta.completeness.")]
        public CallToolResult ReadUnifiedHmiTextsV4(
            string hmiSoftwarePath,
            [Description("screenName: exact screen name.")] string screenName,
            string itemName,
            [Description("textProperty: name of the multilingual text property to read ('' = all).")] string textProperty = "Text")
            => UnifiedHmiContract.Run("GetUnifiedHmiTexts", false, false, () => ReadUnifiedHmiTexts(hmiSoftwarePath, screenName, itemName, textProperty));

        public ResponseMessage ReadUnifiedHmiTexts(string hmiSoftwarePath,
            string screenName,
            string itemName,
            string textProperty = "Text")
            => _service.ReadUnifiedHmiTexts(hmiSoftwarePath, screenName, itemName, textProperty);

        [McpServerTool(Name = "ApplyUnifiedHmiScreenDesign"), Description("[L2][HMI-Unified] Apply layout JSON. For language text use items[].text, culture (default zh-CN), textProperty (default Text; also AlternateText/ToolTipText). Omit text to preserve, empty string clears, null is invalid. Exact language match required; data.evidence.textReadback contains verified raw text by language. ok=false on any failed write, including strict=false partial results. Changes are not transactional. Requires ConnectPortal + OpenProject. Use type Text for labels; Rectangle has no text. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ApplyUnifiedHmiScreenDesignJsonV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("design: JSON object with optional screen properties and items array")] UnifiedScreenSpec design,
            [Description("strict: true includes failure details in the error; false returns a partial-result summary. Both report ok=false on failed writes; neither rolls back changes.")] bool strict = true)
            => UnifiedHmiContract.Run("ApplyUnifiedHmiScreenDesign", true, true, () => ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, design.ToBuilderInput().ToJsonString(), strict));

        public ResponseMessage ApplyUnifiedHmiScreenDesignJson(string hmiSoftwarePath,
            string screenName,
            string designJson,
            bool strict = true)
        {
            try
            {
                return _service.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, designJson, strict);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI design to '{screenName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }









        [McpServerTool(Name = "ApplyUnifiedHmiTheme"), Description("[L2][HMI-Unified] Apply a theme/palette to a real Unified HMI screen through ApplyUnifiedHmiScreenDesign. Requires a connected TIA project; verify with DescribeHmiScreenItem/readback before saving. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ApplyUnifiedHmiThemeV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("theme: JSON accepted by BuildUnifiedHmiThemeDesign.")] UnifiedThemeSpec theme)
            => UnifiedHmiContract.Run("ApplyUnifiedHmiTheme", true, true, () => ApplyUnifiedHmiTheme(hmiSoftwarePath, screenName, theme.ToBuilderInput().ToJsonString()));

        public ResponseMessage ApplyUnifiedHmiTheme(string hmiSoftwarePath,
            string screenName,
            string themeJson)
        {
            try
            {
                var design = HmiDesignBuilder.BuildUnifiedHmiThemeDesignJson(themeJson).Data?.ToJsonString() ?? "{}";
                return _service.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, design);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI theme: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ApplyUnifiedHmiLayout"), Description("[L2][HMI-Unified] Apply a grid layout to a real Unified HMI screen through ApplyUnifiedHmiScreenDesign. Requires a connected TIA project; verify changed items with DescribeHmiScreenItem/readback before saving. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ApplyUnifiedHmiLayoutV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("layout: JSON accepted by BuildUnifiedHmiLayoutDesign.")] UnifiedLayoutSpec layout)
            => UnifiedHmiContract.Run("ApplyUnifiedHmiLayout", true, true, () => ApplyUnifiedHmiLayout(hmiSoftwarePath, screenName, layout.ToBuilderInput().ToJsonString()));

        public ResponseMessage ApplyUnifiedHmiLayout(string hmiSoftwarePath,
            string screenName,
            string layoutJson)
        {
            try
            {
                var design = HmiDesignBuilder.BuildUnifiedHmiLayoutDesignJson(layoutJson).Data?.ToJsonString() ?? "{}";
                return _service.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, design);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI layout: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BindUnifiedHmiButtonPressedTag"), Description("[L2][HMI-Unified]Bind a Unified HMI button PressedStateTags entry to an HMI tag (momentary press behavior, best-effort). Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult BindUnifiedHmiButtonPressedTagV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("tagName: HMI tag name to write while pressed")] string tagName)
            => UnifiedHmiContract.Run("BindUnifiedHmiButtonPressedTag", true, true, () => BindUnifiedHmiButtonPressedTag(hmiSoftwarePath, screenName, buttonName, tagName));

        public ResponseMessage BindUnifiedHmiButtonPressedTag(string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string tagName)
        {
            try
            {
                return _service.BindUnifiedHmiButtonPressedTag(hmiSoftwarePath, screenName, buttonName, tagName);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error binding button '{buttonName}' to tag '{tagName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListUnifiedHmiApiTypes"), Description("[L2][HMI-Unified]List loaded WinCC Unified HMI API types/enums by name filter, useful for discovering event and dynamization types.")]
        public CallToolResult ListUnifiedHmiApiTypesV4(
            [Description("nameContains: case-insensitive substring filter, e.g. Dynamization or EventType")] string nameContains = "",
            [Description("limit: max returned type lines")] int limit = 500)
            => UnifiedHmiContract.Run("ListUnifiedHmiApiTypes", false, false, () => ListUnifiedHmiApiTypes(nameContains, limit));

        public ResponseStringList ListUnifiedHmiApiTypes(string nameContains = "",
            int limit = 500)
        {
            try
            {
                var items = _service.ListUnifiedHmiApiTypes(nameContains, limit);
                return new ResponseStringList
                {
                    Message = $"Unified HMI API types listed (filter='{nameContains}')",
                    Items = items,
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing Unified HMI API types: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiButtonEventHandler"), Description("[L2][HMI-Unified]Ensure a Unified HMI button event handler exists and return its API shape. eventType must match HmiButtonEventType. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult EnsureUnifiedHmiButtonEventHandlerV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: HmiButtonEventType value, e.g. Tapped, Down, Up; use ListUnifiedHmiApiTypes('HmiButtonEventType') to inspect")] string eventType)
            => UnifiedHmiContract.Run("EnsureUnifiedHmiButtonEventHandler", true, true, () => EnsureUnifiedHmiButtonEventHandler(hmiSoftwarePath, screenName, buttonName, eventType));

        public ResponseMessage EnsureUnifiedHmiButtonEventHandler(string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType)
        {
            try
            {
                return _service.EnsureUnifiedHmiButtonEventHandler(hmiSoftwarePath, screenName, buttonName, eventType);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring button event handler '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeUnifiedHmiButtonEventScript"), Description("[L2][HMI-Unified]Describe a Unified HMI button event handler Script property and its current object members/attributes.")]
        public CallToolResult DescribeUnifiedHmiButtonEventScriptV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: enum value, e.g. Tapped, Down, Up")] string eventType,
            [Description("maxMembers: max member count")] int maxMembers = 200)
            => UnifiedHmiContract.Run("DescribeUnifiedHmiButtonEventScript", false, false, () => DescribeUnifiedHmiButtonEventScript(hmiSoftwarePath, screenName, buttonName, eventType, maxMembers));

        public ResponseObjectDescribe DescribeUnifiedHmiButtonEventScript(string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType,
            int maxMembers = 200)
        {
            try
            {
                var res = _service.DescribeUnifiedHmiButtonEventScript(hmiSoftwarePath, screenName, buttonName, eventType, maxMembers);
                res.Meta ??= ResponseMeta.Unstamped(false, ("operationSuccess", false));
                res.Meta["timestamp"] = DateTime.Now;
                res.Meta["memberCount"] = res.Members?.Count() ?? 0;
                return res;
            }
            catch (PortalException pex)
            {
                // 路径解析不到是调用方的参数问题，不是服务器内部意外错误。
                throw new McpException(pex.Message, pex,
                    pex.Code == PortalErrorCode.NotFound ? McpErrorCode.InvalidParams : McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing button event script '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetUnifiedHmiButtonEventScriptCode"), Description("[L2][HMI-Unified]Set ScriptCode on a Unified HMI button event ScriptDynamization. SyntaxCheck is OFF by default because on TIA V21 it can crash the Portal process and lose the script (issue #36); pass syntaxCheck=true only when you need that evidence. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult SetUnifiedHmiButtonEventScriptCodeV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: enum value, e.g. Tapped, Down, Up")] string eventType,
            [Description("scriptCode: JavaScript code for the event")] string scriptCode,
            [Description("globalDefinitionAreaScriptCode: optional global definitions for the script")] string globalDefinitionAreaScriptCode = "",
            [Description("async: whether the script is async")] bool async = false,
            [Description("syntaxCheck: run TIA SyntaxCheck after writing. Default false - on TIA V21 this call can crash the Portal process (NonRecoverableException) and lose the just-written script. When false, no syntaxErrorCount is reported: absence means NOT CHECKED, never zero errors.")] bool syntaxCheck = false)
            => UnifiedHmiContract.Run("SetUnifiedHmiButtonEventScriptCode", true, true, () => SetUnifiedHmiButtonEventScriptCode(hmiSoftwarePath, screenName, buttonName, eventType, scriptCode, globalDefinitionAreaScriptCode, async, syntaxCheck));

        public ResponseMessage SetUnifiedHmiButtonEventScriptCode(string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType,
            string scriptCode,
            string globalDefinitionAreaScriptCode = "",
            bool async = false,
            bool syntaxCheck = false)
        {
            try
            {
                return _service.SetUnifiedHmiButtonEventScriptCode(hmiSoftwarePath, screenName, buttonName, eventType, scriptCode, globalDefinitionAreaScriptCode, async, syntaxCheck);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting button event script '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }









        [McpServerTool(Name = "EnsureUnifiedHmiButtonAction"), Description("[L2][HMI-Unified]Generate and apply a deterministic Unified HMI button action. Only set-bit/reset-bit/toggle-bit are applied; high-risk or TODO recipes are rejected. SyntaxCheck is OFF by default (issue #36: it can crash TIA V21). Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult EnsureUnifiedHmiButtonActionV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: HmiButtonEventType value, e.g. Down (press), Up (release), Tapped — NOT Pressed/Released")] string eventType,
            [Description("actionKind: set-bit, reset-bit, or toggle-bit")] string actionKind,
            [Description("targetTag: verified target HMI tag")] string targetTag,
            [Description("syntaxCheck: run TIA SyntaxCheck after writing the script. Default false - see SetUnifiedHmiButtonEventScriptCode. The applied recipes are generated by this server and already linted offline, so the check adds little and risks a V21 crash.")] bool syntaxCheck = false)
            => UnifiedHmiContract.Run("EnsureUnifiedHmiButtonAction", true, true, () => EnsureUnifiedHmiButtonAction(hmiSoftwarePath, screenName, buttonName, eventType, actionKind, targetTag, syntaxCheck));

        public ResponseMessage EnsureUnifiedHmiButtonAction(string hmiSoftwarePath,
            string screenName,
            string buttonName,
            string eventType,
            string actionKind,
            string targetTag,
            bool syntaxCheck = false)
        {
            try
            {
                var recipe = HmiActionScriptRecipeBuilder.Build(actionKind, eventType, new[] { targetTag });
                var kind = recipe["recipeKind"]?.ToString() ?? "";
                var script = recipe["script"]?.ToString() ?? "";
                var allowed = new[] { "set-bit", "reset-bit", "toggle-bit" };
                if (!allowed.Contains(kind, StringComparer.OrdinalIgnoreCase))
                {
                    recipe["applyStatus"] = "rejected";
                    recipe["applyReason"] = "Only set-bit/reset-bit/toggle-bit recipes can be applied by this safe high-level tool.";
                    return new ResponseMessage { Message = "Unified HMI button action rejected by safety policy.", Meta = recipe };
                }
                if (recipe["ok"]?.GetValue<bool>() != true || string.IsNullOrWhiteSpace(script) || script.IndexOf("TODO", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    recipe["applyStatus"] = "rejected";
                    recipe["applyReason"] = "Recipe has errors, empty script, or TODO placeholder.";
                    return new ResponseMessage { Message = "Unified HMI button action rejected because the generated script is not directly applicable.", Meta = recipe };
                }

                var ensure = _service.EnsureUnifiedHmiButtonEventHandler(hmiSoftwarePath, screenName, buttonName, eventType);
                var set = _service.SetUnifiedHmiButtonEventScriptCode(hmiSoftwarePath, screenName, buttonName, eventType, script, "", false, syntaxCheck);
                recipe["applyStatus"] = set.Meta?["success"]?.GetValue<bool>() == true ? "applied" : "apply-failed";
                recipe["ensureMessage"] = ensure.Message ?? "";
                recipe["ensureMeta"] = ensure.Meta?.DeepClone();
                recipe["setMessage"] = set.Message ?? "";
                recipe["setMeta"] = set.Meta?.DeepClone();
                return new ResponseMessage
                {
                    Message = "Unified HMI button action applied via generated recipe.",
                    Meta = recipe
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI button action '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiDynamization"), Description("[L2][HMI-Unified]Ensure a Unified HMI item property dynamization exists using a concrete dynamization type and return its API shape. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult EnsureUnifiedHmiDynamizationV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("itemName: HMI screen item name")] string itemName,
            [Description("propertyName: target property name, e.g. BackColor or Visible")] string propertyName,
            [Description("dynamizationType: type short name/full name; empty tries common candidates and returns errors if unsupported")] string dynamizationType = "")
            => UnifiedHmiContract.Run("EnsureUnifiedHmiDynamization", true, true, () => EnsureUnifiedHmiDynamization(hmiSoftwarePath, screenName, itemName, propertyName, dynamizationType));

        public ResponseMessage EnsureUnifiedHmiDynamization(string hmiSoftwarePath,
            string screenName,
            string itemName,
            string propertyName,
            string dynamizationType = "")
        {
            try
            {
                return _service.EnsureUnifiedHmiDynamization(hmiSoftwarePath, screenName, itemName, propertyName, dynamizationType);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring dynamization '{itemName}.{propertyName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BindUnifiedHmiTagDynamization"), Description("[L2][HMI-Unified]Ensure a Unified HMI TagDynamization exists for an item property and bind it to an HMI tag. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult BindUnifiedHmiTagDynamizationV4(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("itemName: HMI screen item name")] string itemName,
            [Description("propertyName: target property name, e.g. BackColor or Visible")] string propertyName,
            [Description("tagName: HMI tag name used as the dynamic source")] string tagName,
            [Description("dataType: tag data type, e.g. Bool, Int, Real")] string dataType = "Bool",
            [Description("plcTag: optional PLC tag/path")] string plcTag = "",
            [Description("address: optional absolute address")] string address = "")
            => UnifiedHmiContract.Run("BindUnifiedHmiTagDynamization", true, true, () => BindUnifiedHmiTagDynamization(hmiSoftwarePath, screenName, itemName, propertyName, tagName, dataType, plcTag, address));

        public ResponseMessage BindUnifiedHmiTagDynamization(string hmiSoftwarePath,
            string screenName,
            string itemName,
            string propertyName,
            string tagName,
            string dataType = "Bool",
            string plcTag = "",
            string address = "")
        {
            try
            {
                return _service.BindUnifiedHmiTagDynamization(hmiSoftwarePath, screenName, itemName, propertyName, tagName, dataType, plcTag, address);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error binding tag dynamization '{itemName}.{propertyName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
