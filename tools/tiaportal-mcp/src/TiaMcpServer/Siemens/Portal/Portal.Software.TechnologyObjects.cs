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
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

            var plc = ResolvePlc(softwarePath, PlcAccess.Write);
            if (plc == null) throw new PortalException(PortalErrorCode.NotFound, $"PlcSoftware not found at '{softwarePath}'" + AvailablePlcPathsSuffix());

            try
            {
                // Use the typed TechnologicalObjectGroup property.
                // Native observation: the misspelled reflective lookup fell through to the PLC with "TechnologyObjects collection not found".
                // TIA version/date were not recorded; see docs/reference/real-machine-ledger.md.
                var group = (global::Siemens.Engineering.SW.TechnologicalObjects.TechnologicalInstanceDBGroup)EngineeringGroupOperations.Group(plc.TechnologicalObjectGroup, folderPath ?? "");
                var col = group.TechnologicalObjects;
                if (TryImportEngineeringObjectIntoCollection(col, importPath, overwrite, importedNames, out var err)) return;
                throw new PortalException(PortalErrorCode.ImportFailed, err ?? "ImportTechnologyObject failed");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.ImportFailed, ex.Message, null, ex);
            }
        }

        #endregion
    }
}
