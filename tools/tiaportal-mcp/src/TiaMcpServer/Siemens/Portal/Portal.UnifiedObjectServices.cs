using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private object ExactUnifiedRoot(string softwarePath)
        {
            var hmi = ResolveHmiSoftwareOrThrow(softwarePath);
            if (hmi.GetType().FullName != "Siemens.Engineering.HmiUnified.HmiSoftware") throw new NotSupportedException("Unified HMI required.");
            return hmi;
        }

    }
}
