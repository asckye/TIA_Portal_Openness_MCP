#nullable disable
using System;
using PlcNative = TiaMcp.Adapters.Native.Plc.PlcBlockPrimitives;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
#if TIA_ADAPTER_INTERNAL_VERSIONING
using TiaMcp.Adapters.Contracts.Studio;
using TiaMcp.Adapters.Contracts.Studio.Errors;
#else
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Models.Errors;
#endif
#if TIA_ADAPTER_INTERNAL_VERSIONING
using IVersionControl = TiaMcp.Adapters.Contracts.IVersionControl;
using ITiaSession = TiaMcp.Adapters.Contracts.IStudioSession;
using IVciReadOnlySession = TiaMcp.Adapters.Contracts.IStudioVciReader;
#else
using TiaOpenness.Core.Abstractions;
#endif
#if !TIA_ADAPTER_INTERNAL_VERSIONING
using TiaOpenness.Core.Inspection;
#endif

namespace TiaOpenness.Openness
{
    /// <summary>
    /// The real <see cref="ITiaSession"/>, backed by Siemens.Engineering.
    ///
    /// Lives only inside the bridge process, which is bound to exactly one Openness version
    /// by <c>OpennessAssemblyResolver</c> before this type is ever touched.
    /// </summary>
    internal sealed partial class OpennessSession : ITiaSession, IVciReadOnlySession
    {
        public IReadOnlyList<TagTableInfo> ListTagTables(string deviceId)
        {
            PlcContext plc;
            if (!TryGetPlc(deviceId, out plc)) return new List<TagTableInfo>();

            return plc.TagTables
                .Select(pair => PlcNavigator.Describe(pair.Value, pair.Key))
                .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<TagInfo> ListTags(string deviceId, string tableName)
        {
            PlcContext plc;
            if (!TryGetPlc(deviceId, out plc)) return new List<TagInfo>();


            IEnumerable<KeyValuePair<string, PlcTagTable>> tables = plc.TagTables;
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                tables = plc.TagTables.Where(p =>
                    string.Equals(p.Key, tableName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Value.Name, tableName, StringComparison.OrdinalIgnoreCase));

                if (!tables.Any())
                {
                    throw new KeyNotFoundException("No tag table named '" + tableName + "' on device '" + deviceId +
                        "'. Known tables: " + string.Join(", ", plc.TagTables.Keys));
                }
            }

            return tables
                .SelectMany(pair => pair.Value.Tags.SafeEnumerate()
                    .Select(tag => PlcNavigator.Describe(tag, pair.Value.Name)))
                .ToList();
        }

        // ---- compile -------------------------------------------------------

        public CompileResult CompileDevice(string deviceId, bool softwareOnly)
        {
            var plc = RequirePlc(deviceId);

            ICompilable compilable;
            if (softwareOnly)
            {
                compilable = plc.Software.GetService<ICompilable>();
            }
            else
            {
                var device = PlcNavigator.AllDevices(_project)
                    .FirstOrDefault(d => string.Equals(d.Name, plc.DeviceId, StringComparison.OrdinalIgnoreCase));
                var carrier = device == null
                    ? null
                    : PlcNavigator.AllDeviceItems(device).FirstOrDefault(i => i.GetService<ICompilable>() != null);
                compilable = carrier?.GetService<ICompilable>() ?? plc.Software.GetService<ICompilable>();
            }

            if (compilable == null)
            {
                throw new NotSupportedException("Device '" + deviceId + "' does not offer a compile service.");
            }

            var stopwatch = Stopwatch.StartNew();
            var compilerResult = PlcNative.Compile(compilable);
            stopwatch.Stop();

            Index(plc);

            return new CompileResult
            {
                State = PlcNative.State(compilerResult).ToString(),
                ErrorCount = PlcNative.ErrorCount(compilerResult),
                WarningCount = PlcNative.WarningCount(compilerResult),
                Duration = stopwatch.Elapsed,
                Messages = PlcNative.Messages(compilerResult).SafeEnumerate().Select(Describe).ToList(),
            };
        }

        private static CompileMessage Describe(CompilerResultMessage message)
        {
            return new CompileMessage
            {
                Severity = SeverityOf(PlcNative.State(message).ToString()),
                Description = PlcNative.Description(message),
                Target = PlcNative.Path(message),
                ErrorCode = message.Prop<string>("ErrorCode"),
                Children = PlcNative.Messages(message).SafeEnumerate().Select(Describe).ToList(),
            };
        }

        private static CompileSeverity SeverityOf(string state)
        {
            if (string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase)) return CompileSeverity.Error;
            if (string.Equals(state, "Warning", StringComparison.OrdinalIgnoreCase)) return CompileSeverity.Warning;
            return CompileSeverity.Information;
        }

        // ---- inspection ----------------------------------------------------

#if TIA_ADAPTER_INTERNAL_VERSIONING
        public string FindPlcDeviceId(string deviceId)
        {
            PlcContext plc;
            return TryGetPlc(deviceId, out plc) ? plc.DeviceId : null;
        }
#else
        public InspectionReport Inspect(string deviceId, InspectionOptions options)
        {
            PlcContext plc;
            if (!TryGetPlc(deviceId, out plc))
            {
                return new InspectionReport { TimestampUtc = DateTimeOffset.UtcNow, DeviceId = deviceId };
            }

            var blocks = ListBlocks(deviceId, includeSystemBlocks: false);

            // An incomplete export scan cannot establish that a block is unused.
            ISet<string> referenced = null;

            return InspectionEngine.Run(plc.DeviceId, blocks, options, referenced);
        }

#endif

        /// <summary>
        /// Exports HMI screens and tag tables. Same contract as the PLC path — one file per object,
        /// folders preserved, each failure reported against the object it belongs to.
        /// </summary>
        private ExportResult ExportHmi(string deviceId, IReadOnlyList<string> paths, string outputDirectory,
            bool preserveFolders, ProgressCallback progress)
        {
            HmiContext hmi;
            if (!TryGetHmi(deviceId, out hmi))
            {
                return new ExportResult { OutputDirectory = outputDirectory };
            }

            var wanted = paths == null || paths.Count == 0
                ? hmi.Items.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()
                : paths.Where(hmi.Items.ContainsKey).ToList();

            Directory.CreateDirectory(outputDirectory);
            var result = new ExportResult { OutputDirectory = outputDirectory, Requested = wanted.Count };

            for (var i = 0; i < wanted.Count; i++)
            {
                var path = wanted[i];
                if (progress != null) progress("export", i + 1, wanted.Count, path);

                var item = new ExportedItem { BlockPath = path };
                try
                {
                    var folder = hmi.Folders.TryGetValue(path, out var known) ? known : string.Empty;
                    var directory = preserveFolders
                        ? Path.Combine(outputDirectory, SafeFolder(folder))
                        : outputDirectory;
                    Directory.CreateDirectory(directory);

                    var name = path.Substring(path.LastIndexOf('/') + 1);
                    item.FilePath = HmiNavigator.Export(hmi.Items[path], directory, SafeFileName(name));
                    item.Succeeded = true;
                    result.Succeeded++;
                }
                catch (Exception ex)
                {
                    item.Succeeded = false;
                    item.Error = Flatten((ex.InnerException ?? ex).Message);
                    result.Failed++;
                }
                result.Items.Add(item);
            }

            return result;
        }

        /// <summary>
        /// The indexed screens and tag tables of one HMI panel, built on first use like a PLC's.
        /// False when the device carries no HMI software.
        /// </summary>
    }
}
