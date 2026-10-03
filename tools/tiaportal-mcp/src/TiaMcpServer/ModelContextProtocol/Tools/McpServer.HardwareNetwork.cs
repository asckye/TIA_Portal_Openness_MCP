using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ReadDeviceAddressing"), Description("[L2][Hardware][READ] Addressing of the exact device or device item: HwIdentifiers (Identifier, controller owner paths), DeviceItem.Addresses (StartAddress, Length, IoType, AddressControllers, dynamic Context/ProcessImage/IsochronousMode/InterruptObNumber) and, when the item is a controller, AddressController.RegisteredAddresses / HwIdentifierController.RegisteredHwIdentifiers with owner paths. Paginated, no modification.")]
        public static ResponseMessage ReadDeviceAddressing(string devicePathJson, string itemPathJson="[]", int offset=0, int limit=100)
            => Portal.ReadDeviceAddressing(devicePathJson,itemPathJson,offset,limit);
        [McpServerTool(Name="UpdateDeviceAddress"), Description("[L2][Hardware][WRITE] Edit one exact Address of a device item, identified by ioType (Input/Output/Diagnosis/Substitute) and its current startAddress: propertiesJson StartAddress/Length and attributesJson ProcessImage/IsochronousMode/InterruptObNumber, each read back. processImageObName (with softwarePath) assigns the process image partition to that OB: Address.AssignProcessImageToOrganizationBlock on V20, the address's ProcessImageProvider service on V21. Changing StartAddress may move the opposite IoType of the module and never rewires tags. Default dryRun=true; no save/compile/download.")]
        public static ResponseMessage UpdateDeviceAddress(
            string devicePathJson,
            string itemPathJson,
            [Description("ioType: None | Input | Output | Substitute | Diagnosis.")] string ioType,
            [Description("startAddress: new start address (byte).")] int startAddress,
            string propertiesJson="{}",
            string attributesJson="{}",
            string softwarePath="",
            [Description("processImageObName: exact name of the OB the process image partition is assigned to ('' = automatic).")] string processImageObName="",
            bool dryRun=true)
            => Portal.UpdateDeviceAddress(devicePathJson,itemPathJson,ioType,startAddress,propertiesJson,attributesJson,softwarePath,processImageObName,dryRun);
    }
}
