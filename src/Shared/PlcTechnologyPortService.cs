using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed partial class PlcOrganisationPortService
    {
        private static ResponseImportBatch TechnologyBatch(JsonObject reply)
        {
            var batch = reply["Batch"]!.AsObject();
            return new ResponseImportBatch { Message = (string?)batch["Message"], Imported = JsonSerializer.Deserialize<List<string>>(batch["Imported"]!.ToJsonString()),
                Failed = JsonSerializer.Deserialize<List<ImportFailure>>(batch["Failed"]!.ToJsonString()), Meta = batch["Meta"]?.DeepClone() as JsonObject };
        }
        public void ImportTechnologyObject(string softwarePath, string folderPath, string importPath)
            => Invoke(new PlcSoftwareRequest { Operation = "ImportTechnologyObject", SoftwarePath = softwarePath, GroupPath = folderPath, FilePath = importPath, DryRun = false });
        public ResponseImportBatch ImportTechnologyObjectsFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
            => TechnologyBatch(Invoke(new PlcSoftwareRequest { Operation = "ImportTechnologyObjectsFromDirectory", SoftwarePath = softwarePath, GroupPath = folderPath, FilePath = dir, RegexName = regexName, Overwrite = overwrite, DryRun = false }));
        public ResponseImportBatch ExportTechnologyObjectsToDirectory(string softwarePath, string exportDir, string regexName = "")
            => TechnologyBatch(Invoke(new PlcSoftwareRequest { Operation = "ExportTechnologyObjectsToDirectory", SoftwarePath = softwarePath, FilePath = exportDir, RegexName = regexName }));
        private static HardwareScalar TechnologyScalar(JsonNode? value)
        {
            if (value == null) return new HardwareScalar { Kind = "null" };
            if (value is not JsonValue scalar) throw new ArgumentException("Only scalar values are supported.");
            if (scalar.TryGetValue<bool>(out var flag)) return new HardwareScalar { Kind = "boolean", Text = flag ? "true" : "false" };
            if (scalar.TryGetValue<string>(out var text)) return new HardwareScalar { Kind = "string", Text = text };
            return new HardwareScalar { Kind = "number", Text = scalar.ToJsonString() };
        }
        public ResponseMessage ReadTechnologyObjectTree(string softwarePath, string groupPath = "", bool includeParameters = false, bool includeMotionView = false, int maxDepth = 4)
            => OrganisationStep(new PlcSoftwareRequest { Operation = "GetTechnologyObjectTree", SoftwarePath = softwarePath, GroupPath = groupPath, IncludeParameters = includeParameters, IncludeMotionView = includeMotionView, MaxDepth = maxDepth });
        public ResponseMessage ManageTechnologyObject(string softwarePath, string objectPath, string action, string typeIdentifier = "", string version = "", string parameter = "", string valueJson = "null", bool dryRun = true)
        {
            var request = new PlcSoftwareRequest { Operation = "ManageTechnologyObject", SoftwarePath = softwarePath, Path = objectPath, Action = action, TypeIdentifier = typeIdentifier, Version = version, Parameter = parameter, DryRun = dryRun };
            try { request.Value = TechnologyScalar(JsonNode.Parse(valueJson)); } catch (Exception error) { request.ValidationError = error.Message; }
            return OrganisationStep(request);
        }
        public ResponseMessage ReadMotionAxisConfiguration(string softwarePath, string objectPath, bool includeParameters = false, int offset = 0, int limit = 100)
            => OrganisationStep(new PlcSoftwareRequest { Operation = "GetMotionAxisConfiguration", SoftwarePath = softwarePath, Path = objectPath, IncludeParameters = includeParameters, Offset = offset, Limit = limit });
        public ResponseMessage ManageMotionAxis(string softwarePath, string objectPath, string action, string aspect = "", string name = "", string targetJson = "{}", string propertiesJson = "{}", int sensorIndex = 0, bool confirmDelete = false, bool dryRun = true)
        {
            var request = new PlcSoftwareRequest { Operation = "ManageMotionAxis", SoftwarePath = softwarePath, Path = objectPath, Action = action, Aspect = aspect, Name = name, SensorIndex = sensorIndex, Confirm = confirmDelete, DryRun = dryRun };
            try {
                var target = MotionProDiagClassicHmiLogic.ParseConnectionTarget(targetJson);
                request.MotionTarget = new MotionConnectionTarget { Mode = target.Mode, DevicePath = target.DevicePath, ItemPath = target.ItemPath, SecondItemPath = target.SecondItemPath,
                    DbMemberPath = target.DbMemberPath, PlcTagPath = target.PlcTagPath, ConnectOption = target.ConnectOption, HasConnectOption = target.HasConnectOption,
                    InputBitAddress = target.InputBitAddress, OutputBitAddress = target.OutputBitAddress, Address = target.Address, ChannelIndex = target.ChannelIndex,
                    ChannelType = target.ChannelType, ChannelIoType = target.ChannelIoType, ChannelNumber = target.ChannelNumber };
            } catch (Exception error) { request.TargetError = error.Message; }
            try {
                var properties = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                request.Properties = properties.ToDictionary(p => p.Key, p => TechnologyScalar(p.Value));
            } catch (Exception error) { request.PropertiesError = error.Message; }
            return OrganisationStep(request);
        }
        public ResponseMessage ExchangeMotionCamData(string softwarePath, string objectPath, string action, string filePath, string format = "", string separator = "", int pointCount = 0, bool dryRun = true)
            => OrganisationStep(new PlcSoftwareRequest { Operation = "ExchangeMotionCamData", SoftwarePath = softwarePath, Path = objectPath, Action = action, FilePath = filePath, Format = format, Separator = separator, PointCount = pointCount, DryRun = dryRun });
        public ResponseMessage ConfigureMotionHardwareConnection(string softwarePath, string objectPath, string interfaceKind, string action, int inputBitAddress = 0, int outputBitAddress = 0, string connectOption = "Default", int sensorIndex = 0, bool dryRun = true)
            => OrganisationStep(new PlcSoftwareRequest { Operation = "ConfigureMotionHardwareConnection", SoftwarePath = softwarePath, Path = objectPath, Aspect = interfaceKind, Action = action, InputBitAddress = inputBitAddress, OutputBitAddress = outputBitAddress, CopyMode = connectOption, SensorIndex = sensorIndex, DryRun = dryRun });
    }
}
