using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.Logic.V4.Hmi
{
    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class DeviceAmlSpec : HmiObject
    {
        public string ProjectName { get; }
        public IReadOnlyList<AmlDeviceSpec> Devices { get; }
        public IReadOnlyList<AmlSubnetSpec>? Subnets { get; }

        public DeviceAmlSpec(string projectName, IReadOnlyList<AmlDeviceSpec> devices,
            IReadOnlyList<AmlSubnetSpec>? subnets = null)
        {
            ProjectName = projectName;
            Devices = V4Validation.List(devices);
            Subnets = HmiContracts.OptionalList(subnets);
            Validate();
        }

        internal override void Validate()
        {
            V4Validation.Require(ProjectName != null, "projectName is required.");
            V4Validation.Require(Devices.Count > 0, "At least one device is required.");
            HmiRules.AmlBudget(Devices.SelectMany(device => device.DeviceItems));
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class AmlDeviceSpec : HmiObject
    {
        public string Name { get; }
        public string TypeIdentifier { get; }
        public IReadOnlyList<DeviceItemSpec> DeviceItems { get; }

        public AmlDeviceSpec(string name, string typeIdentifier, IReadOnlyList<DeviceItemSpec> deviceItems)
        {
            Name = name;
            TypeIdentifier = typeIdentifier;
            DeviceItems = V4Validation.List(deviceItems);
            Validate();
        }

        internal override void Validate()
        {
            V4Validation.Text(Name, "name");
            V4Validation.Text(TypeIdentifier, "typeIdentifier");
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class DeviceItemSpec : HmiObject
    {
        public string Name { get; }
        public string? TypeIdentifier { get; }
        public string? Role { get; }
        public int? PositionNumber { get; }
        public bool? BuiltIn { get; }
        public string? FirmwareVersion { get; }
        public string? Comment { get; }
        public string? Label { get; }
        public AttributeMap<string>? Attributes { get; }
        public IReadOnlyList<AmlNodeSpec>? Nodes { get; }
        public IReadOnlyList<DeviceItemSpec>? DeviceItems { get; }

        public DeviceItemSpec(string name, string? typeIdentifier = null, string? role = null,
            int? positionNumber = null, bool? builtIn = null, string? firmwareVersion = null, string? comment = null,
            string? label = null, IReadOnlyDictionary<string, string>? attributes = null,
            IReadOnlyList<AmlNodeSpec>? nodes = null, IReadOnlyList<DeviceItemSpec>? deviceItems = null)
        {
            Name = name;
            TypeIdentifier = typeIdentifier;
            Role = role;
            PositionNumber = positionNumber;
            BuiltIn = builtIn;
            FirmwareVersion = firmwareVersion;
            Comment = comment;
            Label = label;
            Attributes = attributes == null ? null : new AttributeMap<string>(attributes);
            Nodes = HmiContracts.OptionalList(nodes);
            DeviceItems = HmiContracts.OptionalList(deviceItems);
            Validate();
        }

        internal override void Validate()
        {
            V4Validation.Text(Name, "name");
            if (Attributes != null) HmiRules.TextMap(Attributes);
            V4Validation.Require(Role == null || HmiRules.AmlRoles.Contains(Role.Trim()), "Unknown AML role.");
            V4Validation.Require(BuiltIn == true || Role != null && Role.Trim() != "Rack" && Role.Trim() != "DeviceItem"
                || !string.IsNullOrWhiteSpace(TypeIdentifier), "A rack or device item requires typeIdentifier unless builtIn.");
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class AmlNodeSpec : HmiObject
    {
        public string? Name { get; }
        public string? NetworkAddress { get; }
        public string? SubnetMask { get; }
        public string? RouterAddress { get; }
        public string? SubnetName { get; }
        public string? PnDeviceName { get; }

        public AmlNodeSpec(string? name = null, string? networkAddress = null, string? subnetMask = null,
            string? routerAddress = null, string? subnetName = null, string? pnDeviceName = null)
        {
            Name = name;
            NetworkAddress = networkAddress;
            SubnetMask = subnetMask;
            RouterAddress = routerAddress;
            SubnetName = subnetName;
            PnDeviceName = pnDeviceName;
            Validate();
        }

        internal override void Validate()
        {
            HmiRules.NetworkAddress(NetworkAddress);
        }
    }

    [JsonConverter(typeof(HmiJsonConverterFactory))]
    public sealed class AmlSubnetSpec : HmiObject
    {
        public string Name { get; }
        public string? Type { get; }

        public AmlSubnetSpec(string name, string? type = null)
        {
            Name = name;
            Type = type;
            Validate();
        }

        internal override void Validate()
        {
            V4Validation.Text(Name, "name");
        }
    }

}
