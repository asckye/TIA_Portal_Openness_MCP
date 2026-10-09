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
using Siemens.Engineering.FingerprintData;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW.Alarm.TextLists;
using Siemens.Engineering.SW.Blocks.Interface;

#if TIA_SHARED_ADAPTER_PATHS
using PlcNative = TiaMcp.Adapters.Native.Plc.PlcBlockPrimitives;
#else
using PlcNative = TiaMcpServer.Siemens.LocalPlcBlocks.PlcBlockPrimitives;
#endif

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class PlcBlocksService
    {
        private readonly IEngineeringSession _session;

#if TIA_SHARED_ADAPTER_PATHS
        private TiaMcp.Adapters.PlcServices.BlockSurface? _plcBlocks;
        private ProjectBase? PlcBlocksProject => (_plcBlocks ?? (_plcBlocks =
            TiaMcp.Adapters.PlcServices.Over(() => _session.CurrentProject!).PlcBlocks)).CurrentProject;
#else
        private ProjectBase? PlcBlocksProject => _session.CurrentProject;
#endif

        private bool IsProjectNull()
        {
            if (PlcBlocksProject == null) return true;
            _session.VerifyBinding("Project access");
            return false;
        }

        public PlcBlocksService(IEngineeringSession session) => _session = session;

        public IEnumerable<PlcBlock>? ExportBlocks(string softwarePath, string exportPath, string regexName = "", bool preservePath = false)
        {
            _session.Logger?.LogInformation("Exporting blocks...");

            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (ConnectPortal is attempted automatically.)");
            }

            var exportList = new List<PlcBlock>();
            var failures = new List<string>();
            
            PlcBlock[] list;

            try
            {
                list = (_session.GetBlocks(softwarePath, regexName) is { } got ? got.ToArray() : []);
            }
            catch (Exception ex)
            {
                _session.Logger?.LogError(ex, "Failed to retrieve block list for {SoftwarePath}", softwarePath);
                return exportList;
            }

            TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,
                SoftwareContainerLookup.PathOf(_session.GetSoftwareContainer(softwarePath)), true);
            TiaOpenness.Shared.NativeExportPolicy.RequireConsistent("blocks", list.Where(x => !PlcNative.IsConsistent(x)).Select(x => PlcNative.Name(x)), "groupPath");

            for (int k = 0; k < list.Count(); k++)
            {
                var block = list[k];

                _session.Logger?.LogDebug($"- Exporting block {k}/{list.Count()} : {PlcNative.Name(block)}");

                string path;
                if (preservePath)
                {
                    var groupPath = "";
                    if (PlcNative.Parent(block) is PlcBlockGroup parentGroup)
                    {
                        groupPath = _session.GetPlcBlockGroupPath(parentGroup);
                    }
                    path = Path.Combine(exportPath, groupPath.Replace('/', '\\'), $"{PlcNative.Name(block)}.xml");
                }
                else
                {
                    path = Path.Combine(exportPath, $"{PlcNative.Name(block)}.xml");
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
                        try { File.Delete(path); }
                        catch (Exception ioEx)
                        {
                            failures.Add($"{PlcNative.Name(block)}: cannot delete existing file ({ioEx.Message})");
                            _session.Logger?.LogError(ioEx, "Delete failed for {File}", path);

                            continue;
                        }
                    }

                    try
                    {
                        PlcNative.Export(block, new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(path)), ExportOptions.None);
                    }
                    catch (LicenseNotFoundException licEx)
                    {
                        failures.Add($"{PlcNative.Name(block)}: license not found ({licEx.Message})");
                        _session.Logger?.LogError(licEx, "License issue exporting {Block}", PlcNative.Name(block));

                        continue;
                    }
                    catch (EngineeringTargetInvocationException engEx)
                    {
                        failures.Add($"{PlcNative.Name(block)}: target invocation failed ({engEx.Message})");
                        _session.Logger?.LogError(engEx, "TargetInvocationException exporting {Block}", PlcNative.Name(block));

                        continue;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{PlcNative.Name(block)}: export failed ({ex.Message})");
                        _session.Logger?.LogError(ex, "Export failed for {Block}", PlcNative.Name(block));

                        continue;
                    }

                    exportList.Add(block);
                }
                catch (Exception ex)
                {
                    // Catch only truly unexpected wrapper-level errors
                    failures.Add($"{PlcNative.Name(block)}: unexpected exception ({ex.Message})");
                    _session.Logger?.LogError(ex, "Unexpected error at block {Block}", PlcNative.Name(block));
                    // continue with next block
                }
            }

            if (failures.Count > 0)
            {
                _session.Logger?.LogWarning($"ExportPlcBlocks completed with {failures.Count} failures out of {list.Count()}. First failure: {failures[0]}");
            }
            else
            {
                _session.Logger?.LogInformation($"ExportPlcBlocks completed successfully. Exported {exportList.Count} blocks.");
            }

            return exportList;
        }

        public (string TempDir, List<string> Paths)? ExportBlocksToTemp(string softwarePath, string regexName = "", bool preservePath = false)
        {
            var tempDir = Path.Combine(TiaOpenness.Shared.DataLocations.Current.TempDirectory, "TiaMcpServer_Export_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var list = ExportBlocks(softwarePath, tempDir, regexName, preservePath);
            if (list == null) return null;

            var paths = Directory.GetFiles(tempDir, "*.xml", SearchOption.AllDirectories).ToList();
            return (tempDir, paths);
        }




        #region delete blocks / tag tables / types

    /// <summary>
    /// Partial: 删除程序块 / PLC 变量表 / 用户数据类型（UDT）。
    ///
    /// 为什么删除单独成一族、而不是散在各自的 Portal.Blocks.cs / Portal.Software.cs 里：
    /// 删除是这个服务器里唯一**不可逆**的一类写操作，护栏（精确路径、dryRun、删后回读）
    /// 必须三个入口一模一样。放在一起，改一条规矩就三处一起改，不会漏。
    ///
    /// 三条共同规矩：
    /// 1) 路径必须精确 —— 叶子名含正则元字符一律拒绝。理由见 <see cref="ResolveSingleByName"/>：
    ///    定位是「先字面、再锚定正则」，模式仍可能命中到一个**同样合法但不是你要的**对象；
    ///    读错了顶多返回错东西，删错了工程就没了。所以删除口不吃模式，一个字符都不行。
    /// 2) dryRun=true 时**一行工程都不动**，只解析目标并把代价（引用它的是谁）摆出来。
    /// 3) dryRun=false 时删完必须重新取句柄回读；对象还在 = 失败，抛异常，绝不报成功。
    ///
    /// 为什么删变量表比删块更危险：块被删了，调用方编译立刻报错；
    /// 一张变量表被删，表里所有变量的**符号**同时消失，而 HMI 是按符号名绑定 PLC 变量的，
    /// PLC 侧编译不一定报错，故障要到画面上才暴露。所以这里的预览必须把
    /// 「这张表里有多少变量、都叫什么」摆给调用方，而不是只回一句「找到了，可以删」。
    /// </summary>
        /// <summary>
        /// 预览或删除**一个** PLC 程序块（FB / FC / OB / 全局 DB / 背景 DB 都是 PlcBlock）。
        /// dryRun=true（默认）只解析目标，不做任何改动。
        /// </summary>


        /// <summary>
        /// 预览或删除一张 PLC 变量表。<paramref name="tagTableName"/> 接受裸表名或
        /// GetPlcTagTables 返回的组限定名（"驱动/变频器变量表"），反斜杠也当分隔符。
        /// dryRun=true（默认）只解析并清点内容，不做任何改动。
        /// </summary>


        /// <summary>
        /// 预览或删除一个 PLC 用户数据类型（UDT）。dryRun=true（默认）只解析并查引用。
        /// </summary>




        /// <summary>
        /// 和 Portal.Software.cs 里的 FindTagTable 走同一棵树、同一套匹配规则，
        /// 但额外回传组限定路径 —— 删除要报「到底删的是哪一张」，裸表名在多组重名时不够用。
        /// </summary>


        /// <summary>清点表里的变量。列表封顶，免得一张几千行的表把响应撑爆。</summary>






        /// <summary>
        /// 试着从变量表对象自己取 CrossReferenceService。
        /// 已知事实（V21 真机实测）：Software / Device / DeviceItem 三层都回 "service not available"，
        /// 只有 Block 层给得出。变量表属于哪一层没有实测数据，
        /// 所以这里**试一次**，拿不到就如实回 null + 原因，绝不假装查过。
        /// </summary>


        private static bool TryInvokeVoidMethod(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, Type.EmptyTypes);
            if (method == null) return false;
            method.Invoke(target, Array.Empty<object>());
            return true;
        }

        #endregion

        private static object RequireEngineeringService(object owner, string typeName)
        {
            // Type lookup by name so a member absent from V20 compiles on both targets and degrades to NotSupported.
            var type = typeof(PlcBlockInterface).Assembly.GetType(typeName) ?? throw new NotSupportedException("Native service type unavailable in this Openness version: " + typeName);
            if (owner is not IEngineeringServiceProvider) throw new NotSupportedException("Selected object is not a service provider.");
            try { return typeof(IEngineeringServiceProvider).GetMethod("GetService")!.MakeGenericMethod(type).Invoke(owner, null) ?? throw new NotSupportedException("Service unavailable on selected object: " + typeName); }
            catch (System.Reflection.TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
        }

        public ResponseMessage ManagePlcDataBlockSnapshot(string softwarePath, string blockPath, string action, string filePath = "", bool confirmValueChange = false, bool dryRun = true)
            => _session.RunHmiStepTool("ManagePlcDataBlockSnapshot", meta => {
                bool writing = PlcBlockServicesLogic.ValidateSnapshotRequest(action, filePath, confirmValueChange, dryRun);
                bool exporting = action == "exportSnapshot";
                var file = exporting ? NativeFileOutput.Plan(filePath) : null;
                using var access = writing && !exporting ? _session.AcquireHmiEditAccess() : null;
                // Snapshot/load semantics need the PLC already online in TIA, so Offline is not demanded here; this tool never changes the online state.
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var db = _session.ExactMasterCopyPlcSource(softwarePath, blockPath, true) as DataBlock ?? throw new ArgumentException("blockPath must identify a data block.");
                var iface = PlcNative.Interface(db) ?? throw new NotSupportedException("Data block exposes no interface.");
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false; meta["blockPath"] = blockPath; meta["action"] = action;
                meta["onlineState"] = PlcNative.StateOrNull(_session.ResolvePlcService<OnlineProvider>(softwarePath, plc))?.ToString();
                meta["before"] = EngineeringScalarProperties.Read(db);
                const string valueServiceName = "Siemens.Engineering.SW.Blocks.Interface.ValueService";
                meta["valueServiceTypeAvailable"] = typeof(PlcBlockInterface).Assembly.GetType(valueServiceName) != null;
                object? valueService = null; InterfaceSnapshot? snapshot = null;
                if (!exporting && action != "read") valueService = RequireEngineeringService(iface, valueServiceName);
                else { try { valueService = RequireEngineeringService(iface, valueServiceName); } catch (NotSupportedException ex) { meta["valueServiceUnavailable"] = ex.Message; } }
                if (exporting || action == "read")
                {
                    var attempts = new JsonArray();
                    foreach (var (source, owner) in new (string, object?)[] { ("DataBlock.Interface", iface), ("DataBlock", db), ("ValueService", valueService) })
                    {
                        if (owner == null || snapshot != null) continue;
                        try { snapshot = PlcNative.SnapshotOrNull(owner as IEngineeringServiceProvider); if (snapshot != null) meta["snapshotServiceSource"] = source; }
                        catch (Exception ex) { attempts.Add(source + ": " + ex.GetBaseException().Message); }
                    }
                    meta["snapshotServiceAttempts"] = attempts; meta["snapshotServiceAvailable"] = snapshot != null;
                    if (exporting && snapshot == null) throw new NotSupportedException("InterfaceSnapshot service unavailable on this data block/version.");
                }
                string nativeMethod = action == "createSnapshot" ? "CreateSnapshot" : action == "loadSnapshotAsActualValues" ? "LoadSnapshotAsActualValues" : action == "loadStartValuesAsActualValues" ? "LoadStartValuesAsActualValues" : "";
                if (nativeMethod.Length > 0 && valueService!.GetType().GetMethod(nativeMethod, Type.EmptyTypes) == null) throw new NotSupportedException("ValueService." + nativeMethod + "() unavailable.");
                meta["semantics"] = "Siemens semantics: CreateSnapshot reads actual values from the CPU; load actions write values into the CPU's actual values. The PLC must already be online in TIA; this tool never goes online/offline.";
                if (action == "read") return "Data block snapshot services inspected; no changes.";
                if (exporting) meta["filePath"] = file!.FullName;
                if (!writing) return "Data block snapshot preview; no native call.";
                if (exporting)
                {
                    meta["mayHaveWrittenFiles"] = true;
                    PlcNative.Export(snapshot!, file!, ExportOptions.None);
                    meta["apiCallSuccess"] = true; meta["file"] = NativeFileOutput.Verify(file!); meta["dataComplete"] = false;
                    return "Snapshot values exported by TIA and hashed; content semantics not verified; no project change.";
                }
                meta["mayHaveChanged"] = true;
                EngineeringGroupOperations.Call(valueService!, nativeMethod, Type.EmptyTypes);
                meta["apiCallSuccess"] = true; meta["after"] = EngineeringScalarProperties.Read(db);
                meta["readbackVerified"] = false; meta["readbackScope"] = "Openness exposes no snapshot/actual value readback; only the native call return and block scalars are reported.";
                return "Native " + nativeMethod + " returned; values not independently verified; no save/compile/download.";
            });

        public ResponseMessage ReadPlcBlockFingerprints(string softwarePath, string targetIpAddress, string pgPcInterface = "", string password = "", int offset = 0, int limit = 100, bool dryRun = true)
            => _session.RunHmiStepTool("GetPlcBlockFingerprints", meta => {
                if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
                var plc = _session.ExactPlcForEngineering(softwarePath, false);
                var provider = _session.ResolvePlcService<FingerprintDataProvider>(softwarePath, plc) ?? throw new NotSupportedException("FingerprintDataProvider unavailable for this PLC/version.");
                var configuration = PlcNative.Configuration(provider) ?? throw new PortalException(PortalErrorCode.InvalidState, "No connection configuration; configure the CPU network interface first.");
                var candidates = new List<PlcBlockServicesLogic.RouteCandidate>();
                foreach (var mode in _session.EnumerateReflectedProperty(configuration, "Modes"))
                    foreach (var pcInterface in _session.EnumerateReflectedProperty(mode, "PcInterfaces"))
                    {
                        foreach (var target in _session.EnumerateReflectedProperty(pcInterface, "TargetInterfaces"))
                            foreach (var address in _session.EnumerateReflectedProperty(target, "Addresses"))
                                if (address is ConfigurationAddress native)
                                    candidates.Add(new PlcBlockServicesLogic.RouteCandidate { ModeName = _session.ReadReflectedString(mode, "Name"), PcInterfaceName = _session.ReadReflectedString(pcInterface, "Name"), TargetName = _session.ReadReflectedString(target, "Name"), Address = PlcNative.Address(native) ?? "", NativeAddress = native });
                        // The CPU's configured IP is listed under the PC interface's subnets / gateways (the target interface stays empty until the adapter sees the CPU)
                        foreach (var subnet in _session.EnumerateReflectedProperty(pcInterface, "Subnets"))
                        {
                            foreach (var address in _session.EnumerateReflectedProperty(subnet, "Addresses"))
                                if (address is ConfigurationAddress native)
                                    candidates.Add(new PlcBlockServicesLogic.RouteCandidate { ModeName = _session.ReadReflectedString(mode, "Name"), PcInterfaceName = _session.ReadReflectedString(pcInterface, "Name"), TargetName = "subnet " + _session.ReadReflectedString(subnet, "Name"), Address = PlcNative.Address(native) ?? "", NativeAddress = native });
                            foreach (var gateway in _session.EnumerateReflectedProperty(subnet, "Gateways"))
                                foreach (var address in _session.EnumerateReflectedProperty(gateway, "Addresses"))
                                    if (address is ConfigurationAddress native)
                                        candidates.Add(new PlcBlockServicesLogic.RouteCandidate { ModeName = _session.ReadReflectedString(mode, "Name"), PcInterfaceName = _session.ReadReflectedString(pcInterface, "Name"), TargetName = "gateway " + _session.ReadReflectedString(gateway, "Name"), Address = PlcNative.Address(native) ?? "", NativeAddress = native });
                        }
                    }
                meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false; meta["contactedPlc"] = false; meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                meta["candidateRoutes"] = new JsonArray(candidates.Take(100).Select(c => (JsonNode)JsonValue.Create(c.Describe())!).ToArray());
                var route = PlcBlockServicesLogic.SelectFingerprintRoute(candidates, targetIpAddress, pgPcInterface);
                meta["selectedRoute"] = route.Describe();
                if (dryRun) return "Fingerprint read preview: route resolved, PLC not contacted.";
                using var secure = string.IsNullOrEmpty(password) ? null : PlcBlockServicesLogic.ToSecureString(password);
                OnlineConfigurationDelegate handler = PlcNative.Handler(cfg =>
                {
                    if (secure != null && cfg is OnlinePasswordConfiguration pwd) PlcNative.Password(pwd, secure);
                    else if (cfg is TlsVerificationConfiguration tls)   // FW >= 2.9 CPUs ask for certificate trust before any online read
                    {
                        var before = PlcNative.Selection(tls).ToString();
                        if (EngineeringCredentialRules.TlsSelectionToApply(true, before) != null) PlcNative.Selection(tls, TlsVerificationConfigurationSelection.Trusted);
                        meta["tlsVerification"] = new JsonObject { ["plcName"] = PlcNative.PlcName(tls), ["verificationInfo"] = PlcNative.VerificationInfo(tls), ["selectionBefore"] = before, ["selectionAfter"] = PlcNative.Selection(tls).ToString() };
                    }
                });
                meta["contactedPlc"] = true;
                var result = PlcNative.Fingerprints(provider, (ConfigurationAddress)route.NativeAddress!, handler) ?? throw new InvalidOperationException("GetFingerprintData returned no result.");
                meta["apiCallSuccess"] = true;
                var items = EngineeringGroupOperations.Items(PlcNative.Items(result)).Cast<FingerprintDataItem>().ToArray();
                var window = PlcBlockServicesLogic.Paginate(meta, items.Length, offset, limit);
                meta["records"] = new JsonArray(items.Skip(window.Skip).Take(window.Take).Select(i => (JsonNode)new JsonObject { ["identifier"] = PlcNative.Identifier(i), ["value"] = PlcNative.Value(i) }).ToArray());
                return "Fingerprint data read from the PLC via the selected route; no project or PLC change.";
            });







        // Typed user-group row for every STEP 7 user group class (Name is writable, Delete exists on each).


        // ======================================================================
        // Block group operations: create (sub)groups + move a block into a group.
        // NOTE: TIA Openness has NO API to reparent an existing PlcBlock
        // (PlcBlock.Parent is read-only). So:
        //   - EnsurePlcBlockGroup: native, via PlcBlockUserGroupComposition.Create.
        //   - MoveBlockToGroup: export -> Delete -> import-into-group round-trip,
        //     so generated blocks can be organized into layer folders afterwards.
        // ======================================================================

        // Navigate the block-group tree by path, CREATING any missing user groups.
        // Returns the leaf group (or null if software not found). `created` lists the
        // group names that were newly created this call (for reporting / idempotency).


        // Move an existing block (found anywhere by exact name) into targetGroupPath.
        // Implemented as export -> Delete -> import-into-group because Openness cannot
        // reparent a block. Prefers SIMATIC SD documents (.s7dcl, keeps comments);
        // falls back to SimaticML XML for mixed-language/STL blocks. Returns a summary.

    }
}
