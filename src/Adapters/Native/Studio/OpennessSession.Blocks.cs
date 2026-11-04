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
            var file = new FileInfo(TiaOpenness.Shared.NativePathSelection.FullPath(Path.Combine(directory, SafeFileName(target.Name) + ".xml")));
            if (file.Exists) throw new IOException("Export file already exists: " + file.FullName);

            if (target.Block != null) PlcNative.Export(target.Block, file, ExportOptions.WithDefaults);
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

            var file = new FileInfo(TiaOpenness.Shared.NativePathSelection.FullPath(Path.Combine(directory, SafeFileName(target.Name) + extension)));
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

        private static string Explain(Exception ex, ExportTarget target)
        {
            // Native wording is diagnostic data, not permission to compile or remove protection.
            return Flatten(ex.Message);
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
            var file = new FileInfo(TiaOpenness.Shared.NativePathSelection.FullPath(path));
            var options = overwrite ? ImportOptions.Override : ImportOptions.None;

            // A UDT and a block both arrive as .xml; the root element decides which composition
            // accepts it, and TIA gives no way to ask beforehand.
            if (LooksLikeType(path))
            {
                var types = PlcNative.Types(PlcNative.TypeGroup(plc.Software)).Import(file, options);
                return string.Join(", ", types.Select(t => PlcNative.Name(t)));
            }

            var blocks = PlcNative.Import(PlcNative.Blocks(PlcNative.BlockGroup(plc.Software)), file, options);
            return string.Join(", ", blocks.Select(b => PlcNative.Name(b)));
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
            catch (Exception) /* swallow(parse-fallback): failed XML type sniffing uses the block import path, where an invalid guess surfaces as an import error */
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
            if (!overwrite)
                throw new InvalidOperationException("Generating blocks from source can replace existing blocks. Confirm overwrite before importing source " +
                "text.");
            // Own only this temporary source; never delete a pre-existing project source.
            var name = "TIA_Studio_" + Guid.NewGuid().ToString("N") + extension;
            var source = plc.Software.ExternalSourceGroup.ExternalSources.CreateFromFile(name, path);
            // F12: the temporary source is removed after a failed generation too; a cleanup failure is reported on its own.
            try { source.GenerateBlocksFromSource(); }
            catch (Exception generation)
            {
                string failed = DeleteTemporarySource(source);
                if (failed == null) throw;
                throw new InvalidOperationException(generation.Message + " The temporary external source '" + name + "' could not be removed: " + failed,
                        generation);
            }
            string cleanup = DeleteTemporarySource(source);
            if (cleanup != null)
                throw new InvalidOperationException("Blocks were generated from the source, but the temporary external source '" + name
                        + "' could not be removed: " + cleanup);
            return Path.GetFileNameWithoutExtension(path);
        }

        private static string DeleteTemporarySource(PlcExternalSource source)
        {
            try { source.Delete(); return null; }
            catch (Exception error) /* swallow(cleanup): the caller reports the leftover temporary source separately */ { return error.Message; }
        }

        // ---- tags ----------------------------------------------------------

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
