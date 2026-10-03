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
            VBScriptFolder current = hmi.VBScriptFolder ?? throw new NotSupportedException("VBScriptFolder unavailable.");
            foreach (var part in folderPath) { VBScriptUserFolderComposition folders = current.Folders; current = folders.Find(part) ?? throw new PortalException(PortalErrorCode.NotFound, "Script folder not found: " + part); }
            return current;
        }
    }
}
