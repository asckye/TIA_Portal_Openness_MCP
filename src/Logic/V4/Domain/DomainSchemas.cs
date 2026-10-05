using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using static TiaMcp.Logic.V4.Domain.DomainShape;

using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    internal static partial class DomainSchemas
    {
        internal static readonly InputSchema Scalar = InputSchema.Scalar();
        private static readonly Dictionary<Type, InputSchema> Shapes = Build();
        internal static bool Has(Type type) => Shapes.ContainsKey(type);

        public static JsonObject Get<T>() => JsonNode.Parse(V4Json.Serialize(For(typeof(T)).Json))!.AsObject();
        internal static InputSchema For(Type type)
        {
            if (Shapes.TryGetValue(type, out var shape)) return shape;
            throw new ArgumentException("No domain schema for " + type.Name);
        }
        internal static Type Concrete(Type type, JsonElement value)
        {
            if (type == typeof(BranchStep)) return value.TryGetProperty("property", out _) ? typeof(PropertyBranchStep)
                : value.TryGetProperty("attribute", out _) ? typeof(AttributeBranchStep)
                : value.TryGetProperty("index", out _) ? typeof(IndexBranchStep) : typeof(NamedBranchStep);
            if (type == typeof(NetworkOperation)) return value.GetProperty("type").GetString() switch
            { "EnsureSubnet" => typeof(EnsureSubnetOperation), "AttachDeviceNodeToSubnet" => typeof(AttachDeviceNodeOperation), _ => typeof(CpuSettingsOperation) };
            if (type == typeof(BlockEdit)) return value.GetProperty("action").GetString() switch
            { "setBlockText" => typeof(BlockTextEdit), "setNetworkText" => typeof(NetworkTextEdit), _ => typeof(MemberStartValueEdit) };
            if (type == typeof(DccPartnerSpec)) return value.TryGetProperty("chartInterface", out _) ? typeof(DccInterfacePartner) : typeof(DccPinPartner);
            if (type == typeof(PlcSimStep)) return value.TryGetProperty("write", out _) ? typeof(PlcSimWriteStep)
                : value.TryGetProperty("waitMs", out _) ? typeof(PlcSimWaitStep) : typeof(PlcSimAssertStep);
            if (type == typeof(GraphicSelectionRecord)) return value.GetProperty("kind").GetString() == "graphicObject"
                ? typeof(GraphicObjectRecord) : typeof(GraphicSummaryRecord);
            return type;
        }
        private static Dictionary<Type, InputSchema> Build()
        {
            var d = new Dictionary<Type, InputSchema>();
            var text = String(); var name = String(1); var count = Integer(0); var strings = Array(text);
            d[typeof(PropertyBranchStep)] = Object(("property", text));
            d[typeof(AttributeBranchStep)] = Object(("attribute", text));
            d[typeof(IndexBranchStep)] = Object(("index", count));
            d[typeof(NamedBranchStep)] = Object(("name", text), ("key?", text));
            d[typeof(BranchStep)] = Union(d[typeof(PropertyBranchStep)], d[typeof(AttributeBranchStep)],
                d[typeof(IndexBranchStep)], d[typeof(NamedBranchStep)]);
            d[typeof(BranchStep[])] = Array(d[typeof(BranchStep)], 0, 64);
            d[typeof(SubjectAlternativeName)] = Object(("type", String(0, null, "Dns", "Email", "IP", "Uri")), ("value", String(1, 255)));
            d[typeof(SubjectAlternativeName[])] = Array(d[typeof(SubjectAlternativeName)], 0, 64);
            d[typeof(Artifact)] = Object(("id", String(1, 512)), ("dependencies?", Array(name, 0, 256)), ("target?", text), ("priority?", Integer()));
            d[typeof(Artifact[])] = Array(d[typeof(Artifact)], 1, 256);
            d[typeof(CpuSettings)] = Object(("exactAttributes", Map(Scalar, 1)));
            d[typeof(EnsureSubnetOperation)] = Object(("type", String(0, null, "EnsureSubnet")),
                ("anchorDeviceItemPath", name), ("subnetName", name), ("subnetType", name), ("ip?", text), ("mask?", text), ("gateway?", text));
            d[typeof(AttachDeviceNodeOperation)] = Object(("type", String(0, null, "AttachDeviceNodeToSubnet")),
                ("deviceItemPath", name), ("subnetName", name), ("interfaceIndex", count), ("anchorDeviceItemPath?", name), ("ip?", text), ("mask?", text), ("gateway?", text));
            d[typeof(CpuSettingsOperation)] = Object(("type", String(0, null, "SetPlcCpuSettings")), ("cpuPath", name), ("settings", d[typeof(CpuSettings)]));
            d[typeof(NetworkOperation)] = Union(d[typeof(EnsureSubnetOperation)], d[typeof(AttachDeviceNodeOperation)], d[typeof(CpuSettingsOperation)]);
            d[typeof(NetworkPlan)] = Object(("operations", Array(d[typeof(NetworkOperation)])));
            var field = String(0, null, "Title", "Comment");
            d[typeof(BlockTextEdit)] = Object(("action", String(0, null, "setBlockText")), ("field", field), ("culture", text), ("expectedValue", text), ("value", text));
            d[typeof(NetworkTextEdit)] = Object(("action", String(0, null, "setNetworkText")), ("networkIndex", count), ("field", field), ("culture", text), ("expectedValue", text), ("value", text));
            d[typeof(MemberStartValueEdit)] = Object(("action", String(0, null, "setMemberStartValue")), ("section", text), ("memberPath", text), ("expectedValue", text), ("value", text));
            d[typeof(BlockEdit)] = Union(d[typeof(BlockTextEdit)], d[typeof(NetworkTextEdit)], d[typeof(MemberStartValueEdit)]);
            d[typeof(BlockEdit[])] = Array(d[typeof(BlockEdit)], 1, 100);
            d[typeof(TemplateRow)] = Object(("fileName", name), ("values", Map(text)));
            d[typeof(TemplateRow[])] = Array(d[typeof(TemplateRow)], 1, 100);
            var symbols = Array(String(1, 128), 1, 32);
            d[typeof(PlcAliasRow)] = Object(("source", symbols), ("destination", symbols), ("invert?", Boolean()), ("acknowledge?", symbols), ("title?", text), ("comment?", text));
            d[typeof(PlcAliasRow[])] = Array(d[typeof(PlcAliasRow)], 1, 500);
            var values = Map(Scalar, 1, 500);
            d[typeof(PlcSimWriteStep)] = Object(("write", values));
            d[typeof(PlcSimWaitStep)] = Object(("waitMs", Integer(1, 60000)));
            d[typeof(PlcSimAssertStep)] = Object(("assert", values), ("tolerance?", Number()), ("note?", text));
            d[typeof(PlcSimStep)] = Union(d[typeof(PlcSimWriteStep)], d[typeof(PlcSimWaitStep)], d[typeof(PlcSimAssertStep)]);
            d[typeof(PlcSimScenario)] = Object(("instance", name), ("mode?", Pattern("^([sS][iI][nN][gG][lL][eE][sS][tT][eE][pP]|[dD][eE][fF][aA][uU][lL][tT])$")),
                ("stopOnFailure?", Boolean()), ("steps", Array(d[typeof(PlcSimStep)], 1, 500)));
            d[typeof(DccPinPartner)] = Object(("block", String(1, 128)), ("pin", String(1, 128)));
            d[typeof(DccInterfacePartner)] = Object(("chartInterface", String(1, 128)));
            d[typeof(DccPartnerSpec)] = Union(d[typeof(DccPinPartner)], d[typeof(DccInterfacePartner)]);
            var paths = Array(name, 1, 64);
            d[typeof(MotionTarget)] = Union(
                Object(("devicePath", paths), ("itemPath", paths)),
                Object(("devicePath", paths), ("itemPath", paths), ("secondItemPath", paths), ("connectOption?", name)),
                Object(("devicePath", paths), ("itemPath", paths), ("channelIndex", count)),
                Object(("devicePath", paths), ("itemPath", paths), ("channelType", name), ("channelIoType", name), ("channelNumber", count)),
                Object(("dbMemberPath", name)), Object(("plcTagPath", name)), Object(("address", count)),
                Object(("inputBitAddress", count), ("outputBitAddress", count), ("connectOption?", name)));
            d[typeof(TestScope)] = Union(Object(("kind", String(0, null, "project"))),
                Object(("kind", String(0, null, "deviceGroup")), ("name", String(1, 256))),
                Object(("kind", String(0, null, "plc", "units")), ("softwarePath", String(1, 1024))),
                Object(("kind", String(0, null, "blocks", "tags", "types")), ("softwarePath", String(1, 1024)), ("groupPath?", text)));
            d[typeof(TestScope[])] = Array(d[typeof(TestScope)]);
            d[typeof(TeamcenterItemSpec)] = Object(("itemId?", text), ("itemName", String(1, 256)), ("revisionId?", text),
                ("teamcenterItemType", String(1, 256)), ("comment?", text), ("teamcenterFolder?", text), ("teamcenterProject?", strings));
            d[typeof(TeamcenterRevisionSpec)] = Object(("revisionId?", text), ("comment?", text));
            d[typeof(SivarcReference)] = Union(
                Object(("kind", String(0, null, "plcBlock")), ("softwarePath", String(1, 1024)), ("path", String(1, 1024))),
                Object(("kind", String(0, null, "masterCopy", "libraryType", "masterCopyFolder", "typeFolder")), ("path", String(1, 1024)), ("libraryName?", text)));
            d[typeof(Dictionary<string, SivarcReference?>)] = Map(Union(d[typeof(SivarcReference)], Null()));
            d[typeof(Dictionary<string, bool>)] = Map(Boolean());
            d[typeof(LibrarySelection)] = Union(Object(("folder", text)), Object(("type", name)));
            d[typeof(LibrarySelection[])] = Array(d[typeof(LibrarySelection)], 1, 200);
            d[typeof(DynamizationMapping)] = Object(("kind", String(0, null, "Simple", "Range", "Bitmask")), ("properties", Map(Scalar, 0, 50)));
            d[typeof(DynamizationMapping[])] = Array(d[typeof(DynamizationMapping)], 0, 100);
            d[typeof(XPathRule)] = Object(("id", name), ("xpath", String(1, 1024)), ("files?", text), ("minCount?", count), ("maxCount?", count),
                ("valuePattern?", text), ("severity?", String(0, null, "info", "warning", "error")));
            d[typeof(XPathRule[])] = Array(d[typeof(XPathRule)], 0, 50);
            d[typeof(LintRules)] = Object(("disabled?", strings), ("maxLineLength?", Integer(40, 1000)), ("maxNesting?", Integer(1, 50)), ("markers?", strings));
            d[typeof(MonitoringOptions)] = Object(("pollMs?", Integer()), ("source?", text));
            d[typeof(TemplateIntent)] = Object(("screenType?", text), ("targetRuntime?", text), ("preferredComponents?", strings));
            AddGraphicShapes(d);
            AddOpenPipeShapes(d);
            return d;
        }
    }
}
