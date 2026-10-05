using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW.Systemdiagnostics.Settings;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Types are resolved from the assembly that carries HardwareObject, so V20 (no CommunicationConnections) degrades to NotSupported.
        // V20 HardwareObject lacks GetService; Device/DeviceItem implement IEngineeringServiceProvider on both versions.
        private static IEngineeringServiceProvider ServiceProvider(HardwareObject owner)
            => owner as IEngineeringServiceProvider ?? throw new NotSupportedException(owner.GetType().FullName + " is not a service provider.");
    }
}
