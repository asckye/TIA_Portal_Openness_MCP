using static TiaMcpServer.Siemens.EngineeringSessionHelpers;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        #region software - TechnologyObjects

        public void ImportTechnologyObject(string softwarePath, string folderPath, string importPath)
        {
            // Keep the existing public entry point and its overwrite behavior.
            ImportTechnologyObject(softwarePath, folderPath, importPath, true, new List<string>());
        }

        private void ImportTechnologyObject(string softwarePath, string folderPath, string importPath, bool overwrite, List<string> importedNames)
        {
            try { new TiaMcp.Adapters.Native.Plc.PlcOrganisationAdapter(new TiaMcpServer.Worker.EnginePlcOrganisationSession(this)).ImportTechnologyObject(softwarePath, folderPath, importPath, overwrite, importedNames); }
            catch (TiaMcp.Adapters.Contracts.PlcSoftwareException error) { throw new PortalException(PortalErrorCode.NotFound, error.Message); }
        }

        #endregion
    }
}
