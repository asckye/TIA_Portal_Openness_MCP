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
        public string ProbeClassicHmiConnectionCreation(string softwarePath, string connectionName, string exportPath)
        {
            var sb = new StringBuilder();
            if (IsProjectNull()) return "Project is null";

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null) return "HMI software not found: " + softwarePath;

            var sw = softwareContainer.Software;
            var connections = TryGetPropertyValue(sw, "Connections");
            if (connections == null) return "Connections collection not found. swType=" + sw.GetType().FullName;

            sb.AppendLine("SoftwareType=" + (sw.GetType().FullName ?? sw.GetType().Name));
            sb.AppendLine("ConnectionsType=" + (connections.GetType().FullName ?? connections.GetType().Name));
            sb.AppendLine("ConnectionsPublicMembers:");
            foreach (var line in DescribeTypeMembers(connections.GetType(), false).Take(120))
            {
                sb.AppendLine("  " + line);
            }
            sb.AppendLine("ConnectionsExplicitMembers:");
            foreach (var line in DescribeTypeMembers(connections.GetType(), true).Take(160))
            {
                sb.AppendLine("  " + line);
            }

            var connectionType = FindTypeBySuffix("Siemens.Engineering.Hmi.Communication.Connection")
                ?? FindTypeBySuffix("Hmi.Communication.Connection")
                ?? FindTypeBySuffix("Communication.Connection");
            sb.AppendLine("ConnectionType=" + (connectionType?.FullName ?? "<not found>"));

            var existing = FindExistingByName(connections, connectionName);
            if (existing != null)
            {
                sb.AppendLine("ExistingConnection=" + connectionName);
                if (TryExportEngineeringObject(existing, exportPath, out var existingExportErr))
                {
                    sb.AppendLine("ExportExisting=OK :: " + exportPath);
                }
                else
                {
                    sb.AppendLine("ExportExisting=FAIL :: " + existingExportErr);
                }
                return sb.ToString();
            }

            if (connectionType == null)
            {
                sb.AppendLine("Create=SKIP :: connection type not found");
                return sb.ToString();
            }

            sb.AppendLine("CreationInfos:");
            var creationInfos = TryInvokeExplicitEngineeringMethod(connections, "GetCreationInfos", Array.Empty<object?>(), out var creationInfoErr);
            if (creationInfos == null && !string.IsNullOrWhiteSpace(creationInfoErr))
            {
                sb.AppendLine("  GetCreationInfos(\"\") failed: " + creationInfoErr);
            }
            else
            {
                foreach (var line in FormatEnumerableObjects(creationInfos, 80))
                {
                    sb.AppendLine("  " + line);
                }
            }

            object? created = null;
            string? createErr = null;
            var attempts = new[]
            {
                new { Description = "Name only", Parameters = new Dictionary<string, object?> { ["Name"] = connectionName } }
            };

            foreach (var attempt in attempts)
            {
                try
                {
                    sb.AppendLine($"CreateAttempt {attempt.Description}");
                    created = TryInvokeExplicitEngineeringMethod(
                        connections,
                        "Create",
                        new object?[] { connectionType, attempt.Parameters },
                        out createErr);
                    if (created != null)
                    {
                        sb.AppendLine("Create=OK :: type=" + (created.GetType().FullName ?? created.GetType().Name));
                        break;
                    }
                    sb.AppendLine("Create=FAIL :: " + (createErr ?? "<null result>"));
                }
                catch (Exception ex)
                {
                    createErr = FormatExceptionDetail(ex);
                    sb.AppendLine("Create=ERR :: " + createErr);
                }
            }

            created ??= FindExistingByName(connections, connectionName);
            if (created == null)
            {
                sb.AppendLine("Readback=FAIL :: connection not found after create attempts");
                return sb.ToString();
            }

            sb.AppendLine("Readback=OK :: " + (TryGetName(created) ?? connectionName));
            if (TryExportEngineeringObject(created, exportPath, out var exportErr))
            {
                sb.AppendLine("ExportCreated=OK :: " + exportPath);
            }
            else
            {
                sb.AppendLine("ExportCreated=FAIL :: " + exportErr);
            }

            return sb.ToString();
        }

        public ResponseImportBatch ImportHmiScreensFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
            => ((Services.HmiExchangeService)EngineServices.Get(typeof(Services.HmiExchangeService))).ImportHmiScreensFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);

        public ResponseImportBatch ImportHmiTagTablesFromDirectory(string softwarePath, string folderPath, string dir, string regexName = "", bool overwrite = true)
            => ((Services.HmiExchangeService)EngineServices.Get(typeof(Services.HmiExchangeService))).ImportHmiTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);

    }
}
