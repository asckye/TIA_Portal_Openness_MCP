using System;
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
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Inspection;

namespace TiaOpenness.Openness
{
    /// <summary>
    /// The real <see cref="ITiaSession"/>, backed by Siemens.Engineering.
    ///
    /// Lives only inside the bridge process, which is bound to exactly one Openness version
    /// by <c>OpennessAssemblyResolver</c> before this type is ever touched.
    /// </summary>
    internal sealed class OpennessSession : ITiaSession
    {
        private readonly string _boundVersion;
        private TiaPortal _portal;
        private Project _project;
        private bool _withUi;
#if STUDIO_VCI
        private OpennessVersionControl _versionControl;
#endif
#if STUDIO_VCI
        private Project _versionControlOwner;
#endif

        /// <summary>Device name -> indexed software. Rebuilt whenever the project changes.</summary>
        private readonly Dictionary<string, PlcContext> _plcs =
            new Dictionary<string, PlcContext>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Device name -> indexed HMI screens and tag tables. Cleared with the PLC indexes.</summary>
        private readonly Dictionary<string, HmiContext> _hmis =
            new Dictionary<string, HmiContext>(StringComparer.OrdinalIgnoreCase);

        public OpennessSession(string boundVersion)
        {
            _boundVersion = boundVersion;
        }

        public SessionMode Mode { get { return SessionMode.Openness; } }
        public bool IsConnected { get { return _portal != null; } }
        public bool HasProject { get { return _project != null; } }

        /// <summary>
        /// The project's Version Control Interface, or null when the service is unavailable. Built once per project: the VCI object graph is COM-backed and
        /// dies if the service it came from is collected, so it must not be re-created per call.
        /// </summary>
        public IVersionControl VersionControl
        {
            get
            {
#if STUDIO_VCI
                if (_project == null) return null;

                if (_versionControl == null || !ReferenceEquals(_versionControlOwner, _project))
                {
                    if (!OpennessVersionControl.IsAvailable(_project)) return null;

                    _versionControl = new OpennessVersionControl(() => _project);
                    _versionControlOwner = _project;
                }
                return _versionControl;
#else
                return null;
#endif
            }
        }

        // ---- session -------------------------------------------------------

        public SessionState Connect(bool withUserInterface, bool attachToRunning, string version)
        {
            if (_portal != null) return GetState();

            _withUi = withUserInterface;

            if (attachToRunning)
            {
                _portal = TryAttach();
                if (_portal != null)
                {
                    Console.Error.WriteLine("[bridge] attached to a running TIA Portal");
                    Console.Error.Flush();

                    // An attached instance keeps whatever project it already had open.
                    _project = _portal.Projects.SafeEnumerate().FirstOrDefault();
                    if (_project != null) IndexProject();
                    return GetState();
                }
            }

            // Starting TIA Portal is TIA's own cost - a minute is normal - and there is nothing to
            // report while it happens, so say up front what is being waited on. Leaving an already
            // running TIA Portal open is much faster, because the branch above attaches to it.
            Console.Error.WriteLine("[bridge] no running TIA Portal to attach to; starting one, " +
                                    "which usually takes about a minute");
            Console.Error.Flush();

            _portal = new TiaPortal(withUserInterface
                ? TiaPortalMode.WithUserInterface
                : TiaPortalMode.WithoutUserInterface);

            return GetState();
        }

        /// <summary>
        /// Attaches to a running TIA Portal. Each attach can fail on its own (the instance may
        /// be shutting down, or owned by another user), so failures are skipped rather than fatal.
        /// </summary>
        private static TiaPortal TryAttach()
        {
            IList<TiaPortalProcess> processes;
            try { processes = TiaPortal.GetProcesses(); }
            catch (Exception) { return null; }

            foreach (var process in processes)
            {
                try { return process.Attach(); }
                catch (Exception) { /* try the next instance */ }
            }
            return null;
        }

        public void Disconnect()
        {
            _plcs.Clear();
            _hmis.Clear();
#if STUDIO_VCI
            _versionControl = null;
#endif
#if STUDIO_VCI
            _versionControlOwner = null;
#endif
            _project = null;

            if (_portal == null) return;
            try { _portal.Dispose(); }
            catch (Exception) { /* TIA may already be gone */ }
            _portal = null;
        }

        public SessionState GetState()
        {
            return new SessionState
            {
                Connected = _portal != null,
                Mode = SessionMode.Openness,
                OpennessVersion = _boundVersion,
                WithUserInterface = _withUi,
                OpenProject = _project == null ? null : Describe(_project),
            };
        }

        // ---- project -------------------------------------------------------

        public ProjectInfo OpenProject(string path)
        {
            RequireConnected();

            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException("Project file not found: " + path, path);
            }

            // Re-opening the same project would throw; reuse what is already open.
            var already = _portal.Projects.SafeEnumerate().FirstOrDefault(p =>
                string.Equals(p.Path?.FullName, file.FullName, StringComparison.OrdinalIgnoreCase));

            _project = already ?? _portal.Projects.Open(file);
            IndexProject();
            return Describe(_project);
        }

        public ProjectInfo GetProjectInfo()
        {
            RequireProject();
            return Describe(_project);
        }

        public void SaveProject()
        {
            RequireProject();
            _project.Save();
        }

        public void CloseProject()
        {
            if (_project == null) return;
            try { _project.Close(); }
            finally
            {
                _project = null;
    #if STUDIO_VCI
            _versionControl = null;
#endif
    #if STUDIO_VCI
            _versionControlOwner = null;
#endif
                _plcs.Clear();
            _hmis.Clear();
            }
        }

        private static ProjectInfo Describe(Project project)
        {
            return new ProjectInfo
            {
                Name = project.Name,
                Path = project.Path?.FullName,
                Author = project.Prop<string>("Author"),
                Comment = (project.Prop<object>("Comment") as MultilingualText).AsText(),
                CreationTime = project.Prop<DateTime>("CreationTime").AsOffset(),
                LastModified = project.Prop<DateTime>("LastModified").AsOffset(),
                IsModified = project.Prop("IsModified", false),
            };
        }

        // ---- devices and blocks --------------------------------------------

        public IReadOnlyList<DeviceInfo> ListDevices()
        {
            RequireProject();
            return PlcNavigator.AllDeviceEntries(_project).Select(PlcNavigator.Describe).ToList();
        }

        public IReadOnlyList<BlockInfo> ListBlocks(string deviceId, bool includeSystemBlocks)
        {
            PlcContext plc;
            if (!TryGetPlc(deviceId, out plc))
            {
                // An HMI panel has screens where a PLC has blocks. The same list serves both, so
                // one device selection shows whatever that device actually contains.
                HmiContext hmi;
                return TryGetHmi(deviceId, out hmi)
                    ? hmi.Items.Keys
                        .Select(path => HmiNavigator.Describe(hmi, path))
                        .OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : (IReadOnlyList<BlockInfo>)new List<BlockInfo>();
            }

            var blocks = new List<BlockInfo>();

            foreach (var pair in plc.Blocks)
            {
                var info = PlcNavigator.Describe(pair.Value, pair.Key);
                info.FolderPath = FolderIn(plc, pair.Key);
                if (!includeSystemBlocks && IsSystemBlock(info)) continue;
                blocks.Add(info);
            }

            foreach (var pair in plc.Types)
            {
                var type = PlcNavigator.Describe(pair.Value, pair.Key);
                type.FolderPath = FolderIn(plc, pair.Key);
                blocks.Add(type);
            }

            return blocks.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Blocks TIA generates itself (safety runtime, motion control, library instances).
        /// They compile from the project's own configuration, so exporting them is noise.
        /// </summary>
        private static bool IsSystemBlock(BlockInfo block)
        {
            if (block.Name.StartsWith("_", StringComparison.Ordinal)) return true;
            if (block.Number.HasValue && block.Number.Value >= 60000) return true;
            return false;
        }

        // ---- export --------------------------------------------------------

        public ExportResult ExportBlocks(string deviceId, IReadOnlyList<string> blockPaths, string outputDirectory,
            ExportFormat format, bool preserveFolders, ProgressCallback progress)
        {
            PlcContext plc;
            if (!TryGetPlc(deviceId, out plc)) return ExportHmi(deviceId, blockPaths, outputDirectory, preserveFolders, progress);

            var targets = ResolveExportTargets(plc, blockPaths);

            Directory.CreateDirectory(outputDirectory);
            var result = new ExportResult { OutputDirectory = outputDirectory, Requested = targets.Count };

            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (progress != null) progress("export", i + 1, targets.Count, target.Path);

                var item = new ExportedItem { BlockPath = target.Path };
                try
                {
                    var directory = preserveFolders
                        ? Path.Combine(outputDirectory, SafeFolder(target.Folder))
                        : outputDirectory;
                    Directory.CreateDirectory(directory);

                    item.FilePath = format == ExportFormat.SimaticMl
                        ? ExportAsXml(target, directory)
                        : ExportAsSource(plc, target, directory);

                    item.Succeeded = true;
                    result.Succeeded++;
                }
                catch (Exception ex)
                {
                    item.Succeeded = false;
                    item.Error = Explain(ex, target);
                    result.Failed++;
                }
                result.Items.Add(item);
            }

            return result;
        }

        private static string ExportAsXml(ExportTarget target, string directory)
        {
            var file = new FileInfo(Path.Combine(directory, SafeFileName(target.Name) + ".xml"));
            if (file.Exists) throw new IOException("Export file already exists: " + file.FullName);

            if (target.Block != null) target.Block.Export(file, ExportOptions.WithDefaults);
            else target.Type.Export(file, ExportOptions.WithDefaults);

            return file.FullName;
        }

        /// <summary>
        /// Text export goes through the external-source generator, which is the only route TIA
        /// offers to SCL/DB/UDT text. It only works for blocks written in a textual language.
        /// </summary>
        private static string ExportAsSource(PlcContext plc, ExportTarget target, string directory)
        {
            var extension = target.Type != null ? ".udt"
                : target.Block is DataBlock ? ".db"
                : ".scl";

            var file = new FileInfo(Path.Combine(directory, SafeFileName(target.Name) + extension));
            if (file.Exists) throw new IOException("Export file already exists: " + file.FullName);

            if (target.Block != null)
            {
                plc.Software.ExternalSourceGroup.GenerateSource(
                    new[] { target.Block }, file, GenerateOptions.None);
            }
            else
            {
                plc.Software.ExternalSourceGroup.GenerateSource(
                    new[] { target.Type }, file, GenerateOptions.None);
            }

            return file.FullName;
        }

        /// <summary>Turns the two failures engineers actually hit into instructions.</summary>
        /// <summary>
        /// Says why the export failed, taken from what TIA reported rather than from what the
        /// block's flags suggested. Leading with "block is inconsistent" because the flag said so,
        /// while TIA had actually rejected the file path, sent people off to compile a block that
        /// compiled perfectly well.
        /// </summary>
        private static string Explain(Exception ex, ExportTarget target)
        {
            var reported = Flatten(ex.Message);

            if (reported.IndexOf("not permitted", StringComparison.OrdinalIgnoreCase) >= 0
                || target.KnowHowProtected && reported.IndexOf("know-how", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Know-how protected: TIA does not permit exporting this block. Remove the protection first. "
                       + "(" + reported + ")";
            }

            if (reported.IndexOf("Inconsistent", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Inconsistent: TIA can only export a block that compiles. Compile the device first. "
                       + "(" + reported + ")";
            }

            // ProDiag and ProDiag_OB blocks are generated from the device's own diagnostic
            // configuration; TIA has no text or SimaticML form for them at all. Left as TIA's own
            // wording this reads like a fault to chase, when the only answer is that these blocks
            // are not exportable and the rest of the export is unaffected.
            if (reported.IndexOf("ProDiag", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "ProDiag: TIA cannot export this block in any format. It is generated from the "
                       + "device's diagnostic settings, so there is nothing to version. Deselect it. "
                       + "(" + reported + ")";
            }

            if (reported.IndexOf("not supported during import and export", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "TIA does not support exporting this block's programming language. "
                       + "(" + reported + ")";
            }

            if (reported.IndexOf("white-space", StringComparison.OrdinalIgnoreCase) >= 0
                || reported.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "TIA rejected the file path built from this block's name. Please report it with the name. "
                       + "(" + reported + ")";
            }

            return reported;
        }

        private static string Flatten(string message)
        {
            return string.Join(" ", (message ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));
        }

        // ---- import --------------------------------------------------------

        public ExportResult ImportBlocks(string deviceId, IReadOnlyList<string> files, bool overwrite,
            ProgressCallback progress)
        {
            var plc = RequirePlc(deviceId);
            var result = new ExportResult { Requested = files.Count };

            for (var i = 0; i < files.Count; i++)
            {
                var path = files[i];
                if (progress != null) progress("import", i + 1, files.Count, path);

                var item = new ExportedItem { FilePath = path };
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException("Import file not found.", path);

                    var extension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
                    item.BlockPath = extension == ".xml"
                        ? ImportSimaticMl(plc, path, overwrite)
                        : ImportExternalSource(plc, path, overwrite);

                    item.Succeeded = true;
                    result.Succeeded++;
                }
                catch (Exception ex)
                {
                    item.Succeeded = false;
                    item.Error = ex.Message;
                    result.Failed++;
                    result.Items.Add(item);
                    break;
                }
                result.Items.Add(item);
            }

            if (result.Failed == 0) Index(plc);
            return result;
        }

        private static string ImportSimaticMl(PlcContext plc, string path, bool overwrite)
        {
            var file = new FileInfo(path);
            var options = overwrite ? ImportOptions.Override : ImportOptions.None;

            // A UDT and a block both arrive as .xml; the root element decides which composition
            // accepts it, and TIA gives no way to ask beforehand.
            if (LooksLikeType(path))
            {
                var types = plc.Software.TypeGroup.Types.Import(file, options);
                return string.Join(", ", types.Select(t => t.Name));
            }

            var blocks = plc.Software.BlockGroup.Blocks.Import(file, options);
            return string.Join(", ", blocks.Select(b => b.Name));
        }

        private static bool LooksLikeType(string path)
        {
            try
            {
                using (var reader = new StreamReader(path))
                {
                    for (var i = 0; i < 40; i++)
                    {
                        var line = reader.ReadLine();
                        if (line == null) break;
                        if (line.IndexOf("SW.Types.PlcStruct", StringComparison.Ordinal) >= 0) return true;
                        if (line.IndexOf("SW.Blocks.", StringComparison.Ordinal) >= 0) return false;
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the block path; a wrong guess surfaces as a normal import error.
            }
            return false;
        }

        /// <summary>
        /// SCL/DB/UDT text has to become an external source first, then be compiled into blocks.
        /// The temporary source object is removed afterwards so it does not clutter the project.
        /// </summary>
        private static string ImportExternalSource(PlcContext plc, string path, bool overwrite)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".scl" && extension != ".db" && extension != ".udt")
                throw new NotSupportedException("Native source import accepts .scl, .db or .udt; use XML for other block formats.");
            if (!overwrite) throw new InvalidOperationException("Generating blocks from source can replace existing blocks. Confirm overwrite before importing source text.");
            // Own only this temporary source; never delete a pre-existing project source.
            var name = "TIA_Studio_" + Guid.NewGuid().ToString("N") + extension;
            var source = plc.Software.ExternalSourceGroup.ExternalSources.CreateFromFile(name, path);
            source.GenerateBlocksFromSource();
            source.Delete();
            return Path.GetFileNameWithoutExtension(path);
        }

        // ---- tags ----------------------------------------------------------

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
            var compilerResult = compilable.Compile();
            stopwatch.Stop();

            Index(plc);

            return new CompileResult
            {
                State = compilerResult.State.ToString(),
                ErrorCount = compilerResult.ErrorCount,
                WarningCount = compilerResult.WarningCount,
                Duration = stopwatch.Elapsed,
                Messages = compilerResult.Messages.SafeEnumerate().Select(Describe).ToList(),
            };
        }

        private static CompileMessage Describe(CompilerResultMessage message)
        {
            return new CompileMessage
            {
                Severity = SeverityOf(message.State.ToString()),
                Description = message.Description,
                Target = message.Path,
                ErrorCode = message.Prop<string>("ErrorCode"),
                Children = message.Messages.SafeEnumerate().Select(Describe).ToList(),
            };
        }

        private static CompileSeverity SeverityOf(string state)
        {
            if (string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase)) return CompileSeverity.Error;
            if (string.Equals(state, "Warning", StringComparison.OrdinalIgnoreCase)) return CompileSeverity.Warning;
            return CompileSeverity.Information;
        }

        // ---- inspection ----------------------------------------------------

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

        // ---- plumbing ------------------------------------------------------

        /// <summary>
        /// Drops the cached indexes. It does not build any.
        ///
        /// Indexing walks every block, data type and tag table of a PLC, and each step is a COM
        /// round trip. Doing that for every PLC the moment a project opened made opening a large
        /// project take as long as reading all of it - before the engineer had even chosen a
        /// device, and for devices they never looked at. Each PLC is now indexed the first time
        /// something actually asks for it.
        /// </summary>
        private void IndexProject()
        {
            _plcs.Clear();
            _hmis.Clear();
        }

        private static void Index(PlcContext context)
        {
            PlcNavigator.Index(context);
        }

        private List<ExportTarget> ResolveExportTargets(PlcContext plc, IReadOnlyList<string> blockPaths)
        {
            if (blockPaths == null || blockPaths.Count == 0)
            {
                return plc.Blocks.Select(p => ExportTarget.For(p.Key, FolderIn(plc, p.Key), p.Value))
                    .Concat(plc.Types.Select(p => ExportTarget.For(p.Key, FolderIn(plc, p.Key), p.Value)))
                    .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var targets = new List<ExportTarget>();
            var missing = new List<string>();

            foreach (var requested in blockPaths)
            {
                var blockMatch = plc.Blocks.FirstOrDefault(p => Matches(p.Key, p.Value.Name, requested));
                if (blockMatch.Value != null)
                {
                    targets.Add(ExportTarget.For(blockMatch.Key, FolderIn(plc, blockMatch.Key), blockMatch.Value));
                    continue;
                }

                var typeMatch = plc.Types.FirstOrDefault(p => Matches(p.Key, p.Value.Name, requested));
                if (typeMatch.Value != null)
                {
                    targets.Add(ExportTarget.For(typeMatch.Key, FolderIn(plc, typeMatch.Key), typeMatch.Value));
                    continue;
                }

                missing.Add(requested);
            }

            if (missing.Count > 0)
            {
                throw new KeyNotFoundException("Unknown block(s) on '" + plc.DeviceId + "': " +
                    string.Join(", ", missing));
            }
            return targets;
        }

        private static bool Matches(string path, string name, string requested)
        {
            return string.Equals(path, requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, requested, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A file name TIA will accept for this block.
        ///
        /// Block names in the field carry things a path cannot: slashes ("FB4 select / request from
        /// panel"), double spaces ("SEW  TT"), trailing spaces. Openness refuses the resulting path
        /// outright - "The argument 'path' contains additional white-space characters which is not
        /// allowed" - so runs of whitespace collapse to one space, the ends are trimmed, and
        /// anything a file name cannot hold becomes an underscore.
        /// </summary>
        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var text = new System.Text.StringBuilder(name.Length);
            var lastWasSpace = false;

            foreach (var character in name ?? string.Empty)
            {
                if (char.IsWhiteSpace(character))
                {
                    if (!lastWasSpace) text.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                lastWasSpace = false;
                text.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
            }

            var safe = text.ToString().Trim();
            return safe.Length == 0 ? "_" : safe;
        }

        /// <summary>The same rules applied to each level of a group path.</summary>
        private static string SafeFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return string.Empty;

            return string.Join(
                Path.DirectorySeparatorChar.ToString(),
                folder.Split('/').Select(SafeFileName).ToArray());
        }

        /// <summary>The group a block sits in, as recorded when the project was indexed.</summary>
        private static string FolderIn(PlcContext plc, string path)
        {
            string folder;
            return plc.Folders.TryGetValue(path, out folder) ? folder : string.Empty;
        }

        private void RequireConnected()
        {
            if (_portal == null) throw new InvalidOperationException("Not connected. Call session.connect first.");
        }

        private void RequireProject()
        {
            RequireConnected();
            if (_project == null) throw new InvalidOperationException("No project is open. Call project.open first.");
        }

        /// <summary>
        /// The indexed software of one PLC, built on first use and kept until the project changes.
        /// Throws when the device carries no PLC software; use <see cref="TryGetPlc"/> where an
        /// empty answer is the right one.
        /// </summary>
        private PlcContext RequirePlc(string deviceId)
        {
            PlcContext plc;
            if (TryGetPlc(deviceId, out plc)) return plc;

            throw new KeyNotFoundException("Device '" + deviceId + "' carries no PLC software, " +
                "so this operation does not apply to it.");
        }

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
        private bool TryGetHmi(string deviceId, out HmiContext hmi)
        {
            RequireProject();

            if (_hmis.TryGetValue(deviceId, out hmi)) return true;

            var device = PlcNavigator.AllDevices(_project).FirstOrDefault(d =>
                string.Equals(d.Name, deviceId, StringComparison.OrdinalIgnoreCase));

            var software = device == null ? null : PlcNavigator.FindSoftware(device);
            if (!HmiNavigator.IsHmiTarget(software)) return false;

            hmi = new HmiContext { DeviceId = device.Name, Target = software };
            HmiNavigator.Index(hmi);
            _hmis[device.Name] = hmi;
            return true;
        }

        /// <summary>
        /// The indexed software of one PLC, or false when this device has none.
        ///
        /// A project lists racks, HMIs and drives beside its PLCs, and the app shows all of them.
        /// Asking one of those for its blocks is an ordinary question with an empty answer, not a
        /// fault - reporting it as an error meant clicking a rack in the device list raised one.
        /// An unknown device name is still an error, because that is a caller mistake.
        /// </summary>
        private bool TryGetPlc(string deviceId, out PlcContext plc)
        {
            RequireProject();

            if (_plcs.TryGetValue(deviceId, out plc)) return true;

            var devices = PlcNavigator.AllDevices(_project).ToList();
            var device = devices.FirstOrDefault(d =>
                string.Equals(d.Name, deviceId, StringComparison.OrdinalIgnoreCase));

            if (device == null)
            {
                throw new KeyNotFoundException("No device named '" + deviceId + "'. Devices in this project: " +
                    (devices.Count == 0 ? "(none)" : string.Join(", ", devices.Select(d => d.Name))));
            }

            var software = PlcNavigator.FindPlcSoftware(device);
            if (software == null) return false;

            plc = new PlcContext { DeviceId = device.Name, Software = software };
            Index(plc);
            _plcs[device.Name] = plc;
            return true;
        }

        public void Dispose()
        {
            Disconnect();
        }

        /// <summary>A block or a UDT, addressed uniformly for export.</summary>
        private sealed class ExportTarget
        {
            public string Path;
            public string Name;
            /// <summary>The real group prefix, never derived by splitting Path: a block name may contain a slash.</summary>
            public string Folder;
            public PlcBlock Block;
            public PlcType Type;
            public bool KnowHowProtected;
            public bool Consistent;

            public static ExportTarget For(string path, string folder, PlcBlock block)
            {
                return new ExportTarget
                {
                    Path = path,
                    Folder = folder,
                    Name = block.Name,
                    Block = block,
                    KnowHowProtected = block.Prop("IsKnowHowProtected", false),
                    Consistent = block.Prop("IsConsistent", true),
                };
            }

            public static ExportTarget For(string path, string folder, PlcType type)
            {
                return new ExportTarget
                {
                    Path = path,
                    Folder = folder,
                    Name = type.Name,
                    Type = type,
                    KnowHowProtected = type.Prop("IsKnowHowProtected", false),
                    Consistent = type.Prop("IsConsistent", true),
                };
            }
        }
    }
}
