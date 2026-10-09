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
using static TiaMcpServer.Siemens.EngineeringSessionHelpers;

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class TypesService
    {
        private readonly IEngineeringSession _session;

        private readonly HmiExchangeService _hmiExchange;

        public TypesService(IEngineeringSession session, HmiExchangeService hmiExchange)
        {
            _session = session;
            _hmiExchange = hmiExchange;
        }

        public IEnumerable<PlcType>? ExportTypes(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _session.Logger?.LogInformation("Exporting types...");

            if (_session.IsProjectNull())
            {
                return null;
            }

            var exportList = new List<PlcType>();
            var failures = new List<string>();

            PlcType[] list;

            try
            {
                list = (_session.GetTypes(softwarePath, regexName) is { } got ? got.ToArray() : []);
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "Failed to retrieve type list for {SoftwarePath}", softwarePath);
                return exportList;
            }

            TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,
                SoftwareContainerLookup.PathOf(_session.GetSoftwareContainer(softwarePath)), true);
            TiaOpenness.Shared.NativeExportPolicy.RequireConsistent("types", list.Where(x => !x.IsConsistent).Select(x => x.Name), "groupPath");

            for (int i = 0; i < list.Count(); i++)
            {
                var type = list[i];

                _session.Logger?.LogDebug("- Exporting type {Index}/{Total} : {Name}", i, list.Count(), type.Name);

                string path;
                if (preservePath)
                {
                    var groupPath = "";
                    if (type.Parent is PlcTypeGroup parentGroup)
                    {
                        groupPath = _session.GetPlcTypeGroupPath(parentGroup);
                    }
                    path = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{type.Name}.xml");
                }
                else
                {
                    path = Path.Combine(exportPath, $"{type.Name}.xml");
                }

                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception ioEx)
                        {
                            failures.Add($"{type.Name}: cannot delete existing file ({ioEx.Message})");
                            _session.Logger?.LogError(ioEx, "Delete failed for {File}", path);
                            continue;
                        }
                    }

                    try
                    {
                        type.Export(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)), ExportOptions.None);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{type.Name}: export failed ({ex.Message})");
                        _session.Logger?.LogError(ex, "Export failed for type {Type}", type.Name);
                        continue;
                    }

                    exportList.Add(type);
                }
                catch (Exception ex)
                {
                    failures.Add($"{type.Name}: unexpected exception ({ex.Message})");
                    _session.Logger?.LogError(ex, "Unexpected error at type {Type}", type.Name);
                }
            }

            if (failures.Count > 0)
            {
                _session.Logger?.LogWarning($"ExportPlcTypes completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
            }
            else
            {
                _session.Logger?.LogInformation($"ExportPlcTypes completed successfully. Exported {exportList.Count} types.");
            }

            return exportList;
        }

        public (string TempDir, List<string> Paths)? ExportTypesToTemp(string softwarePath, string regexName = "", bool preservePath = false)
        {
            var tempDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var list = ExportTypes(softwarePath, tempDir, regexName, preservePath);
            if (list == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }


        public (string TempDir, List<string> Paths)? ExportTypeToTemp(string softwarePath, string typePath, bool preservePath = false)
        {
            var tempDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var t = _session.ExportType(softwarePath, typePath, tempDir, preservePath);
            if (t == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }
    }
}
