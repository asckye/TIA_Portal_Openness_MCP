using System;

namespace TiaMcp.Adapters.Contracts
{
    // API availability is separate from runtime feature presence and acceptance.
    public static class HardwareCapabilities
    {
        public static string? Unsupported(string release, string operation, string action = "", string family = "", int positionNumber = -1, int extendedPositionNumber = -1, bool hasPassword = false)
        {
            int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release);
            string name = operation.Substring(operation.LastIndexOf('.') + 1);
            if ((name == "ListCommunicationConnections" || name == "ManageCommunicationConnection"
                || name == "HardwareReadCommunicationConnections" || name == "HardwareManageCommunicationConnection") && version < 21)
                return "CommunicationConnections requires V21.";
            if ((name == "ListTransferAreas" || name == "ManageTransferArea" || name == "HardwareReadTransferAreas" || name == "HardwareManageTransferArea") && version < 15)
                return "TransferAreas requires V15.1.";
            if ((name == "ListNetworkDomains" || name == "ManageNetworkDomain" || name == "HardwareReadNetworkDomains" || name == "HardwareManageNetworkDomain") && version < 16)
                return "MRP and Sync domains require V16.";
            if ((name == "ExchangeSystemDiagnosticsSettings" || name == "HardwareExchangeSystemDiagnosticsSettings") && version < 17)
                return "System diagnostics settings require V17.";
            if ((name == "CreateHardwareCatalogDevice" || name == "HardwareCreateCatalogDevice" || name == "SearchHardwareCatalog" || name == "HardwareLegacySearchHardwareCatalog") && version < 18)
                return "HardwareCatalog requires V18.";
            if (name == "ManageDeviceServiceObjects" || name == "HardwareManageDeviceServiceObjects")
            {
                if (family == "webApplications" && version < 21) return "DefaultWebPagesFeature requires V21.";
                if (family == "telecontrolDataPoints" && (version < 20 || version == 20 && action != "export" && action != "import"))
                    return "Telecontrol export/import requires V20; typed data point actions require V21.";
                if (family == "certificateServices" && version < 18) return "CertificateManagementConfiguration requires V18.";
            }
            if ((name == "ManageHardwareUtilities" || name == "HardwareManageHardwareUtilities") && action == "exportCardReaderPsc" && version < 15)
                return "CardReaderPscProvider requires V15.1.";
            if ((name == "ManageHardwareUtilities" || name == "HardwareManageHardwareUtilities") && action == "normalizeTypeIdentifier" && version < 17)
                return "GetTypeIdentifierNormalized requires V17.";
            if ((name == "ManageTransferArea" || name == "HardwareManageTransferArea") && family == "multicast" && version < 20)
                return "Multicast transfer area mutations require V20.";
            if ((name == "ListTransferAreas" || name == "ManageTransferArea" || name == "HardwareReadTransferAreas" || name == "HardwareManageTransferArea") && version < 16
                && (extendedPositionNumber >= 0 || action == "create" && positionNumber >= 0))
                return "Extended positions and explicit creation positions require V16.";
            if ((name == "ManageHardwareUtilities" || name == "HardwareManageHardwareUtilities") && action == "exportCardReaderPsc" && hasPassword && version < 20)
                return "Encrypted PSC export requires V20.";
            return null;
        }
    }
}
