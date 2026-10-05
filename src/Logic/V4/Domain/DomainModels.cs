using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Domain
{
    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class Artifact : DomainDto
    {
        internal Artifact(JsonElement json) : base(json) { }
        public string Id => Required<string>("id");
        public IReadOnlyList<string>? Dependencies => Optional<string[]?>("dependencies", null);
        public string? Target => Optional<string?>("target", null);
        public int? Priority => Optional<int?>("priority", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class NetworkPlan : DomainDto
    {
        internal NetworkPlan(JsonElement json) : base(json) { }
        public IReadOnlyList<NetworkOperation> Operations => Items<NetworkOperation>("operations");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal abstract class NetworkOperation : DomainDto
    {
        internal NetworkOperation(JsonElement json) : base(json) { }
        public string Type => Required<string>("type");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class EnsureSubnetOperation : NetworkOperation
    {
        internal EnsureSubnetOperation(JsonElement json) : base(json) { }
        public string AnchorDeviceItemPath => Required<string>("anchorDeviceItemPath");
        public string SubnetName => Required<string>("subnetName");
        public string SubnetType => Required<string>("subnetType");
        public string? Ip => Optional<string?>("ip", null);
        public string? Mask => Optional<string?>("mask", null);
        public string? Gateway => Optional<string?>("gateway", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class AttachDeviceNodeOperation : NetworkOperation
    {
        internal AttachDeviceNodeOperation(JsonElement json) : base(json) { }
        public string DeviceItemPath => Required<string>("deviceItemPath");
        public string SubnetName => Required<string>("subnetName");
        public int InterfaceIndex => Required<int>("interfaceIndex");
        public string? AnchorDeviceItemPath => Optional<string?>("anchorDeviceItemPath", null);
        public string? Ip => Optional<string?>("ip", null);
        public string? Mask => Optional<string?>("mask", null);
        public string? Gateway => Optional<string?>("gateway", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class CpuSettingsOperation : NetworkOperation
    {
        internal CpuSettingsOperation(JsonElement json) : base(json) { }
        public string CpuPath => Required<string>("cpuPath");
        public CpuSettings Settings => Required<CpuSettings>("settings");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class CpuSettings : DomainDto
    {
        internal CpuSettings(JsonElement json) : base(json) { }
        public IReadOnlyDictionary<string, Scalar> ExactAttributes => Map<Scalar>("exactAttributes");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal abstract class BlockEdit : DomainDto
    {
        internal BlockEdit(JsonElement json) : base(json) { }
        public string Action => Required<string>("action");
        public string ExpectedValue => Required<string>("expectedValue");
        public string Value => Required<string>("value");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class BlockTextEdit : BlockEdit
    {
        internal BlockTextEdit(JsonElement json) : base(json) { }
        public string Field => Required<string>("field");
        public string Culture => Required<string>("culture");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class NetworkTextEdit : BlockEdit
    {
        internal NetworkTextEdit(JsonElement json) : base(json) { }
        public int NetworkIndex => Required<int>("networkIndex");
        public string Field => Required<string>("field");
        public string Culture => Required<string>("culture");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class MemberStartValueEdit : BlockEdit
    {
        internal MemberStartValueEdit(JsonElement json) : base(json) { }
        public string Section => Required<string>("section");
        public string MemberPath => Required<string>("memberPath");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class TemplateRow : DomainDto
    {
        internal TemplateRow(JsonElement json) : base(json) { }
        public string FileName => Required<string>("fileName");
        public IReadOnlyDictionary<string, string> Values => Map<string>("values");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class PlcAliasRow : DomainDto
    {
        internal PlcAliasRow(JsonElement json) : base(json) { }
        public IReadOnlyList<string> Source => Items<string>("source");
        public IReadOnlyList<string> Destination => Items<string>("destination");
        public bool Invert => Optional("invert", false);
        public IReadOnlyList<string>? Acknowledge => Optional<string[]?>("acknowledge", null);
        public string Title => Optional("title", string.Join(".", Destination));
        public string Comment => Optional("comment", "");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class PlcSimScenario : DomainDto
    {
        internal PlcSimScenario(JsonElement json) : base(json) { }
        public string Instance => Required<string>("instance").Trim();
        public string Mode => Optional("mode", "singleStep").Equals("default", System.StringComparison.OrdinalIgnoreCase) ? "default" : "singleStep";
        public bool StopOnFailure => Optional("stopOnFailure", true);
        public IReadOnlyList<PlcSimStep> Steps => Items<PlcSimStep>("steps");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal abstract class PlcSimStep : DomainDto
    {
        internal PlcSimStep(JsonElement json) : base(json) { }
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class PlcSimWriteStep : PlcSimStep
    {
        internal PlcSimWriteStep(JsonElement json) : base(json) { }
        public IReadOnlyDictionary<string, Scalar> Write => Map<Scalar>("write");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class PlcSimWaitStep : PlcSimStep
    {
        internal PlcSimWaitStep(JsonElement json) : base(json) { }
        public int WaitMs => Required<int>("waitMs");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class PlcSimAssertStep : PlcSimStep
    {
        internal PlcSimAssertStep(JsonElement json) : base(json) { }
        public IReadOnlyDictionary<string, Scalar> Assert => Map<Scalar>("assert");
        public double Tolerance => Optional("tolerance", 0.0);
        public string Note => Optional("note", "");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal abstract class DccPartnerSpec : DomainDto
    {
        internal DccPartnerSpec(JsonElement json) : base(json) { }
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class DccPinPartner : DccPartnerSpec
    {
        internal DccPinPartner(JsonElement json) : base(json) { }
        public string Block => Required<string>("block");
        public string Pin => Required<string>("pin");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class DccInterfacePartner : DccPartnerSpec
    {
        internal DccInterfacePartner(JsonElement json) : base(json) { }
        public string ChartInterface => Required<string>("chartInterface");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class MotionTarget : DomainDto
    {
        internal MotionTarget(JsonElement json) : base(json) { }
        public string Mode => DomainValidation.MotionMode(Json);
        public IReadOnlyList<string>? DevicePath => Optional<string[]?>("devicePath", null);
        public IReadOnlyList<string>? ItemPath => Optional<string[]?>("itemPath", null);
        public IReadOnlyList<string>? SecondItemPath => Optional<string[]?>("secondItemPath", null);
        public string? DbMemberPath => Optional<string?>("dbMemberPath", null);
        public string? PlcTagPath => Optional<string?>("plcTagPath", null);
        public string ConnectOption => Optional("connectOption", "Default");
        public bool HasConnectOption => Json.TryGetProperty("connectOption", out _);
        public int? InputBitAddress => Optional<int?>("inputBitAddress", null);
        public int? OutputBitAddress => Optional<int?>("outputBitAddress", null);
        public int? Address => Optional<int?>("address", null);
        public int? ChannelIndex => Optional<int?>("channelIndex", null);
        public string? ChannelType => Optional<string?>("channelType", null);
        public string? ChannelIoType => Optional<string?>("channelIoType", null);
        public int? ChannelNumber => Optional<int?>("channelNumber", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class TestScope : DomainDto
    {
        internal TestScope(JsonElement json) : base(json) { }
        public string Kind => Required<string>("kind");
        public string? SoftwarePath => Optional<string?>("softwarePath", null);
        public string? GroupPath => Optional<string?>("groupPath", null);
        public string? Name => Optional<string?>("name", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class TeamcenterItemSpec : DomainDto
    {
        internal TeamcenterItemSpec(JsonElement json) : base(json) { }
        public string ItemId => Optional("itemId", "");
        public string ItemName => Required<string>("itemName");
        public string RevisionId => Optional("revisionId", "");
        public string TeamcenterItemType => Required<string>("teamcenterItemType");
        public string Comment => Optional("comment", "");
        public string TeamcenterFolder => Optional("teamcenterFolder", "");
        public IReadOnlyList<string> TeamcenterProject => Optional("teamcenterProject", System.Array.Empty<string>());
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class TeamcenterRevisionSpec : DomainDto
    {
        internal TeamcenterRevisionSpec(JsonElement json) : base(json) { }
        public string RevisionId => Optional("revisionId", "");
        public string Comment => Optional("comment", "");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class SivarcReference : DomainDto
    {
        internal SivarcReference(JsonElement json) : base(json) { }
        public string Kind => Required<string>("kind");
        public string? SoftwarePath => Optional<string?>("softwarePath", null);
        public string Path => Required<string>("path");
        public string? LibraryName => Optional<string?>("libraryName", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class LibrarySelection : DomainDto
    {
        internal LibrarySelection(JsonElement json) : base(json) { }
        public bool IsFolder => Json.TryGetProperty("folder", out _);
        public string Path => Required<string>(IsFolder ? "folder" : "type").Trim().Trim('/');
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class DynamizationMapping : DomainDto
    {
        internal DynamizationMapping(JsonElement json) : base(json) { }
        public string Kind => Required<string>("kind");
        public IReadOnlyDictionary<string, Scalar> Properties => Map<Scalar>("properties");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class XPathRule : DomainDto
    {
        internal XPathRule(JsonElement json) : base(json) { }
        public string Id => Required<string>("id");
        public string XPath => Required<string>("xpath");
        public string Files => Optional("files", ".*");
        public int MinCount => Optional("minCount", 0);
        public int MaxCount => Optional("maxCount", int.MaxValue);
        public string? ValuePattern => Optional<string?>("valuePattern", null);
        public string Severity => Optional("severity", "warning");
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class LintRules : DomainDto
    {
        internal LintRules(JsonElement json) : base(json) { }
        public IReadOnlyList<string> Disabled => Optional("disabled", System.Array.Empty<string>()).Distinct(System.StringComparer.OrdinalIgnoreCase).ToArray();
        public int MaxLineLength => Optional("maxLineLength", 120);
        public int MaxNesting => Optional("maxNesting", 5);
        public IReadOnlyList<string> Markers => Optional("markers", new[] { "TODO", "FIXME", "HACK", "XXX", "NOTE", "BUG" }).Where(m => m.Length > 0).ToArray();
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class MonitoringOptions : DomainDto
    {
        internal MonitoringOptions(JsonElement json) : base(json) { }
        public int? PollMs => Optional<int?>("pollMs", null);
        public string? Source => Optional<string?>("source", null);
    }

    [JsonConverter(typeof(DomainDtoConverter))]
    internal sealed class TemplateIntent : DomainDto
    {
        internal TemplateIntent(JsonElement json) : base(json) { }
        public string? ScreenType => Optional<string?>("screenType", null);
        public string? TargetRuntime => Optional<string?>("targetRuntime", null);
        public IReadOnlyList<string>? PreferredComponents => Optional<string[]?>("preferredComponents", null);
    }

}
