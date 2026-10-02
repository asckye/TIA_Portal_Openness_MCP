using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Features whose availability depends on the connected TIA Portal major version.
    /// Only model differences that actually fork the code today — extend deliberately.
    /// </summary>
    public enum TiaFeature
    {
        /// <summary>Hardware-level HMI connection creation via Siemens.Engineering.HW.CommunicationConnections (V21+ only; absent on V20).</summary>
        HardwareHmiConnection,

        /// <summary>SIMATIC SD / S7DCL document export (ExportAsDocuments), available V20 and newer.</summary>
        DocumentExport
    }

    public class CapabilityInfo
    {
        public string Feature { get; set; } = "";
        public bool Supported { get; set; }
        public int MinVersion { get; set; }
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// Single source of truth for version-gated feature availability. Replaces scattered
    /// silent no-ops with an explicit, queryable answer so the model knows up front what
    /// the connected portal can do instead of discovering it through a failed call.
    /// </summary>
    internal static class Capability
    {
        // Minimum TIA major version that supports each feature.
        private static readonly IReadOnlyDictionary<TiaFeature, int> MinVersion =
            new Dictionary<TiaFeature, int>
            {
                [TiaFeature.HardwareHmiConnection] = 21,
                [TiaFeature.DocumentExport] = 20
            };

        private static readonly IReadOnlyDictionary<TiaFeature, string> Notes =
            new Dictionary<TiaFeature, string>
            {
                [TiaFeature.HardwareHmiConnection] = "Siemens.Engineering.HW.CommunicationConnections is not exposed on TIA V20; this build provides the API route on V21 only.",
                [TiaFeature.DocumentExport] = "ExportAsDocuments API route exists in the V20/V21 engines. Availability is not proof of language, update-level, object or round-trip support; SIMATIC SD content restrictions still apply."
            };

        /// <summary>Unknown, planned and unrecognized versions fail closed.</summary>
        public static bool IsSupported(TiaFeature feature)
        {
            return IsSupported(feature, Engineering.TiaMajorVersion);
        }

        internal static bool IsSupported(TiaFeature feature, int major)
        {
            int minimum;
            return MinVersion.TryGetValue(feature, out minimum)
                && TiaMcp.Versioning.TiaVersionCatalog.Runnable.Any(v => v.MajorVersion == major)
                && major >= minimum;
        }

        /// <summary>Throws <see cref="PortalException"/> (NotSupportedOnVersion) when the feature is unavailable on the connected version.</summary>
        public static void RequireSupported(TiaFeature feature)
        {
            if (!IsSupported(feature))
                throw new PortalException(PortalErrorCode.NotSupportedOnVersion, Describe(feature));
        }

        /// <summary>Human-readable explanation of why a feature is/isn't available, including the version requirement.</summary>
        public static string Describe(TiaFeature feature)
        {
            return Notes.TryGetValue(feature, out var note) ? note : $"{feature} has no registered version capability.";
        }

        /// <summary>Snapshot of every feature's availability against the connected version, for Bootstrap to advertise.</summary>
        public static List<CapabilityInfo> Snapshot()
        {
            return MinVersion.Keys
                .Select(f => new CapabilityInfo
                {
                    Feature = f.ToString(),
                    Supported = IsSupported(f),
                    MinVersion = MinVersion[f],
                    Note = Describe(f)
                })
                .ToList();
        }
    }
}
