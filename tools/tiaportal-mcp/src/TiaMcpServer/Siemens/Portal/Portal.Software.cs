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
    // Partial: software. 2.8.0 split by family into Portal.Software.<Family>.cs; behavior unchanged.
    public partial class Portal
    {
        #region software

        public PlcSoftware? GetPlcSoftware(string softwarePath)
        {
            _logger?.LogInformation($"Getting software by path: {softwarePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);

            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                return plcSoftware;
            }

            // Low-barrier fallback: tolerate a sloppy softwarePath (wrong case / extra spaces /
            // a single-PLC project / a unique substring like "PLC" -> "PLC_1"). Exact resolution
            // above is tried first, so this only runs when it misses.
            return ResolvePlcSoftwareFuzzy(softwarePath);
        }

        private PlcSoftware? ResolvePlcSoftwareFuzzy(string softwarePath)
        {
            var all = GetAllPlcSoftware();
            if (all.Count == 0) return null;

            var matched = Guard.MatchPlcName(all.Select(p => p.Name).ToList(), softwarePath);
            if (matched == null) return null;

            return all.FirstOrDefault(p => string.Equals(p.Name, matched, StringComparison.Ordinal))
                ?? all.FirstOrDefault(p => string.Equals(p.Name, matched, StringComparison.OrdinalIgnoreCase));
        }

        // Enumerate every PlcSoftware in the open project (devices + device groups), de-duplicated by
        // name. Used for tolerant softwarePath resolution and "Available PLC paths" error hints.
        public List<PlcSoftware> GetAllPlcSoftware()
        {
            var result = new List<PlcSoftware>();
            if (_project == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Collect(IEnumerable<DeviceItem>? roots)
            {
                if (roots == null) return;
                var stack = new Stack<DeviceItem>(roots.Where(x => x != null));
                while (stack.Count > 0)
                {
                    var it = stack.Pop();
                    if (it == null) continue;
                    try
                    {
                        if (it.GetService<SoftwareContainer>()?.Software is PlcSoftware plc &&
                            !string.IsNullOrEmpty(plc.Name) && seen.Add(plc.Name))
                        {
                            result.Add(plc);
                        }
                    }
                    catch { }
                    try { if (it.DeviceItems != null) foreach (var ch in it.DeviceItems) if (ch != null) stack.Push(ch); } catch { }
                }
            }

            void WalkDevices(DeviceComposition? devices)
            {
                if (devices == null) return;
                foreach (var d in devices) { try { Collect(d.DeviceItems); } catch { } }
            }
            void WalkGroups(DeviceUserGroupComposition? groups)
            {
                if (groups == null) return;
                foreach (var g in groups) { try { WalkDevices(g.Devices); WalkGroups(g.Groups); } catch { } }
            }

            try { WalkDevices(_project.Devices); } catch { }
            try { WalkGroups(_project.DeviceGroups); } catch { }
            return result;
        }

        // " Available PLC paths: a, b, c" suffix for not-found error messages (empty when none).
        public string AvailablePlcPathsSuffix()
        {
            try
            {
                var names = GetAllPlcSoftware().Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
                var paths = names.Count > 0 ? " Available PLC paths: " + string.Join(", ", names) : string.Empty;
                // 附上设备树遍历中被吞掉的真因：路径其实是对的、只是遍历半途抛了的那种情况，
                // 光报 "Available PLC paths" 会把用户引去改一个本来就没错的参数。
                // 遍历正常时 DeviceScanErrorSuffix() 返回空串，消息与以前逐字节相同。
                return paths + DeviceScanErrorSuffix();
            }
            catch { return DeviceScanErrorSuffix(); }
        }

        public CompilerResult CompileSoftware(string softwarePath, string password = "")
        {
            _logger?.LogInformation($"Compiling software by path: {softwarePath}");

            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState, "Project is null");

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software == null)
                throw new PortalException(PortalErrorCode.NotFound, $"SoftwareContainer or Software not found for path '{softwarePath}'");

            if (!string.IsNullOrEmpty(password))
            {
                var deviceItem = softwareContainer?.Parent as DeviceItem;

                var admin = deviceItem?.GetService<SafetyAdministration>();
                if (admin != null)
                {
                    if (!admin.IsLoggedOnToSafetyOfflineProgram)
                    {
                        SecureString secString = new NetworkCredential("", password).SecurePassword;
                        try
                        {
                            admin.LoginToSafetyOfflineProgram(secString);
                        }
                        catch (Exception ex)
                        {
                            throw new PortalException(PortalErrorCode.OpennessError, $"Safety login failed: {ex.Message}", null, ex);
                        }
                    }
                }
            }

            // PlcSoftware and classic WinCC (HmiTarget) are themselves service providers, so the
            // compiler service comes off the software object. WinCC Unified's HmiSoftware is NOT
            // an IEngineeringServiceProvider (verified against the V21 PublicAPI) and exposes no
            // Compile of its own — for Unified the compilable object is the owning device item,
            // which is what the TIA UI compiles as well.
            ICompilable compileService = ResolveCompileService(softwareContainer, softwarePath, out var targetKind);
            // Siemens.Engineering.Compiler.CompileProvider is documented but internal in the PublicAPI; ICompilable is the public entry.

            try
            {
                CompilerResult result = compileService.Compile();

                if (result == null)
                    throw new PortalException(PortalErrorCode.OpennessError, "ICompilable.Compile() returned null");
                try
                {
                    foreach (CompilerResultMessage top in EngineeringGroupOperations.Items(result.Messages).Cast<CompilerResultMessage>().Take(20))
                        _logger?.LogInformation("Compile {Path}: {State} {Description} ({Errors} errors / {Warnings} warnings, {Time}, {Nested} nested)", top.Path, top.State, top.Description, top.ErrorCount, top.WarningCount, top.DateTime, EngineeringGroupOperations.Items(top.Messages).Count());
                }
                catch { }

                return result;
            }
            catch (PortalException)
            {
                throw;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw new PortalException(PortalErrorCode.OpennessError, $"{tie.InnerException.GetType().FullName}: {tie.InnerException.Message}", null, tie.InnerException);
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError, $"{ex.GetType().FullName}: {ex.Message}", null, ex);
            }
        }

        /// <summary>
        /// Find the object that actually carries the ICompilable service for a software path,
        /// and return that service.
        ///
        /// PlcSoftware and classic WinCC (HmiTarget) carry it themselves. WinCC Unified's
        /// HmiSoftware does not — for Unified the compilable object sits further up the
        /// ownership chain (the HMI device), which is what the TIA UI compiles too.
        ///
        /// The judgement must be "does this object actually hand out ICompilable", NOT
        /// "is this object an IEngineeringServiceProvider" — in Openness practically
        /// everything implements that interface, while GetService&lt;T&gt;() just returns
        /// **null** when the service is absent instead of throwing. Testing the interface
        /// therefore picks the first ancestor unconditionally and then fails with a null
        /// service. Real-machine 2026-08-31, MTP700 Unified Basic on V21: the software's
        /// immediate parent DeviceItem passed the interface test and yielded a null
        /// service, so Unified compiles failed with
        /// "HmiSoftware via DeviceItemImpl.GetService&lt;ICompilable&gt;() returned null"
        /// — i.e. exactly the panel type this tool was added for.
        ///
        /// So: walk up from the software and take the first level that really provides
        /// the service. Walking is also depth-proof — Unified PC stations nest
        /// Device → DeviceItem → DeviceItem, and that nesting is not ours to predict.
        /// </summary>
        private ICompilable ResolveCompileService(SoftwareContainer? softwareContainer, string softwarePath, out string targetKind)
        {
            var software = softwareContainer?.Software;
            if (software == null)
                throw new PortalException(PortalErrorCode.NotFound, $"SoftwareContainer or Software not found for path '{softwarePath}'");

            // 走过的每一层都记下来：找不到时把这串报出去，下一个人不用再猜层级。
            var probed = new List<string>();

            ICompilable? Probe(object? candidate, string kind)
            {
                if (candidate is not IEngineeringServiceProvider provider) return null;
                ICompilable? service;
                try
                {
                    service = provider.GetService<ICompilable>();
                }
                catch (Exception ex)
                {
                    // 代理对象可能已失效；这一层探不了不代表上一层探不了，记下继续往上。
                    probed.Add($"{kind}(threw {ex.GetType().Name})");
                    return null;
                }
                probed.Add($"{kind}{(service == null ? "(no ICompilable)" : "(OK)")}");
                return service;
            }

            var direct = Probe(software, software.GetType().Name);
            if (direct != null)
            {
                targetKind = software.GetType().Name;
                return direct;
            }

            // 从软件容器往上爬。上限 8 层纯属防御：真实层级是 3~4 层，
            // 加个上限只是不想在代理对象出怪时把自己转死在循环里。
            object? node = softwareContainer;
            for (int depth = 0; node != null && depth < 8; depth++)
            {
                var kind = $"{software.GetType().Name} via {node.GetType().Name}";
                var service = Probe(node, kind);
                if (service != null)
                {
                    targetKind = kind;
                    return service;
                }
                node = (node as IEngineeringObject)?.Parent;
            }

            throw new PortalException(
                PortalErrorCode.InvalidState,
                $"Software at '{softwarePath}' ({software.GetType().FullName}) is not compilable: " +
                $"neither it nor any owner up to 8 levels provides ICompilable. Probed: {string.Join(" -> ", probed)}");
        }

        #endregion
    }
}
