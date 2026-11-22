using System;
using System.Linq;

namespace TiaMcp.Adapters.Contracts
{
    // API availability is separate from runtime feature presence and acceptance.
    public static class HardwareCapabilities
    {
        public static System.Collections.Generic.IReadOnlyList<string> TransferAreaTypes { get; } = Array.AsReadOnly(new[] {
            "None", "MS", "CD", "F_PS", "TM", "IN", "OUT", "MSI", "MSO", "MSO_LOCAL", "RECORD_WRITE_STO", "RECORD_WRITE_PUB", "RECORD_READ_STO",
                "RECORD_READ_PUB",
            "MSI_MSO", "IN_OUT", "LOCAL_RECORD_STO", "LOCAL_RECORD_PUB", "SUB_MSI", "SUB_MSO", "SUB_LOCAL_RECORD_STO_READ", "SUB_LOCAL_RECORD_PUB_READ",
                "PROFISAFE_IN12_OUT6", "PROFISAFE_IN6_OUT12",
            "F_Proxy_CD", "DDX", "ISOC_STATUS_CONTROL", "ISOCHRON_IN", "ISOCHRON_OUT", "F_CD" });

        public static string
            ? Unsupported(string release, string operation, string action = "", string family = "", int positionNumber = -1, int extendedPositionNumber = -1,
                bool hasPassword = false)
            => Unsupported(release, operation, action, family, positionNumber, extendedPositionNumber, hasPassword, "");

        public static string? Unsupported(string release, string operation, string action, string family, int positionNumber, int extendedPositionNumber,
            bool hasPassword, string transferAreaType)
        {
            int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release, System.Globalization.CultureInfo.InvariantCulture);
            string name = operation.Substring(operation.LastIndexOf('.') + 1);
            if ((name == "ManageHardwareObject" || name == "HardwareManageHardwareObject") && action == "changeType" && version < 18)
                return "DeviceItem.ChangeType requires V18.";
            if ((name == "ListCommunicationConnections" || name == "ManageCommunicationConnection"
                || name == "HardwareReadCommunicationConnections" || name == "HardwareManageCommunicationConnection")
                    && version < OpennessApi.CommunicationConnections.FirstMajor)
                return "CommunicationConnections requires V21.";
            if ((name == "ListTransferAreas" || name == "ManageTransferArea" || name == "HardwareReadTransferAreas" || name == "HardwareManageTransferArea")
                    && version < 15)
                return "TransferAreas requires V15.1.";
            if ((name == "ManageTransferArea" || name == "HardwareManageTransferArea") && release == "15.1"
                && action == "create" && family != "multicast" && TransferAreaTypes.Contains(transferAreaType)
                && transferAreaType is not ("None" or "MS" or "CD" or "F_PS" or "TM"))
                return "V15.1 supports only None, MS, CD, F_PS and TM for standard transfer area type.";
            if ((name == "ListNetworkDomains" || name == "ManageNetworkDomain" || name == "HardwareReadNetworkDomains" || name == "HardwareManageNetworkDomain")
                    && version < 16)
                return "MRP and Sync domains require V16.";
            if ((name == "ExchangeSystemDiagnosticsSettings" || name == "HardwareExchangeSystemDiagnosticsSettings") && version < 17)
                return "System diagnostics settings require V17.";
            if ((name == "CreateHardwareCatalogDevice" || name == "HardwareCreateCatalogDevice" || name == "SearchHardwareCatalog"
                        || name == "HardwareLegacySearchHardwareCatalog")
                    && version < OpennessApi.HardwareCatalog.FirstMajor)
                return "HardwareCatalog requires V18.";
            if ((name == "CreateGsdDevice" || name == "HardwareCreateGsdDevice") && version < OpennessApi.HardwareCatalog.FirstMajor)
                return "GSD device creation requires a HardwareCatalog TypeIdentifier (V18); the GSD:<file>/DAP/<id> identifier form is not verified for " +
                    "this release.";
            if (name == "ManageDeviceServiceObjects" || name == "HardwareManageDeviceServiceObjects")
            {
                if (family == "webApplications" && version < OpennessApi.DefaultWebPages.FirstMajor) return "DefaultWebPagesFeature requires V21.";
                if (family == "telecontrolDataPoints" && (version < OpennessApi.TelecontrolFiles.FirstMajor
                    || version < OpennessApi.TelecontrolPoints.FirstMajor && action != "export" && action != "import"))
                    return "Telecontrol export/import requires V20; typed data point actions require V21.";
                if (family == "certificateServices" && version < 18) return "CertificateManagementConfiguration requires V18.";
            }
            if ((name == "ManageHardwareUtilities" || name == "HardwareManageHardwareUtilities") && action == "exportCardReaderPsc" && version < 15)
                return "CardReaderPscProvider requires V15.1.";
            if ((name == "ManageHardwareUtilities" || name == "HardwareManageHardwareUtilities") && action == "normalizeTypeIdentifier" && version < 17)
                return "GetTypeIdentifierNormalized requires V17.";
            if ((name == "ManageTransferArea" || name == "HardwareManageTransferArea") && family == "multicast" && version < 19)
                return "Multicast transfer area mutations require V19.";
            if ((name == "ListTransferAreas" || name == "ManageTransferArea" || name == "HardwareReadTransferAreas" || name == "HardwareManageTransferArea")
                    && version < 16
                && (extendedPositionNumber >= 0 || action == "create" && positionNumber >= 0))
                return "Extended positions and explicit creation positions require V16.";
            if ((name == "ManageHardwareUtilities" || name == "HardwareManageHardwareUtilities") && action == "exportCardReaderPsc" && hasPassword
                    && version < OpennessApi.EncryptedPscExport.FirstMajor)
                return "Encrypted PSC export requires V20.";
            return null;
        }
    }
}
