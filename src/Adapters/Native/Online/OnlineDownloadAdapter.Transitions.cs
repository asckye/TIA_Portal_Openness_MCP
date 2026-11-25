using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
#if ONLINE_DOWNLOAD
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
#endif
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcpServer.Siemens;

namespace TiaMcp.Adapters.Native.Online
{
    internal sealed partial class OnlineDownloadAdapter
    {
#if ONLINE_DOWNLOAD
        public OnlineDownloadResult DownloadToPlc(OnlineRequest request)
        {
            OnlineTransitionPolicy.RequireAddress(request.TargetIpAddress, "targetIpAddress");
            string softwarePath = request.SoftwarePath;
            string? password = EmptyToNull(request.Password), pgPcInterface = EmptyToNull(request.PgPcInterface), targetIpAddress =
                EmptyToNull(request.TargetIpAddress);
            bool trustDeviceCertificate = request.TrustDeviceCertificate;
            var legitimation = new HmiObject { ["trustDeviceCertificate"] = trustDeviceCertificate, ["writeAttempted"] = false };   // TLS prompt record
            _session.Log.Information(
                "DownloadPlc: softwarePath={SoftwarePath} consistentOnly={C} keepDB={K} start={S} stop={T} hasPassword={P} pgPc={I} targetIp={A} " +
                    "userMgmt={U} scope={O}",
                softwarePath, request.ConsistentBlocksOnly, request.KeepActualValues, request.StartAfterDownload, request.StopBeforeDownload, !string
                    .IsNullOrEmpty(password), pgPcInterface, targetIpAddress, request.UserManagementMode, request.DownloadScope);
            string rhTarget;
            try { rhTarget = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ValidateRhTarget(request.RhTarget); }
            catch (ArgumentException ex)
            {
                return new OnlineDownloadResult { Ok = false, Meta = legitimation, Message = ex.Message, Errors = new[] { ex.Message } };
            }
            if (!OnlineCapabilities.DownloadScopes.Contains(request.DownloadScope))
            {
                const string scopeError = "downloadScope must be software or softwareOnlyChanges.";
                return new OnlineDownloadResult { Ok = false, Meta = legitimation, Message = scopeError, Errors = new[] { scopeError } };
            }

            if (_session.IsProjectNull())
                return new OnlineDownloadResult { Ok = false, Message = "No project open.", Meta = new HmiObject { ["v4Rejection"] = "PROJECT_NOT_BOUND" } };

            DownloadPromptPolicy promptPolicy;
            try
            {
                promptPolicy = BuildDownloadPromptPolicy(request);
            }
            catch (ArgumentException ex)
            {
                return new OnlineDownloadResult { Ok = false, Meta = legitimation,
                    Message = "Invalid download prompt parameters: " + ex.Message, Errors = new[] { ex.Message } };
            }

            // The exact PLC without the standard-target precondition: R/H systems download through RHDownloadProvider, and the
            // download's own offline requirement below names the documented reason.
            var plcSoftware = _session.ResolvePlc(softwarePath, false);
            if (plcSoftware == null)
                return new OnlineDownloadResult { Ok = false, Meta = legitimation,
                    Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() };
            RequireDownloadOffline(softwarePath, plcSoftware, rhTarget);
            bool modified = _session.CurrentProject!.IsModified;
            bool? consistent = null;
            try
            {
                var consistency = _session.ReadPlcConsistency(softwarePath);
                consistent = consistency.Any(x => x.Consistent == false) ? false
                    : consistency.Count > 0 && consistency.All(x => x.Consistent == true) ? true : (bool?)null;
            }
            catch (Exception ex) when (_session.RecoverableAuditError(ex))
            {
                legitimation["consistencyReadWarning"] = "Compile consistency could not be read.";
            }
            if (OnlineTransitionPolicy.DownloadProjectRefusal(modified, consistent) is { } projectRefusal)
                throw new AdapterPreconditionException(projectRefusal.Message, projectRefusal.Condition, null);
            legitimation["consistency"] = consistent == true ? "consistent" : "unknown";

            // Declared outside the try so the catch can report which PG/PC route was used.
            DownloadRouteSelection? routeDiagnostics = null;

            try
            {
#if ONLINE_RH
                // R/H systems download to one CPU at a time through RHDownloadProvider; its own Configuration enumerates the routes
                // and raises the legitimation prompts (targets are bound to the provider instance that enumerated them).
                RHDownloadProvider? rh = rhTarget.Length == 0 ? null : _session.ResolvePlcService<RHDownloadProvider>(softwarePath, plcSoftware)
                    ?? throw new PortalException(PortalErrorCode.NotFound,
                            "RHDownloadProvider service not available on this PLC (rhTarget applies to R/H systems only).");
#endif
                DownloadProvider? downloadProvider = rhTarget.Length == 0 ? _session.ResolvePlcService<DownloadProvider>(softwarePath, plcSoftware) : null;
                if (rhTarget.Length == 0 && downloadProvider == null)
                    return new OnlineDownloadResult
                    {
                        Ok = false, Meta = legitimation,
                        Message = "DownloadProvider service not available for this PLC. Ensure hardware configuration has network settings."
                    };

#if ONLINE_RH
                ConnectionConfiguration? configuration = rh != null ? rh.Configuration : downloadProvider!.Configuration;
#else
                ConnectionConfiguration? configuration = downloadProvider!.Configuration;
#endif
                if (configuration == null)
                    return new OnlineDownloadResult
                    {
                        Ok = false, Meta = legitimation,
                        Message = "No connection configuration found. Configure the PLC's PROFINET/IP address in hardware configuration first."
                    };

                using var legitimationScope =
                    AttachOnlineLegitimationHandler(configuration, password, legitimation, trustDeviceCertificate, request.AllowNonTlsLogin);

                DownloadConfigurationDelegate preDelegate = (config) => ApplyDownloadPrompt(config, promptPolicy);
                DownloadConfigurationDelegate postDelegate = (config) => ApplyDownloadPrompt(config, promptPolicy);

                // ConnectionConfiguration does NOT implement IConfiguration; a ConfigurationTargetInterface (Modes -> PcInterfaces ->
                // TargetInterfaces) or a ConfigurationAddress does. Select one (applying its route) and pass THAT to Download().
                routeDiagnostics = SelectDownloadRoute(configuration, pgPcInterface, targetIpAddress);
                if (routeDiagnostics.WriteAttempted) legitimation["mayHaveChanged"] = true;
                if (routeDiagnostics.Error != null && routeDiagnostics.Unreadable) legitimation["writeOutcomeKnown"] = false;
                if (routeDiagnostics.Error != null)
                    return new OnlineDownloadResult
                    {
                        Ok = false, Meta = legitimation,
                        Message = routeDiagnostics.Error,
                        Errors = new[] { routeDiagnostics.Error }
                    };
                if (routeDiagnostics.Configuration is not IConfiguration downloadConfig)
                {
                    const string noRoute =
                        "No download route: the connection configuration lists no Modes -> PcInterfaces -> TargetInterfaces route (IConfiguration) to " +
                        "download through. "
                        + "Check the PG/PC interface and the CPU's network settings; CheckDownloadReadiness lists the routes.";
                    return new OnlineDownloadResult { Ok = false, Meta = legitimation, Message = noRoute, Errors = new[] { noRoute } };
                }
                legitimation["routeApplied"] = routeDiagnostics.Applied;
                var options = DownloadScopeOption(request.DownloadScope);
                legitimation["downloadOptions"] = options.ToString();
                _session.Log.Information("DownloadPlc: PG/PC route = {Route}", routeDiagnostics.Description);

#if ONLINE_RH
                if (rh != null)
                {
                    legitimation["writeAttempted"] = true;
                    DownloadResult rhResult;
#if ONLINE_TARGET_ADDRESSES
                    if (routeDiagnostics.Address != null && routeDiagnostics.AddressSource == "created" && routeDiagnostics
                            .Target is IConfiguration rhTargetConfiguration)
                        rhResult = rhTarget == "primary"
                            ? rh.DownloadToPrimary(rhTargetConfiguration, routeDiagnostics.Address, preDelegate, postDelegate, options)
                            : rh.DownloadToBackup(rhTargetConfiguration, routeDiagnostics.Address, preDelegate, postDelegate, options);
                    else
#endif
                    rhResult = rhTarget == "primary"
                        ? rh.DownloadToPrimary(downloadConfig, preDelegate, postDelegate, options)
                        : rh.DownloadToBackup(downloadConfig, preDelegate, postDelegate, options);
                    var rhResponse = BuildDownloadResponse(rhResult, softwarePath, routeDiagnostics, promptPolicy, legitimation);
                    rhResponse.Message = "[R/H " + rhTarget + "] " + rhResponse.Message;
                    return rhResponse;
                }
#endif

#if ONLINE_TARGET_ADDRESSES
                if (routeDiagnostics.Address != null && routeDiagnostics.AddressSource == "created"
                    && routeDiagnostics.Target is IConfiguration targetConfiguration)
                {
                    // "Download Hardware and Software to a target with specific IP-Address" - the official overload for an
                    // address TIA has not seen on the target yet (PLCSIM Advanced instance before its first download, new CPU).
                    legitimation["writeAttempted"] = true;
                    DownloadResult createdResult = downloadProvider!
                        .Download(targetConfiguration, routeDiagnostics.Address, preDelegate, postDelegate, options);
                    return BuildDownloadResponse(createdResult, softwarePath, routeDiagnostics, promptPolicy, legitimation);
                }
#endif

                // Typed Download(IConfiguration, pre, post, DownloadOptions), present on every release with DownloadProvider (V15.1+).
                legitimation["writeAttempted"] = true;
                DownloadResult result = downloadProvider!.Download(downloadConfig, preDelegate, postDelegate, options);
                return BuildDownloadResponse(result, softwarePath, routeDiagnostics, promptPolicy, legitimation);
            }
            catch (Exception ex)
            {
                // A native failure can arrive wrapped (reflection or the delegate boundary). Unwrap it so the caller sees the
                // actual reason (connection/route error, not-reachable CPU, a prompt refused by the policy, etc.).
                var real = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException : ex;
                _session.Log.Error(real, "DownloadPlc failed for {SoftwarePath}", softwarePath);

                // A connection failure is usually the wrong PG/PC adapter on a multi-NIC PC, so
                // always show which route was used and what else was available (issue #14).
                var routeHint = routeDiagnostics == null
                    ? string.Empty
                    : $" Route used: {routeDiagnostics.Description}."
                      + (routeDiagnostics.Candidates.Count > 1
                          ? $" Available routes: {DescribeRoutes(routeDiagnostics.Candidates)}."
                            + " Pass pgPcInterface / targetIpAddress to DownloadPlc to pick one explicitly."
                          : string.Empty);

                var failureMeta = promptPolicy.Summary();
                foreach (var kv in legitimation) failureMeta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
                failureMeta["success"] = false;
                if (!NativeRefusalProbe.AllowsRefusalProof(ex) || routeDiagnostics?.WriteAttempted == true || legitimation["writeAttempted"] is true)
                    failureMeta["writeOutcomeKnown"] = false;
                return new OnlineDownloadResult
                {
                    Ok = false,
                    Message =
                        $"Download failed: {real.Message}{routeHint}{promptPolicy.RefusedSummary()}{promptPolicy.ReinitializationSummary()}{promptPolicy.UnansweredSummary()}",
                    Errors = new[] { real.Message },
                    Meta = failureMeta
                };
            }
        }

#endif
        public HmiMessage GoOffline(string softwarePath)
        {
            // 未找到工程、PLC 或在线服务时，不能报告下线成功。
            if (_session.IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    "DisconnectOnlinePlc: no project is open, so nothing was taken offline. "
                    + "Call ConnectPortal + OpenProject (or AttachOpenProject) first.");
            }

            // GoOffline needs no configuration (Siemens online-and-download skill) and acts on an online PLC by definition, so the
            // offline-export precondition of a write resolution must not run here.
            var plcSoftware = _session.ResolvePlc(softwarePath, false);
            if (plcSoftware == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DisconnectOnlinePlc: PLC software not found at '{softwarePath}', so nothing was taken offline."
                    + _session.AvailablePlcPathsSuffix());
            }

            bool issued = false;
            try
            {
                var provider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plcSoftware);
                if (provider == null)
                {
                    return new HmiMessage { Meta = HmiMeta.Basic(false, ("writeAttempted", false)), Message =
                        $"DisconnectOnlinePlc: OnlineProvider service is not available on '{softwarePath}', "
                        + "so the offline transition was NOT performed. Any live online session is still open — "
                        + "disconnect it in the TIA Portal UI before compiling or exporting." };
                }

                var before = provider.State.ToString();
                issued = true;
                try { provider.GoOffline(); }
                catch (Exception error)
                {
                    string? observed = null;
                    bool unchanged = before is "Online" or "Offline" && NativeRefusalProbe.ProvesUnchanged(error, () =>
                        (observed = provider.State.ToString()) == before);
                    _session.Log.Error(error, "GoOffline failed for {SoftwarePath}", softwarePath);
                    return new HmiMessage { Message = $"GoOffline error: {error.Message}",
                        Meta = HmiMeta.Basic(false, ("before", before), ("after", observed), ("writeAttempted", true),
                            ("writeOutcomeKnown", unchanged), ("mayHaveChanged", !unchanged)) };
                }
                // GoOffline returns void: the state read back decides the result, as DisconnectOnlinePlcs does.
                var after = provider.State.ToString();
                var outcome = OnlineTransitionPolicy.GoOfflineOutcome(softwarePath, before, after);
                bool settled = outcome.Success || before == "Online" && after == before;
                return new HmiMessage { Message = outcome.Message, Meta = HmiMeta.Basic(outcome.Success, ("before", before), ("after", after),
                    ("writeAttempted", true), ("writeOutcomeKnown", settled), ("mayHaveChanged", !settled || before != after)) };
            }
            catch (Exception ex)
            {
                _session.Log.Error(ex, "GoOffline failed for {SoftwarePath}", softwarePath);
                return new HmiMessage { Message = $"GoOffline error: {ex.Message}",
                    Meta = HmiMeta.Basic(false, ("writeAttempted", issued), ("writeOutcomeKnown", false), ("mayHaveChanged", issued)) };
            }
        }

    }
}
