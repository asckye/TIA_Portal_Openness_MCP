#nullable disable
#if TIA_ENGINE_LOCAL_PRIMITIVES
#define PLC_HARDWARE_CATALOG_ENTRIES
namespace TiaMcpServer.Siemens.LocalHardware
#else
namespace TiaMcp.Adapters.Hardware
#endif
{
    using global::Siemens.Engineering;
    using global::Siemens.Engineering.HW;
#if PLC_HARDWARE_CATALOG_ENTRIES
    using global::Siemens.Engineering.HW.HardwareCatalog;
#endif

    // Raw shared members only; hosts retain catalog selection, retries and creation policy.
    public static class HardwarePrimitives
    {
#if PLC_SAFETY || TIA_ENGINE_LOCAL_PRIMITIVES
        public static DeviceComposition Devices(ProjectBase project)
#else
        public static DeviceComposition Devices(Project project)
#endif
            => project.Devices;
        public static Device CreateWithItem(DeviceComposition devices, string typeIdentifier, string itemName, string deviceName)
            => devices.CreateWithItem(typeIdentifier, itemName, deviceName);
#if PLC_HARDWARE_CATALOG_ENTRIES
        public static string ArticleNumber(CatalogEntry entry) => entry.ArticleNumber;
        public static string CatalogPath(CatalogEntry entry) => entry.CatalogPath;
        public static string Description(CatalogEntry entry) => entry.Description;
        public static string TypeIdentifier(CatalogEntry entry) => entry.TypeIdentifier;
        public static string TypeIdentifierNormalized(CatalogEntry entry) => entry.TypeIdentifierNormalized;
        public static string TypeName(CatalogEntry entry) => entry.TypeName;
        public static string Version(CatalogEntry entry) => entry.Version;
#endif
    }
}
