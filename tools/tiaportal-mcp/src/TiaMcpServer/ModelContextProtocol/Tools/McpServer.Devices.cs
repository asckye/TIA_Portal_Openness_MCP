using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;


namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        #region devices

        public static ResponseStringList ProbeHardwareHmiConnectionOwnerCandidates(
            string plcRootPath,
            string hmiRootPath,
            bool deepScan = true)
            => ((HardwareNetworkTools)EngineServices.Get(typeof(HardwareNetworkTools))).ProbeHardwareHmiConnectionOwnerCandidates(plcRootPath, hmiRootPath, deepScan);

        public static ResponseStringList ProbeHardwareHmiConnectionWhitelistedServices(
            string plcRootPath,
            string hmiRootPath,
            bool deepScan = true)
            => ((HardwareNetworkTools)EngineServices.Get(typeof(HardwareNetworkTools))).ProbeHardwareHmiConnectionWhitelistedServices(plcRootPath, hmiRootPath, deepScan);


        #endregion
    }
}
