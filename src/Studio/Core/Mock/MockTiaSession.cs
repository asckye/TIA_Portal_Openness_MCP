using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TiaOpenness.Core.Inspection;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Models.Errors;
using TiaOpenness.Core.Abstractions;

namespace TiaOpenness.Core.Mock
{
    /// <summary>
    /// An in-memory stand-in for a real Openness session. It exists so the bridge, the CLI,
    /// the desktop UI can be built and exercised end to end on a machine
    /// with no TIA Portal &#8212; exports really write files, compiles really return diagnostics,
    /// and the inspection rules run against the same shapes as production.
    /// </summary>
    public sealed partial class MockTiaSession : ITiaSession
    {
        private readonly List<MockDevice> _devices = new List<MockDevice>();
        private bool _connected;
        private ProjectInfo _project;
        private bool _withUi;
        private string _version;
        private MockVersionControl _versionControl;

        public SessionMode Mode { get { return SessionMode.Mock; } }
        public bool IsConnected { get { return _connected; } }
        public bool HasProject { get { return _project != null; } }

        /// <summary>Present once a project is open; the mock always claims V21-level support.</summary>
        public IVersionControl VersionControl { get { return _versionControl; } }

        public SessionState Connect(bool withUserInterface, bool attachToRunning, string version, int? processId = null)
        {
            if (processId.HasValue) throw new InvalidOperationException("Mock sessions cannot attach to a TIA Portal process.");
            _connected = true;
            _withUi = withUserInterface;
            _version = string.IsNullOrWhiteSpace(version) ? "21.0" : version;
            return GetState();
        }

        public void Disconnect()
        {
            _project = null;
            _versionControl = null;
            _devices.Clear();
            _connected = false;
        }

        public SessionState GetState()
        {
            return new SessionState
            {
                Connected = _connected,
                Mode = SessionMode.Mock,
                OpennessVersion = _version,
                WithUserInterface = _withUi,
                OpenProject = _project,
            };
        }

        public ProjectInfo OpenProject(string path)
        {
            RequireConnected();

            var name = string.IsNullOrWhiteSpace(path)
                ? "MockProject"
                : Path.GetFileNameWithoutExtension(path);

            _project = new ProjectInfo
            {
                Name = name,
                Path = path,
                Author = "mock",
                Comment = "Synthetic project served by the mock session.",
                CreationTime = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero),
                LastModified = DateTimeOffset.UtcNow,
                IsModified = false,
            };

            BuildFixture();
            _versionControl = new MockVersionControl(this, path);
            return _project;
        }

        public ProjectInfo GetProjectInfo()
        {
            RequireProject();
            return _project;
        }

        public void SaveProject()
        {
            RequireProject();
            _project.IsModified = false;
            _project.LastModified = DateTimeOffset.UtcNow;
        }

        public void CloseProject()
        {
            _project = null;
            _versionControl = null;
            _devices.Clear();
        }

        public IReadOnlyList<DeviceInfo> ListDevices()
        {
            RequireProject();
            return _devices.Select(d => d.Info).ToList();
        }

        public IReadOnlyList<BlockInfo> ListBlocks(string deviceId, bool includeSystemBlocks)
        {
            var device = RequireDevice(deviceId);
            return device.Blocks
                .Where(b => includeSystemBlocks || !b.Name.StartsWith("_", StringComparison.Ordinal))
                .ToList();
        }

        public ExportResult ExportBlocks(string deviceId, IReadOnlyList<string> blockPaths, string outputDirectory,
            ExportFormat format, bool preserveFolders, ProgressCallback progress)
        {
            var device = RequireDevice(deviceId);
            var targets = ResolveTargets(device, blockPaths);

            Directory.CreateDirectory(outputDirectory);
            var result = new ExportResult { OutputDirectory = outputDirectory, Requested = targets.Count };

            for (var i = 0; i < targets.Count; i++)
            {
                var block = targets[i];
                if (progress != null) progress("export", i + 1, targets.Count, block.Path);

                var item = new ExportedItem { BlockPath = block.Path };
                try
                {
                    if (block.IsKnowHowProtected)
                    {
                        throw new InvalidOperationException(
                            "Block is know-how protected and cannot be exported. Remove the protection in TIA Portal first.");
                    }

                    var file = Path.Combine(
                        preserveFolders ? Path.Combine(outputDirectory, RelativeFolder(block.Path)) : outputDirectory,
                        block.Name + ExtensionFor(block, format));

                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file, RenderBlock(block, format), new UTF8Encoding(false));

                    item.FilePath = file;
                    item.Succeeded = true;
                    result.Succeeded++;
                }
                catch (Exception ex)
                {
                    item.Succeeded = false;
                    item.Error = ex.Message;
                    result.Failed++;
                }
                result.Items.Add(item);
            }

            return result;
        }

        public ExportResult ImportBlocks(string deviceId, IReadOnlyList<string> files, bool overwrite, ProgressCallback progress)
        {
            var device = RequireDevice(deviceId);
            var result = new ExportResult { OutputDirectory = null, Requested = files.Count };

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (progress != null) progress("import", i + 1, files.Count, file);

                var item = new ExportedItem { FilePath = file };
                try
                {
                    if (!File.Exists(file)) throw new FileNotFoundException("Import file not found.", file);

                    var name = Path.GetFileNameWithoutExtension(file);
                    var existing = device.Blocks.FirstOrDefault(b =>
                        string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

                    if (existing != null && !overwrite)
                    {
                        throw new InvalidOperationException(
                            "Block '" + name + "' already exists. Pass overwrite=true to replace it.");
                    }

                    // Overwriting replaces the block where it already lives. Moving it to the
                    // root instead would silently break every version-control mapping and
                    // cross-reference that names it by folder path.
                    var path = existing?.Path ?? name;
                    var kind = existing?.Kind ?? KindFromExtension(file);
                    if (existing != null) device.Blocks.Remove(existing);

                    device.Blocks.Add(new BlockInfo
                    {
                        Path = path,
                        Name = name,
                        Kind = kind,
                        Number = existing?.Number,
                        ProgrammingLanguage = "SCL",
                        IsConsistent = false,
                        ModifiedDate = DateTimeOffset.UtcNow,
                        HeaderAuthor = existing?.HeaderAuthor,
                        HeaderVersion = existing?.HeaderVersion,
                    });

                    item.BlockPath = path;
                    item.Succeeded = true;
                    result.Succeeded++;
                    _project.IsModified = true;
                }
                catch (Exception ex)
                {
                    item.Succeeded = false;
                    item.Error = ex.Message;
                    result.Failed++;
                }
                result.Items.Add(item);
            }

            return result;
        }

        public IReadOnlyList<TagTableInfo> ListTagTables(string deviceId)
        {
            var device = RequireDevice(deviceId);
            return device.TagTables
                .Select(t => new TagTableInfo
                {
                    Path = t.Key,
                    Name = t.Key,
                    TagCount = t.Value.Count,
                    IsDefault = t.Key == "Default tag table",
                })
                .ToList();
        }

        public IReadOnlyList<TagInfo> ListTags(string deviceId, string tableName)
        {
            var device = RequireDevice(deviceId);
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return device.TagTables.SelectMany(t => t.Value).ToList();
            }

            List<TagInfo> tags;
            if (!device.TagTables.TryGetValue(tableName, out tags))
            {
                throw new KeyNotFoundException("No tag table named '" + tableName + "' on device '" + deviceId + "'.");
            }
            return tags;
        }

        public CompileResult CompileDevice(string deviceId, bool softwareOnly)
        {
            var device = RequireDevice(deviceId);
            var messages = new List<CompileMessage>();

            foreach (var block in device.Blocks.Where(b => !b.IsConsistent))
            {
                messages.Add(new CompileMessage
                {
                    Severity = CompileSeverity.Warning,
                    Target = device.Info.Name + "/Program blocks/" + block.Path,
                    Description = "Block was changed since the last compile and has been rebuilt.",
                });
            }

            // One deliberate error so callers can exercise the failure path.
            var faulty = device.Blocks.FirstOrDefault(b => b.Name == "FC_Broken");
            if (faulty != null)
            {
                messages.Add(new CompileMessage
                {
                    Severity = CompileSeverity.Error,
                    Target = device.Info.Name + "/Program blocks/" + faulty.Path,
                    ErrorCode = "SCL-1234",
                    Description = "Operand 'MissingTag' is not defined.",
                });
            }

            foreach (var block in device.Blocks) block.IsConsistent = true;

            var errors = messages.Count(m => m.Severity == CompileSeverity.Error);
            var warnings = messages.Count(m => m.Severity == CompileSeverity.Warning);

            return new CompileResult
            {
                State = errors > 0 ? "Error" : warnings > 0 ? "Warning" : "Success",
                ErrorCount = errors,
                WarningCount = warnings,
                Duration = TimeSpan.FromMilliseconds(420),
                Messages = messages,
            };
        }

        public InspectionReport Inspect(string deviceId, InspectionOptions options)
        {
            var device = RequireDevice(deviceId);

            // The fixture models one block that nothing calls, so the dead-code rule has
            // something to find without needing a real call graph.
            var referenced = new HashSet<string>(
                device.Blocks.Select(b => b.Name).Where(n => !n.EndsWith("_Unused", StringComparison.Ordinal)),
                StringComparer.OrdinalIgnoreCase);

            return InspectionEngine.Run(device.Info.Id, device.Blocks, options, referenced);
        }

        // ---- version control ----------------------------------------------

        /// <summary>Devices as the mock version control sees them. Internal, not part of ITiaSession.</summary>
        internal IEnumerable<MockDevice> EnumerateDevices()
        {
            RequireProject();
            return _devices;
        }

        /// <summary>
        /// Records that a WorkspaceToProject synchronization replaced an object's content with the
        /// workspace file's. The project now genuinely holds that text, so version control must
        /// compare it as equal &#8212; storing the content here is what makes that true instead of
        /// leaving the object permanently out of sync with the file it was just restored from.
        ///
        /// The real API leaves the restored block inconsistent and the project unsaved, so the
        /// mock does too: a caller that forgets to compile and save sees the same consequence.
        /// </summary>
        internal void NoteRestoredFromWorkspace(string mappingKey)
        {
            var block = FindBlockByMappingKey(mappingKey);
            if (block != null) block.IsConsistent = false;
            if (_project != null) _project.IsModified = true;
        }

        private BlockInfo FindBlockByMappingKey(string mappingKey)
        {
            var parts = (mappingKey ?? string.Empty).Split(new[] { '|' }, 2);
            if (parts.Length != 2) return null;

            var device = _devices.FirstOrDefault(d =>
                string.Equals(d.Info.Id, parts[0], StringComparison.OrdinalIgnoreCase));

            return device?.Blocks.FirstOrDefault(b =>
                string.Equals(b.Path, parts[1], StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            Disconnect();
        }

        private static BlockInfo ProtectedBlock(string path, int number)
        {
            var b = Block(path, "FB", BlockKind.FB, number, "SCL", true, "vendor");
            b.IsKnowHowProtected = true;
            return b;
        }

        // ---- helpers -------------------------------------------------------

        private List<BlockInfo> ResolveTargets(MockDevice device, IReadOnlyList<string> blockPaths)
        {
            if (blockPaths == null || blockPaths.Count == 0) return device.Blocks.ToList();

            var wanted = new HashSet<string>(blockPaths, StringComparer.OrdinalIgnoreCase);
            var targets = device.Blocks
                .Where(b => wanted.Contains(b.Path) || wanted.Contains(b.Name))
                .ToList();

            var missing = blockPaths
                .Where(p => !device.Blocks.Any(b =>
                    string.Equals(b.Path, p, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(b.Name, p, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (missing.Count > 0)
            {
                throw new KeyNotFoundException("Unknown block(s): " + string.Join(", ", missing));
            }
            return targets;
        }

        private static string RelativeFolder(string blockPath)
        {
            var idx = blockPath.LastIndexOf('/');
            return idx < 0 ? string.Empty : blockPath.Substring(0, idx).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string ExtensionFor(BlockInfo block, ExportFormat format)
        {
            if (format == ExportFormat.SimaticMl) return ".xml";
            switch (block.Kind)
            {
                case BlockKind.UDT: return ".udt";
                case BlockKind.DB:
                case BlockKind.InstanceDB: return ".db";
                default: return ".scl";
            }
        }

        private static string RenderBlock(BlockInfo block, ExportFormat format)
        {
            if (format == ExportFormat.Source)
            {
                return "// mock export of " + block.Path + System.Environment.NewLine +
                       "FUNCTION \"" + block.Name + "\" : Void" + System.Environment.NewLine +
                       "BEGIN" + System.Environment.NewLine +
                       "    ; // body not modelled by the mock session" + System.Environment.NewLine +
                       "END_FUNCTION" + System.Environment.NewLine;
            }

            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + System.Environment.NewLine +
                   "<Document>" + System.Environment.NewLine +
                   "  <SW.Blocks." + block.Kind + " ID=\"0\">" + System.Environment.NewLine +
                   "    <AttributeList>" + System.Environment.NewLine +
                   "      <Name>" + block.Name + "</Name>" + System.Environment.NewLine +
                   "      <Number>" + (block.Number.HasValue
                       ? block.Number.Value.ToString(CultureInfo.InvariantCulture)
                       : "0") + "</Number>" + System.Environment.NewLine +
                   "      <ProgrammingLanguage>" + (block.ProgrammingLanguage ?? "DB") + "</ProgrammingLanguage>" + System.Environment.NewLine +
                   "    </AttributeList>" + System.Environment.NewLine +
                   "  </SW.Blocks." + block.Kind + ">" + System.Environment.NewLine +
                   "</Document>" + System.Environment.NewLine;
        }

        private static BlockKind KindFromExtension(string file)
        {
            var ext = (Path.GetExtension(file) ?? string.Empty).ToLowerInvariant();
            switch (ext)
            {
                case ".udt": return BlockKind.UDT;
                case ".db": return BlockKind.DB;
                case ".scl": return BlockKind.FC;
                default: return BlockKind.Unknown;
            }
        }

        private void RequireConnected()
        {
            if (!_connected) throw new SessionPreconditionException(SessionFailureReason.NotConnected, "Not connected. Call session.connect first.");
        }

        private void RequireProject()
        {
            RequireConnected();
            if (_project == null) throw new SessionPreconditionException(SessionFailureReason.NoProjectOpen, "No project is open. Call project.open first.");
        }

        private MockDevice RequireDevice(string deviceId)
        {
            RequireProject();
            var device = _devices.FirstOrDefault(d =>
                string.Equals(d.Info.Id, deviceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Info.Name, deviceId, StringComparison.OrdinalIgnoreCase));

            if (device == null)
            {
                throw new KeyNotFoundException("No device named '" + deviceId + "'. Known devices: " +
                    string.Join(", ", _devices.Select(d => d.Info.Id)));
            }
            return device;
        }

        internal sealed class MockDevice
        {
            public DeviceInfo Info;
            public readonly List<BlockInfo> Blocks = new List<BlockInfo>();
            public readonly Dictionary<string, List<TagInfo>> TagTables =
                new Dictionary<string, List<TagInfo>>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
