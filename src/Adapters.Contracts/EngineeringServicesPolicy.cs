using System;
using System.Linq;

namespace TiaMcp.Adapters.Contracts
{
    public static class EngineeringServicesPolicy
    {
        private static void DevicePath(string[] names)
        {
            if (names == null || names.Length < 1 || names.Length > 64 || names.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("devicePath needs 1-64 exact nonempty names.");
        }
        private static void Text(string value, string parameter, int max)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value)
                throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        public static void ObjectSelection(string kind, string[] devicePath, string[] itemPath, string softwarePath, string objectPath, string identifier)
        {
            if (!new[] { "device", "deviceItem", "plcBlock", "plcType", "plcTagTable" }.Contains(kind, StringComparer.Ordinal))
                throw new ArgumentException("kind must be one of: device/deviceItem/plcBlock/plcType/plcTagTable (case-sensitive).");
            if (!string.IsNullOrEmpty(identifier)) { if (identifier.Length > 4096) throw new ArgumentException("identifier too long."); return; }
            if (kind == "device" || kind == "deviceItem")
            {
                DevicePath(devicePath);
                if (kind == "deviceItem" && (itemPath == null || itemPath.Length == 0))
                    throw new ArgumentException("kind=deviceItem needs a non-empty itemPath.");
            }
            else { Text(softwarePath, "softwarePath", 256); Text(objectPath, "objectPath", 1024); }
        }
        public static void EditorSelection(string kind, string[] devicePath, string[] itemPath, string softwarePath, string objectPath, string view)
        {
            if (!new[] { "", "device", "network", "topology" }.Contains(view, StringComparer.Ordinal))
                throw new ArgumentException("view must be empty or device/network/topology (case-sensitive).", nameof(view));
            if (view != "" && kind != "device") throw new ArgumentException("view is only supported for kind=device.", nameof(view));
            if (kind == "plcWatchTable" || kind == "plcForceTable")
            { Text(softwarePath, "softwarePath", 256); Text(objectPath, "objectPath", 1024); }
            else ObjectSelection(kind, devicePath, itemPath, softwarePath, objectPath, "");
        }
        public static void LibraryPair(string left, string right)
        {
            if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right))
                throw new ArgumentException("Comparing the project library to itself is refused; name at least one open global library.");
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Left and right library must differ.");
        }
        public static void Comparison(string kind, string software, string[] device, string project, string targetSoftware, string[] targetDevice,
                bool sameDevicePaths)
        {
            if (!new[] { "software", "softwareToLibrary", "hardware" }.Contains(kind, StringComparer.Ordinal))
                throw new ArgumentException("kind must be one of: software, softwareToLibrary, hardware");
            if (kind == "hardware") { DevicePath(device); DevicePath(targetDevice); }
            else if (string.IsNullOrWhiteSpace(software)) throw new ArgumentException("Exact softwarePath required.");
            if (kind == "software")
            {
                if (!string.IsNullOrWhiteSpace(project)) DevicePath(targetDevice);
                else if (string.IsNullOrWhiteSpace(targetSoftware))
                    throw new ArgumentException("targetSoftwarePath (same project) or targetProjectName + targetDevicePath/targetItemPath (other " +
                    "open project) required.");
                else if (string.Equals(software, targetSoftware, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Source and target software must differ.");
            }
            if (kind == "hardware" && string.IsNullOrWhiteSpace(project) && sameDevicePaths)
                throw new ArgumentException("Source and target device paths must differ unless targetProjectName names another open project (item paths are " +
                "compared natively).");
        }
    }
}
