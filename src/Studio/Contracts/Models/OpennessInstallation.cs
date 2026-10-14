using System.Collections.Generic;

namespace TiaOpenness.Contracts.Models
{
    /// <summary>One TIA Portal / Openness installation discovered on the machine.</summary>
    public class OpennessInstallation
    {
        /// <summary>Openness API version, e.g. "21.0".</summary>
        public string Version { get; set; }
        /// <summary>Full path to the assembly that anchors the API for this version.</summary>
        public string EngineeringDllPath { get; set; }
        /// <summary>Directory holding the public API assemblies.</summary>
        public string PublicApiDirectory { get; set; }
        /// <summary>How the entry was found: "Registry" or "Probe".</summary>
        public string DiscoveredBy { get; set; }

        /// <summary>
        /// True from V21 on, where Siemens split the monolithic Siemens.Engineering.dll into
        /// Siemens.Engineering.Base.dll, .Step7.dll, .WinCC.dll and friends, moved them under a
        /// <c>net48</c> subfolder, and re-signed them with a new public key token. Code built
        /// against V20 or earlier does not run on V21 and vice versa.
        /// </summary>
        public bool IsModular { get; set; }

        /// <summary>File name of the anchor assembly: Siemens.Engineering.Base.dll from V21, else Siemens.Engineering.dll.</summary>
        public string PrimaryAssembly { get; set; }

        /// <summary>Every Siemens.Engineering* assembly in <see cref="PublicApiDirectory"/>.</summary>
        public List<string> Assemblies { get; set; } = new List<string>();
    }
}
