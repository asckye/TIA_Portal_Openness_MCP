using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Reflection;
using System.Threading;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Native.Plc;
using TiaMcpServer.Siemens;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Online;
#if ONLINE_LEGITIMATION
using Siemens.Engineering.Online.Configurations;
#endif
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
#if ONLINE_DOWNLOAD
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Upload;
using Siemens.Engineering.Upload.Configurations;
#endif
namespace TiaMcp.Adapters.Native.Online
{
    // Historical native observations lack a recorded PLCSIM version/date; see docs/reference/real-machine-ledger.md.
    internal sealed partial class OnlineDownloadAdapter
    {
        public OnlineCompareResult CompareSoftwareToOnline(string softwarePath, int maxDepth = 4, int maxEntries = 200)
        {
            // 缺少工程或 PLC 时没有比对结果，不能解释为在线离线一致。
            if (_session.IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "CompareSoftwareToOnline: no project is open, so NO comparison was performed. "
                    + "Do not read this as 'offline and online are identical'. "
                    + "Call ConnectPortal + OpenProject (or AttachOpenProject) first.");
            }

            var plcSoftware = _session.GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"CompareSoftwareToOnline: PLC software not found at '{softwarePath}', "
                    + "so NO comparison was performed. Do not read this as 'identical'."
                    + _session.AvailablePlcPathsSuffix());
            }

            string probeState = "Unknown";
            try
            {
                // Note: don't gate on OnlineProvider here. The provider service is sometimes
                // unavailable on PlcSoftware even when the PLC is online via the TIA Portal UI
                // (depends on hardware variant / project layout). Let CompareToOnline itself
                // throw if the connection isn't actually live — the exception path below
                // captures that with the original TIA error message.
                var provider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                probeState = provider?.State.ToString() ?? "Unknown";

                // Typed PlcSoftware.CompareToOnline (all eight releases); CompareResultState is read by name because
                // CompareIrrelevant and the folder own-state values exist from V15.1 only.
                var result = plcSoftware.CompareToOnline();
                if (result == null)
                    return new OnlineCompareResult { Message = "CompareToOnline returned null.", IsOnline = true };

                var entries = new List<OnlineCompareEntry>();
                var summary = new Dictionary<string, int>();
                bool truncated = WalkCompareTree(result.RootElement, "", 0, maxDepth, maxEntries, entries, summary);

                return new OnlineCompareResult
                {
                    Message = $"Compare complete: {entries.Count} differences" + (truncated ? " (truncated)." : "."),
                    IsOnline = true,
                    Entries = entries.ToArray(),
                    Summary = summary,
                    Truncated = truncated
                };
            }
            // 比对失败必须抛出原生错误，不能返回空差异或声称在线。
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (probeState == "Offline")
                    return new OnlineCompareResult { IsOnline = false, Message = $"'{softwarePath}' is offline. Call ConnectOnlinePlc first." };
                _session.Log.Error(ex, "CompareSoftwareToOnline failed for {SoftwarePath}", softwarePath);
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"CompareSoftwareToOnline failed for '{softwarePath}': {ex.Message} "
                    + "NO comparison result is available — do not read this as 'offline and online are identical'. "
                    + "Go online first (ConnectOnlinePlc) and retry.", null, ex);
            }
        }

        private static bool WalkCompareTree(global::Siemens.Engineering.Compare.CompareResultElement? element, string path, int depth, int maxDepth,
                int maxEntries,
            List<OnlineCompareEntry> entries, Dictionary<string, int> summary)
        {
            if (element == null) return false;
            if (entries.Count >= maxEntries) return true;

            string leftName = element.LeftName ?? string.Empty;
            string rightName = element.RightName ?? string.Empty;
            string status = element.ComparisonResult.ToString();
            string? details = element.DetailedInformation;

            string displayName = !string.IsNullOrEmpty(leftName) ? leftName : rightName;
            string fullPath = string.IsNullOrEmpty(path)
                ? displayName
                : (string.IsNullOrEmpty(displayName) ? path : path + "/" + displayName);

            if (summary.ContainsKey(status)) summary[status]++;
            else summary[status] = 1;

            // Only differences are reported.
            // 实测出现过的真差异状态："RightMissing"（只在离线侧，没下载过）、
            // "FolderContentsDifferent"；同族还有 "LeftMissing" / "ObjectsDifferent"。
            // 以 "Identical" 结尾的状态和 CompareIrrelevant 是**一致**，漏掉会把一致报成
            // 「Compare complete: N differences」，诱发一次不必要的下载。
            bool isDifference = OnlineTransitionPolicy.IsCompareDifference(status);
            if (depth > 0 && isDifference)
            {
                entries.Add(new OnlineCompareEntry
                {
                    Path = fullPath,
                    LeftName = leftName,
                    RightName = rightName,
                    Status = status,
                    Details = string.IsNullOrEmpty(details) ? null : details
                });
                if (entries.Count >= maxEntries) return true;
            }

            if (depth >= maxDepth) return false;

            if (element.Elements == null) return false;
            foreach (global::Siemens.Engineering.Compare.CompareResultElement child in element.Elements)
            {
                if (WalkCompareTree(child, fullPath, depth + 1, maxDepth, maxEntries, entries, summary))
                    return true;
            }
            return false;
        }

    }
}
