using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class UnifiedScreenItemsTools
    {
        private readonly UnifiedScreenItemsService _service;

        public UnifiedScreenItemsTools(UnifiedScreenItemsService service) => _service = service;

        [McpServerTool(Name="DescribeUnifiedScreenItemType"), Description("[L2][HMI-Unified][READ] Typed catalog and schema of WinCC Unified screen item types from the loaded official API (no project needed): empty itemType lists every concrete UI.Shapes / UI.Widgets / UI.Controls / UI.Screens item type with its IHmi*Feature interfaces; a name (HmiCircle, Circle, Widgets.HmiSlider or full CLR name) returns its properties classified as scalar/color/multilingual/part/collection/reference with CLR type, enum values, writability, part sub-properties to depth, event types and Delete availability. Use before ManageUnifiedScreenItem so property names and enums are exact.")]
        public CallToolResult DescribeUnifiedScreenItemTypeV4(
            string itemType="",
            [Description("depth: how deep to describe nested items (0 = the item itself).")] int depth=2)
            => UnifiedHmiContract.Run("DescribeUnifiedScreenItemType", false, false, () => DescribeUnifiedScreenItemType(itemType, depth));

        public ResponseMessage DescribeUnifiedScreenItemType(string itemType="",
            int depth=2)
            => _service.DescribeUnifiedScreenItemType(itemType,depth);

        [McpServerTool(Name="ManageUnifiedScreenItem"), Description("[L2][HMI-Unified][WRITE] Any screen item type on one exact Unified screen (screenPath = unique screen name or /Group/Screen): list (name/type/geometry, paged), read (scalars, #AARRGGBB colors, parts and collections to depth, every MultilingualText language, features, event/dynamization counts), create (itemType from DescribeUnifiedScreenItemType via native Create<T>(name) or Create<T>(name, containedType) for faceplate/custom widget containers, with initial properties), update, delete (confirmDelete=true). properties accepts scalars or one level of parts ({\"Font\":{\"Size\":14},\"BackColor\":\"#FF0000FF\"}) and multilingual texts per culture ({\"Text\":{\"en-US\":\"Start\"}}); at most 50 leaves, every leaf is read back. Default preview; no save/compile/download. Events: ManageUnifiedEvent; dynamizations: ManageUnifiedDynamization. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManageUnifiedScreenItemV4(
            string softwarePath,
            string screenPath,
            [Description("action: the operation to perform - read | list | create | update | delete.")] string action="read",
            string itemName="",
            string itemType="",
            CompositeAttributeMap? properties=null,
            [Description("depth: how deep to describe nested items (0 = the item itself).")] int depth=2,
            bool confirmDelete=false,
            int offset=0,
            int limit=100,
            bool dryRun=true,
            [Description("containedType: type of the contained items (see the tool description).")] string containedType="")
            => UnifiedHmiContract.Run("ManageUnifiedScreenItem", !dryRun && action != "read" && action != "list", true,
                () => ManageUnifiedScreenItem(softwarePath,screenPath,action,itemName,itemType,
                    V4Json.Serialize(properties ?? new CompositeAttributeMap(Array.Empty<KeyValuePair<string, CompositeAttributeValue>>())),
                    depth,confirmDelete,offset,limit,dryRun,containedType), offset, limit);

        public ResponseMessage ManageUnifiedScreenItem(string softwarePath, string screenPath, string action="read", string itemName="", string itemType="",
            string propertiesJson="{}", int depth=2, bool confirmDelete=false, int offset=0, int limit=100, bool dryRun=true, string containedType="")
            => _service.ManageUnifiedScreenItem(softwarePath,screenPath,action,itemName,itemType,propertiesJson,depth,confirmDelete,offset,limit,dryRun,containedType);

        // Domain admission uses only metadata from the resolved host type; it never reads a native property.
        // Value conversions and multilingual culture handling remain in the existing service before any write.
        internal static Error? ValidateProperties(Type type, CompositeAttributeMap properties)
        {
            IReadOnlyDictionary<string, AttributeRule> Scalars(Type owner) => UnifiedUiModelLogic.PublicProperties(owner)
                .Where(p => UnifiedScreenItemLogic.Kind(p) == "scalar" || UnifiedScreenItemLogic.Kind(p) == "color")
                .ToDictionary(p => p.Name, p => new AttributeRule(InputSchema.Scalar(), p.SetMethod?.IsPublic == true), StringComparer.Ordinal);
            var parts = new Dictionary<string, IReadOnlyDictionary<string, AttributeRule>>(StringComparer.Ordinal);
            foreach (var property in UnifiedUiModelLogic.PublicProperties(type))
            {
                if (UnifiedScreenItemLogic.Kind(property) == "part") parts.Add(property.Name, Scalars(property.PropertyType));
                else if (UnifiedScreenItemLogic.Kind(property) == "multilingual")
                {
                    var cultures = new Dictionary<string, AttributeRule>(StringComparer.Ordinal);
                    if (properties.TryGetValue(property.Name, out var value) && value.Json.ValueKind == JsonValueKind.Object)
                        foreach (var culture in value.Json.EnumerateObject())
                            if (!string.IsNullOrWhiteSpace(culture.Name) && culture.Name.Length <= 16)
                                cultures.Add(culture.Name, new AttributeRule(InputSchema.String()));
                    parts.Add(property.Name, cultures);
                }
            }
            return CompositeAttributeMapValidator.ScreenItem(Scalars(type), parts).Validate(properties, "properties").Error;
        }
    }
}
