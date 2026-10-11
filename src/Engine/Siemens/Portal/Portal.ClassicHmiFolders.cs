using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Globalization;
using Siemens.Engineering.Hmi.RuntimeScripting;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using TiaMcpServer.ModelContextProtocol;
using Logic = TiaMcpServer.Siemens.ClassicHmiFoldersLogic;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private static VBScriptFolder ExactVbScriptFolder(HmiTarget hmi, IEnumerable<string> folderPath)
        {
            try { return TiaMcp.Adapters.Native.Hmi.HmiAccessHelpers.ExactVbScriptFolder(hmi, folderPath); }
            catch (TiaMcp.Adapters.Contracts.PlcSoftwareException error)
            { throw new PortalException((PortalErrorCode)Enum.Parse(typeof(PortalErrorCode), error.Code), error.Message); }
        }
        private static JsonNode? HmiValueJson(object value)
        {
            var rendered = TiaMcp.Adapters.Native.Hmi.HmiValueTypes.Render(value);
            return rendered == null ? null : System.Text.Json.JsonSerializer.SerializeToNode(rendered);
        }
    }
}
