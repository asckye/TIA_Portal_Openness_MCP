using System.Text.Json;
using System.Text.Json.Nodes;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Plc;
namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private static JsonObject TechnologyObjectRow(TechnologicalInstanceDB db) => JsonSerializer.SerializeToNode(PlcOrganisationAdapter.ReadTechnologyObjectEvidence(db))!.AsObject();
        private static JsonObject ParameterRow(TechnologicalParameter parameter) => JsonSerializer.SerializeToNode(PlcOrganisationAdapter.ReadTechnologyParameterEvidence(parameter))!.AsObject();
        private static JsonObject TypedMotionView(TechnologicalInstanceDB db) => JsonSerializer.SerializeToNode(PlcOrganisationAdapter.ReadTechnologyMotionEvidence(db))!.AsObject();
        private static JsonObject MappingRow(object mapping) => JsonSerializer.SerializeToNode(PlcOrganisationAdapter.ReadTechnologyMappingEvidence(mapping))!.AsObject();
        private static JsonObject InterfaceRow(object value) => JsonSerializer.SerializeToNode(PlcOrganisationAdapter.ReadTechnologyInterfaceEvidence(value))!.AsObject();
        private static bool DisconnectTyped(object value) => PlcOrganisationAdapter.DisconnectTechnologyInterface(value);
        private static bool? IsConnectedTyped(object value) => PlcOrganisationAdapter.TechnologyInterfaceConnected(value);
        private bool ConnectTyped(object value, MotionProDiagClassicHmiLogic.ConnectionTarget target, string softwarePath, ConnectOption option)
            => new PlcOrganisationAdapter(new TiaMcpServer.Worker.EnginePlcOrganisationSession(this)).ConnectTechnologyInterface(value,
                new MotionConnectionTarget { Mode = target.Mode, DevicePath = target.DevicePath, ItemPath = target.ItemPath, SecondItemPath = target.SecondItemPath,
                    DbMemberPath = target.DbMemberPath, PlcTagPath = target.PlcTagPath, ConnectOption = target.ConnectOption, HasConnectOption = target.HasConnectOption,
                    InputBitAddress = target.InputBitAddress, OutputBitAddress = target.OutputBitAddress, Address = target.Address, ChannelIndex = target.ChannelIndex,
                    ChannelType = target.ChannelType, ChannelIoType = target.ChannelIoType, ChannelNumber = target.ChannelNumber }, softwarePath, option);
    }
}
