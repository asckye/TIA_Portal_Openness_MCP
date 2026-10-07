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
        #region blocks/types

        /// <summary>
        /// 按名字在一个组里定位**唯一一个**对象。三级次序，专治「返回错块还冠上你请求的名字」：
        /// 1) 先按字面精确同名（OrdinalIgnoreCase）—— 西门子块名里常带 '.'，而 '.' 在 _regexChars 里，
        ///    老代码见到 '.' 就直接当正则走，于是请求 "FB_Motor.V2" 会被没锚定的 IsMatch 匹配上
        ///    "X_FB_MotorAV2_Old"，FirstOrDefault 把排在前面的那个错块返回、调用方还以为拿到的是自己要的；
        /// 2) 没有同名、且名字确实含元字符时才当模式用，并且**锚定** ^(?:...)$ ——
        ///    保留模式能力，但杜绝部分命中；正则非法则返回 null（等同找不到，与老行为一致）；
        /// 3) 锚定后仍命中多个 → 宁可报歧义也不猜（团队在删除口早就拒绝含元字符的路径，
        ///    读/导出口一直没堵，这里补上）。
        /// </summary>
        private T? ResolveSingleByName<T>(IEnumerable<T> items, string name, Func<T, string> nameOf, string kind)
            where T : class
        {
            var exact = items.Where(i => nameOf(i).Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (exact.Count > 1) throw new PortalException(PortalErrorCode.InvalidParams,
                $"Ambiguous {kind} name '{name}': use its group-qualified path.");
            if (exact.Count == 1) return exact[0];

            if (name.IndexOfAny(_regexChars) < 0)
            {
                return null;
            }

            Regex regex;
            try
            {
                regex = new Regex($"^(?:{name})$", RegexOptions.IgnoreCase);
            }
            catch (Exception)
            {
 /* swallow(parse-fallback): Invalid regular expressions retain the existing not-found result. */                // Invalid regex, return null
                return null;
            }

            // 匹配与抛歧义都放在 try 之外：否则「歧义」这个异常会被上面捕获非法正则的 catch 吞成 null。
            var matches = items.Where(i => regex.IsMatch(nameOf(i))).Take(11).ToList();
            if (matches.Count == 0)
            {
                return null;
            }
            if (matches.Count > 1)
            {
                var candidates = matches.Take(10).Select(nameOf).ToList();
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"Ambiguous {kind} name '{name}': it matched {(matches.Count > 10 ? "more than 10" : matches.Count.ToString())} {kind}s. Use the exact path instead.",
                    candidates);
            }

            return matches[0];
        }

        public PlcBlock? GetBlock(string softwarePath, string blockPath)
        {
            _logger?.LogInformation($"Getting block by path: {blockPath}");

            if (IsProjectNull())
            {
                return null;
            }

            var root = GetBlockRootGroup(softwarePath);
            if (root == null) return null;
            return new PlcListingRead().Required(softwarePath + "/BlockGroup/" + blockPath,
                () => PlcBlockLookup.Find<PlcBlockGroup, PlcBlock>(root, blockPath,
                    g => g.Name, g => g.Groups, g => g.Blocks,
                    (items, name) => ResolveSingleByName(items, name, b => b.Name, "block")));
        }

        public PlcType? GetType(string softwarePath, string typePath)
        {
            _logger?.LogInformation($"Getting type by path: {typePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var typeGroup = plcSoftware?.TypeGroup;

                if (typeGroup != null)
                {
                    var path = typePath.Contains("/") ? typePath.Substring(0, typePath.LastIndexOf("/")) : string.Empty;
                    var regexName = typePath.Contains("/") ? typePath.Substring(typePath.LastIndexOf("/") + 1) : typePath;

                    var group = GetPlcTypeGroupByPath(softwarePath, path);
                    if (group != null)
                    {
                        return ResolveSingleByName(group.Types.Cast<PlcType>(), regexName, t => t.Name, "type");
                    }
                }
            }

            return null;
        }

        public string GetBlockPath(PlcBlock block)
        {
            if (block == null)
            {
                return string.Empty;
            }

            if (block.Parent is PlcBlockGroup parentGroup)
            {
                var groupPath = GetPlcBlockGroupPath(parentGroup);
                return string.IsNullOrEmpty(groupPath) ? block.Name : $"{groupPath}/{block.Name}";
            }

            return block.Name;
        }

        /// <summary>
        /// 取块清单。**没打开项目时返回 null，不是空列表** —— 这两件事对调用方完全不同：
        /// 空列表意味着「这个 PLC 里确实没有块」，null 意味着「根本没查成」。
        /// 以前返回 `[]`，于是工具层那句 `if (list != null)` 恒为真、`else throw` 永不执行，
        /// 离线调用得到「成功，0 个块」—— 调用方据此认定这个 PLC 是空的，继续往下走。
        /// </summary>
        public List<PlcBlock>? GetBlocks(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting blocks...");

            if (IsProjectNull())
            {
                return null;
            }

            var list = new List<PlcBlock>();

            // 路径解析不到 ≠ 这个 PLC 里没有块。原来两件事都返回空列表，于是把 softwarePath
            // 写错也得到「成功，0 个块」—— 调用方（尤其是模型）会据此认为 PLC 是空的，
            // 转头去建一堆已经存在的块。真机实测过这条：传 'PLC_NOT_EXIST_XYZ' 得到 success=true。
            var plcSoftware = ResolvePlcForListing(softwarePath);
            var read = new PlcListingRead();
            var group = read.Required(softwarePath + "/BlockGroup", () => plcSoftware.BlockGroup);
            if (group == null)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"ListPlcBlocks: PLC '{softwarePath}' resolved, but its BlockGroup is not available — "
                    + "the block list could NOT be read. This is not the same as 'the PLC has no blocks'.");
            }

            try
            {
                GetBlocksRecursive(group, list, regexName);
            }
            catch (PortalException)
            {
                // 参数类错误（比如 regexName 不是合法正则）要原样上抛：包成「遍历失败」
                // 会给出一个**不准确**的原因，调用方照着去查 Openness 就跑偏了。
                throw;
            }
            catch (Exception ex)
            {
                // 遍历炸在半路时原来只记日志、把**残缺的**列表当完整结果返回。
                // 「少了几个块」比「一个都没有」更难发现，因为它看起来完全正常。
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"ListPlcBlocks: block enumeration failed after {list.Count} block(s) in '{softwarePath}'; "
                    + "the returned list would have been INCOMPLETE, so it is not returned at all. "
                    + $"Root cause: {ex.Message}", null, ex);
            }

            return list;
        }

        public PlcBlockGroup? GetBlockRootGroup(string softwarePath)
        {
            _logger?.LogInformation("Getting block root group...");

            if (IsProjectNull())
            {
                return null;
            }

            var plcSoftware = ResolvePlcForListing(softwarePath);
            return new PlcListingRead().Required(softwarePath + "/BlockGroup", () => plcSoftware.BlockGroup)
                ?? throw new PortalException(PortalErrorCode.OpennessError,
                    $"PLC '{softwarePath}' resolved, but its BlockGroup is unavailable; hierarchy not read.");
        }

        /// <summary>同 GetBlocks：没打开项目时返回 null，别把「没查成」伪装成「确实没有」。</summary>
        public List<PlcType>? GetTypes(string softwarePath, string regexName = "")
        {
            _logger?.LogInformation("Getting types...");

            if (IsProjectNull())
            {
                return null;
            }

            var list = new List<PlcType>();

            // 与 GetBlocks 同因：路径解析不到 ≠ 这个 PLC 没有 UDT。
            var plcSoftware = ResolvePlcForListing(softwarePath);
            var group = new PlcListingRead().Required(softwarePath + "/TypeGroup", () => plcSoftware.TypeGroup);
            if (group == null)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"ListPlcTypes: PLC '{softwarePath}' resolved, but its TypeGroup is not available — "
                    + "the type list could NOT be read. This is not the same as 'the PLC has no types'.");
            }

            try
            {
                GetTypesRecursive(group, list, regexName);
            }
            catch (PortalException)
            {
                throw;   // 同上：参数类错误不许被包成「遍历失败」
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"ListPlcTypes: type enumeration failed after {list.Count} type(s) in '{softwarePath}'; "
                    + "the returned list would have been INCOMPLETE, so it is not returned at all. "
                    + $"Root cause: {ex.Message}", null, ex);
            }

            return list;
        }

        // The file actually written by the last ExportBlock / ExportType (exportPath is a directory unless it ends in .xml).
        public string? LastExportedFile { get; private set; }

        private static string ResolveExportFile(string exportPath, string name, string groupPath, bool preservePath)
        {
            if (!preservePath && exportPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(exportPath))
                return exportPath;
            return preservePath ? Path.Combine(exportPath, groupPath.Replace('/', '\\'), name + ".xml") : Path.Combine(exportPath, name + ".xml");
        }

        public PlcBlock? ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting block by path: {blockPath}");
            LastExportedFile = null;

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");
                }

                var block = Guard.RequireNotNull(GetBlock(softwarePath, blockPath), "Block", blockPath);

                var blockGroupPath = block.Parent is PlcBlockGroup parentGroup ? GetPlcBlockGroupPath(parentGroup) : "";
                exportPath = ResolveExportFile(exportPath, block.Name, blockGroupPath, preservePath);

                // TIA Portal never exports inconsistent blocks
                TiaOpenness.Shared.NativeExportPolicy.RequireConsistent("blocks", block.IsConsistent ? Array.Empty<string>() : new[] { blockPath }, "blockPath");

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                block.Export(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath)), ExportOptions.None);
                LastExportedFile = exportPath;

                return block;
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                //If the exception is already a PortalException, use it; otherwise, wrap it in a new PortalException
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                pex.Data["softwarePath"] = softwarePath;
                pex.Data["blockPath"] = blockPath;
                pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportPlcBlock failed for {SoftwarePath} {BlockPath} -> {ExportPath}", softwarePath, blockPath, exportPath);
                throw pex;
            }
        }

        public PlcType? ExportType(string softwarePath, string typePath, string exportPath, bool preservePath = false)
        {
            _logger?.LogInformation($"Exporting type by path: {typePath}");
            LastExportedFile = null;

            try
            {
                if (IsProjectNull())
                {
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");
                }

                var type = Guard.RequireNotNull(GetType(softwarePath, typePath), "Type", typePath);

                // TIA Portal never exports inconsistent types
                TiaOpenness.Shared.NativeExportPolicy.RequireConsistent("types", type.IsConsistent ? Array.Empty<string>() : new[] { typePath }, "typePath");

                var typeGroupPath = type.Parent is PlcTypeGroup parentTypeGroup ? GetPlcTypeGroupPath(parentTypeGroup) : "";
                exportPath = ResolveExportFile(exportPath, type.Name, typeGroupPath, preservePath);

                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }

                type.Export(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(exportPath)), ExportOptions.None);
                LastExportedFile = exportPath;

                return type;
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ExportFailed, "Export failed", null, ex);

                if (!pex.Data.Contains("softwarePath")) pex.Data["softwarePath"] = softwarePath;
                if (!pex.Data.Contains("typePath")) pex.Data["typePath"] = typePath;
                if (!pex.Data.Contains("exportPath")) pex.Data["exportPath"] = exportPath;

                _logger?.LogError(pex, "ExportPlcType failed for {SoftwarePath} {TypePath} -> {ExportPath}", softwarePath, typePath, exportPath);
                throw pex;
            }
        }

        // Prepare a block/type XML file for Openness import. Two things are fixed on a temp
        // copy (the user's original file is never touched):
        //   1) Engineering version: Openness rejects an XML whose <Engineering version="Vxx"/>
        //      is newer than the connected portal ("The engineering version 'V21' ... is not
        //      supported."). The XML builders historically hardcode V21, so on a V20 portal
        //      every import fails. The header is rewritten to the detected major version.
        //   2) Encoding/BOM: block/type XML carrying Chinese comments must be UTF-8 *with BOM*
        //      or TIA imports the text as mojibake (中文乱码). Callers (and the model that wrote
        //      the file) frequently emit BOM-less UTF-8, so we always re-emit with a BOM here.
        private static string PrepareXmlForImport(string path)
        {
            try
            {
                var bytes = TiaOpenness.Shared.NativeInputPolicy.Read("importPath", () => File.ReadAllBytes(path));
                bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var text = TiaOpenness.Shared.NativeInputPolicy.Read("importPath", () => File.ReadAllText(path, Encoding.UTF8));

                var fixedText = text;
                int major = Engineering.TiaMajorVersion;
                if (major > 0)
                {
                    fixedText = Regex.Replace(text,
                        "<Engineering\\s+version=\"V\\d+\"\\s*/>",
                        $"<Engineering version=\"V{major}\" />");
                }

                // Already correct: version matches (or unknown) AND a BOM is present -> import as-is.
                if (fixedText == text && hasBom) return path;

                var tmp = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "tia_mcp_import_" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(tmp, fixedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return tmp;
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch
            {
 /* swallow(parse-fallback): If XML preparation fails, preserve the original path so native import reports the input error. */                return path; // best effort; on any failure import the original file
            }
        }

        public bool ImportBlock(string softwarePath, string groupPath, string importPath)
        {
            _logger?.LogInformation($"Importing block from path: {importPath}");

            try
            {
                if (IsProjectNull())
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                    throw new PortalException(PortalErrorCode.NotFound,
                        softwareContainer?.Software == null
                            ? $"Software container not found for path '{softwarePath}'"
                            : $"Software at '{softwarePath}' is not PlcSoftware (type={softwareContainer.Software.GetType().Name})");

                var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                if (group == null)
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"PLC block group not found for groupPath='{groupPath}'; use empty string for root program blocks");

                if (!new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath)).Exists)
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                var fileInfo = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(PrepareXmlForImport(importPath)));

                var imported = group.Blocks.Import(fileInfo, ImportOptions.Override);
                if (imported == null || imported.Count == 0)
                    throw new PortalException(PortalErrorCode.ImportFailed, "Blocks.Import returned an empty collection");

                return true;
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                // Surface the real Openness error to callers — without this the message
                // is just "Import failed" which is useless for diagnosing bad LAD/SCL XML.
                var inner = UnwrapImportError(ex);
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ImportFailed, $"Import failed: {inner}", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportPlcBlock failed for {SoftwarePath} group={GroupPath} file={ImportPath}: {Inner}", softwarePath, groupPath, importPath, inner);
                throw pex;
            }
        }

        // Walk InnerException chain and concatenate type+message — Openness wraps the
        // useful XML-validation error several layers deep.
        private static string UnwrapImportError(Exception ex)
        {
            var parts = new List<string>();
            var cur = ex;
            int depth = 0;
            while (cur != null && depth < 6)
            {
                parts.Add($"{cur.GetType().Name}: {cur.Message}");
                cur = cur.InnerException;
                depth++;
            }
            return string.Join(" | ", parts);
        }

        public ResponseImportBatch ImportBlocksFromDirectory(string softwarePath, string groupPath, string dir, string regexName = "", bool overwrite = true)
        {
            if (!IsProjectNull()) TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,
                TiaMcpServer.Siemens.SoftwareContainerLookup.PathOf(ResolvePlc(softwarePath, PlcAccess.Read)), true);

            var imported = new List<string>();
            var failed = new List<ImportFailure>();

            try
            {
                if (IsProjectNull())
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Project is null" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    failed.Add(new ImportFailure { Path = dir, Error = "Directory not found" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware)
                {
                    failed.Add(new ImportFailure { Path = dir, Error = $"PlcSoftware not found at '{softwarePath}'" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                var group = GetPlcBlockGroupByPath(softwarePath, groupPath);
                if (group == null)
                {
                    failed.Add(new ImportFailure { Path = dir, Error = $"Block group not found (groupPath='{groupPath}')" });
                    return new ResponseImportBatch { Imported = imported, Failed = failed };
                }

                Regex? regex = null;
                if (!string.IsNullOrWhiteSpace(regexName))
                {
                    regex = new Regex(regexName, RegexOptions.IgnoreCase);
                }

                foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (regex != null && !regex.IsMatch(name))
                    {
                        continue;
                    }

                    try
                    {
                        if (!new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(file)).Exists)
                        {
                            failed.Add(new ImportFailure { Path = file, Error = "File not found" });
                            continue;
                        }
                        var fi = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(PrepareXmlForImport(file)));

                        // Let Openness check the XML object's identity atomically. A filename
                        // lookup cannot enforce overwrite=false (and may miss renamed files).
                        var list = group.Blocks.Import(fi, overwrite ? ImportOptions.Override : ImportOptions.None);
                        if (list != null && list.Count > 0)
                        {
                            imported.AddRange(list.Select(b => b?.Name).Where(n => !string.IsNullOrWhiteSpace(n))!.Cast<string>());
                        }
                        else
                        {
                            imported.Add(name);
                        }
                    }
                    catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
                    {
                        failed.Add(new ImportFailure { Path = file, Error = ex.ToString() });
                    }
                }

                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                failed.Add(new ImportFailure { Path = dir, Error = ex.ToString() });
                return new ResponseImportBatch { Imported = imported, Failed = failed };
            }
        }

        public bool ImportType(string softwarePath, string groupPath, string importPath)
        {
            _logger?.LogInformation($"Importing type from path: {importPath}");

            try
            {
                if (IsProjectNull())
                    throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");

                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is not PlcSoftware plcSoftware)
                    throw new PortalException(PortalErrorCode.NotFound,
                        softwareContainer?.Software == null
                            ? $"Software container not found for path '{softwarePath}'"
                            : $"Software at '{softwarePath}' is not PlcSoftware (type={softwareContainer.Software.GetType().Name})");

                var group = GetPlcTypeGroupByPath(softwarePath, groupPath);
                if (group == null)
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"PLC type group not found for groupPath='{groupPath}'; use empty string for root PLC data types");

                if (!new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(importPath)).Exists)
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Import file not found: {importPath}");
                var fileInfo = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(PrepareXmlForImport(importPath)));

                var imported = group.Types.Import(fileInfo, ImportOptions.Override);
                if (imported == null || imported.Count == 0)
                    throw new PortalException(PortalErrorCode.ImportFailed, "Types.Import returned an empty collection");

                return true;
            }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException) { throw; }
            catch (Exception ex)
            {
                var pex = ex as PortalException ?? new PortalException(PortalErrorCode.ImportFailed, "Import failed", null, ex);
                pex.Data["softwarePath"] = softwarePath;
                pex.Data["groupPath"] = groupPath;
                pex.Data["importPath"] = importPath;
                _logger?.LogError(pex, "ImportPlcType failed for {SoftwarePath} group={GroupPath} file={ImportPath}", softwarePath, groupPath, importPath);
                throw pex;
            }
        }

        public (string TempDir, List<string> Paths)? ExportBlockToTemp(string softwarePath, string blockPath, bool preservePath = false)
        {
            var tempDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var blk = ExportBlock(softwarePath, blockPath, tempDir, preservePath);
            if (blk == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }




        #endregion
    }
}
