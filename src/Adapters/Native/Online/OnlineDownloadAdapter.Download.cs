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
        #region download

#if ONLINE_DOWNLOAD
        public OnlineDownloadResult DownloadToPlc(OnlineRequest request)
        {
            string softwarePath = request.SoftwarePath;
            string? password = EmptyToNull(request.Password), pgPcInterface = EmptyToNull(request.PgPcInterface), targetIpAddress =
                EmptyToNull(request.TargetIpAddress);
            bool trustDeviceCertificate = request.TrustDeviceCertificate;
            var legitimation = new HmiObject { ["trustDeviceCertificate"] = trustDeviceCertificate };   // TLS prompt record
            _session.Log.Information(
                "DownloadPlc: softwarePath={SoftwarePath} consistentOnly={C} keepDB={K} start={S} stop={T} hasPassword={P} pgPc={I} targetIp={A} " +
                    "userMgmt={U} scope={O}",
                softwarePath, request.ConsistentBlocksOnly, request.KeepActualValues, request.StartAfterDownload, request.StopBeforeDownload, !string
                    .IsNullOrEmpty(password), pgPcInterface, targetIpAddress, request.UserManagementMode, request.DownloadScope);
            string rhTarget;
            try { rhTarget = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ValidateRhTarget(request.RhTarget); }
            catch (ArgumentException ex) { return new OnlineDownloadResult { Ok = false, Message = ex.Message, Errors = new[] { ex.Message } }; }
            if (!OnlineCapabilities.DownloadScopes.Contains(request.DownloadScope))
            {
                const string scopeError = "downloadScope must be software or softwareOnlyChanges.";
                return new OnlineDownloadResult { Ok = false, Message = scopeError, Errors = new[] { scopeError } };
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
                return new OnlineDownloadResult { Ok = false, Message = "Invalid download prompt parameters: " + ex.Message, Errors = new[] { ex.Message } };
            }

            // The exact PLC without the standard-target precondition: R/H systems download through RHDownloadProvider, and the
            // download's own offline requirement below names the documented reason.
            var plcSoftware = _session.ResolvePlc(softwarePath, false);
            if (plcSoftware == null)
                return new OnlineDownloadResult { Ok = false, Message = $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() };
            RequireDownloadOffline(softwarePath, plcSoftware, rhTarget);

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
                        Ok = false,
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
                        Ok = false,
                        Message = "No connection configuration found. Configure the PLC's PROFINET/IP address in hardware configuration first."
                    };

                using var legitimationScope =
                    AttachOnlineLegitimationHandler(configuration, password, legitimation, trustDeviceCertificate, request.AllowNonTlsLogin);

                DownloadConfigurationDelegate preDelegate = (config) => ApplyDownloadPrompt(config, promptPolicy);
                DownloadConfigurationDelegate postDelegate = (config) => ApplyDownloadPrompt(config, promptPolicy);

                // ConnectionConfiguration does NOT implement IConfiguration; a ConfigurationTargetInterface (Modes -> PcInterfaces ->
                // TargetInterfaces) or a ConfigurationAddress does. Select one (applying its route) and pass THAT to Download().
                routeDiagnostics = SelectDownloadRoute(configuration, pgPcInterface, targetIpAddress);
                if (routeDiagnostics.Error != null)
                    return new OnlineDownloadResult
                    {
                        Ok = false,
                        Message = routeDiagnostics.Error,
                        Errors = new[] { routeDiagnostics.Error }
                    };
                if (routeDiagnostics.Configuration is not IConfiguration downloadConfig)
                {
                    const string noRoute =
                        "No download route: the connection configuration lists no Modes -> PcInterfaces -> TargetInterfaces route (IConfiguration) to " +
                        "download through. "
                        + "Check the PG/PC interface and the CPU's network settings; CheckDownloadReadiness lists the routes.";
                    return new OnlineDownloadResult { Ok = false, Message = noRoute, Errors = new[] { noRoute } };
                }
                legitimation["routeApplied"] = routeDiagnostics.Applied;
                var options = DownloadScopeOption(request.DownloadScope);
                legitimation["downloadOptions"] = options.ToString();
                _session.Log.Information("DownloadPlc: PG/PC route = {Route}", routeDiagnostics.Description);

#if ONLINE_RH
                if (rh != null)
                {
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
                    DownloadResult createdResult = downloadProvider!
                        .Download(targetConfiguration, routeDiagnostics.Address, preDelegate, postDelegate, options);
                    return BuildDownloadResponse(createdResult, softwarePath, routeDiagnostics, promptPolicy, legitimation);
                }
#endif

                // Typed Download(IConfiguration, pre, post, DownloadOptions), present on every release with DownloadProvider (V15.1+).
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

        // DownloadPlc applies its route with ConnectionConfiguration.ApplyConfiguration, which throws while the device is online, and
        // DownloadProvider connects by itself: the download starts from Offline (R/H: both CPUs through RHOnlineProvider).
        private void RequireDownloadOffline(string softwarePath, PlcSoftware plc, string rhTarget)
        {
            var states = new List<string?>();
#if ONLINE_RH
            if (rhTarget.Length > 0)
            {
                var rh = _session.ResolvePlcService<RHOnlineProvider>(softwarePath, plc);
                if (rh != null) { states.Add(rh.PrimaryState.ToString()); states.Add(rh.BackupState.ToString()); }
            }
            else
#endif
            {
                var provider = _session.ResolvePlcService<OnlineProvider>(softwarePath, plc);
                if (provider != null) states.Add(provider.State.ToString());
            }
            var refusal = OnlineTransitionPolicy.DownloadStateRefusal(softwarePath, states);
            if (refusal != null) throw new AdapterPreconditionException(refusal, "offline-state", false);
        }

        // downloadScope software -> DownloadOptions.Software ("Download software all"); softwareOnlyChanges ->
        // DownloadOptions.SoftwareOnlyChanges ("Download software only changes"), a V17+ member resolved by name.
        private static DownloadOptions DownloadScopeOption(string scope)
            => scope == "softwareOnlyChanges"
                ? (DownloadOptions)Enum.Parse(typeof(DownloadOptions), "SoftwareOnlyChanges")
                : DownloadOptions.Software;

        private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
#endif

#if ONLINE_DOWNLOAD
        private DownloadPromptPolicy BuildDownloadPromptPolicy(OnlineRequest request)
            => DownloadPromptPolicy.ForRequest(request, _session.PromptAnswers(request.PromptAnswersJson));
#endif

        // 每个提示对象按其真实形态应答：枚举型 CurrentSelection、布尔型 Checked 或 SetPassword(SecureString)。
        // 决策来自 DownloadPromptPolicy；无法应答的提示连同 TIA 的提示文本一起记录，永远不记录密码。
#if ONLINE_DOWNLOAD
        private void ApplyDownloadPrompt(object config, DownloadPromptPolicy policy)
        {
            var type = config.GetType();
            var typeName = type.Name;
            string? message = null;
            // the official base classes carry the prompt text; the 43 concrete V21 prompt classes derive from them.
            try { message = config switch { DownloadConfiguration download => download.Message, UploadConfiguration upload => upload.Message, _ => type.GetProperty("Message")?.GetValue(config) as string }; } catch /* swallow(probe-optional): prompt text is diagnostic; a missing message must not prevent answering the prompt */ { }
            _session.Log.Debug("ApplyDownloadPrompt: {TypeName}", typeName);
            try
            {
                var selection = type.GetProperty("CurrentSelection");
                var selectionValues = selection != null && selection.PropertyType.IsEnum && selection.CanWrite
                    ? Enum.GetNames(selection.PropertyType) : Array.Empty<string>();
                var checkedProp = type.GetProperty("Checked");
                bool hasChecked = config is DownloadCheckConfiguration || checkedProp != null && checkedProp.CanWrite && checkedProp
                    .PropertyType == typeof(bool);
                bool hasPassword = config is DownloadPasswordConfiguration || config is UploadPasswordConfiguration || type
                    .GetMethod("SetPassword", new[] { typeof(SecureString) }) != null;

                var answer = policy.Decide(typeName, selectionValues, hasChecked, hasPassword);
                switch (answer.Kind)
                {
                    case DownloadPromptPolicy.AnswerKind.Selection:
                        #if ONLINE_SELECTIVE_DELETE
                        if (config is SelectiveDeleteDownload selectiveDelete) selectiveDelete.CurrentSelection = (SelectiveDeleteDataSelections)Enum
                            .Parse(typeof(SelectiveDeleteDataSelections), answer.Value!, ignoreCase: true);
                        else
#endif
                        selection!.SetValue(config, Enum.Parse(selection.PropertyType, answer.Value!, ignoreCase: true));
                        break;
                    case DownloadPromptPolicy.AnswerKind.Checked:
                        // the Startdrive prompts are typed (V21 Siemens.Engineering.Startdrive.dll): the two upload checks derive from
                        // UploadConfiguration (not UploadCheckConfiguration; V21 only), the three download checks from DownloadCheckConfiguration.
#if ONLINE_PARAMETERS
                        if (config is OverrideTelegramMismatch telegramMismatch) telegramMismatch.Checked = answer.Value == "true";
                        else if (config is OverwriteOfflineConfiguration overwriteOffline) overwriteOffline.Checked = answer.Value == "true";
                        else
#endif
                        if (config is StartDriveDownloadCheckConfiguration startdriveCheck) startdriveCheck.Checked = answer.Value == "true";
#if ONLINE_DOWNLOAD_SENSITIVE
                        else if (config is AcceptDownloadOfUnencryptedSensitiveData unencrypted) unencrypted.Checked = answer.Value == "true";
                        else if (config is ReplaceDownloadedData replaceDownloaded) replaceDownloaded.Checked = answer.Value == "true";

#endif
                        else if (config is DownloadCheckConfiguration check) check.Checked = answer.Value == "true";
                        else checkedProp!.SetValue(config, answer.Value == "true");
                        break;
                    case DownloadPromptPolicy.AnswerKind.Password:
                    {
                        var secret = typeName switch
                        {
                            "BlockBindingPassword" => policy.BlockBindingPassword,
                            "PlcMasterSecretPassword" => policy.MasterSecretPassword,
                            _ => policy.ModuleAccessPassword
                        };
                        // The official pattern checks IsSecureCommunication before entering a password (V17+ on these prompts).
                        bool? secureChannel = null;
#if ONLINE_TLS
                        if (config is DownloadPasswordConfiguration securedDownload) secureChannel = securedDownload.IsSecureCommunication;
                        else if (config is UploadPasswordConfiguration securedUpload) secureChannel = securedUpload.IsSecureCommunication;
#endif
                        var nonTlsRefusal = DownloadPromptPolicy.NonTlsLoginRefusal(secureChannel, policy.AllowNonTlsLogin);
                        if (nonTlsRefusal != null)
                        {
                            answer = new DownloadPromptPolicy.Answer {
                                Kind = DownloadPromptPolicy.AnswerKind.Unanswered, Source = "unanswered", Note = nonTlsRefusal };
                            break;
                        }
                        using var secure = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ToSecureString(secret!);
                        if (secureChannel != null) answer.Note = "secureCommunication=" + secureChannel.Value;
                        if (config is DownloadPasswordConfiguration downloadPassword) downloadPassword.SetPassword(secure);
                        else if (config is UploadPasswordConfiguration uploadPassword) uploadPassword.SetPassword(secure);
                        else type.GetMethod("SetPassword", new[] { typeof(SecureString) })!.Invoke(config, new object[] { secure });
                        break;
                    }
                    case DownloadPromptPolicy.AnswerKind.Refused:
                        // Record first: the download stops at this prompt even when its documented "no" selection cannot be set.
                        policy.Record(typeName, message, answer);
                        if (answer.Value != null)
                        {
                            try { selection!.SetValue(config, Enum.Parse(selection.PropertyType, answer.Value, ignoreCase: true)); }
                            catch (Exception ex) {
                                _session.Log.Warning(ex, "Refused download prompt {TypeName}: the {Value} selection could not be set", typeName, answer.Value);
                            }
                        }
                        throw new DownloadPromptPolicy.PromptRefusedException("Download stopped at the refused prompt " + typeName
                            + (string.IsNullOrEmpty(message) ? "" : " ('" + message + "')") + "; pass " + answer.AllowWith + "=true to allow it.");
                }
                policy.Record(typeName, message, answer);
            }
            catch (Exception ex) when (ex is not DownloadPromptPolicy.PromptRefusedException)
            {
                var real = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                _session.Log.Warning(real, "Download prompt {TypeName} could not be answered", typeName);
                policy
                    .Record(typeName, message, new DownloadPromptPolicy.Answer {
                        Kind = DownloadPromptPolicy.AnswerKind.Unanswered, Source = "unanswered", Note = "answer failed: " + real.Message });
            }
        }
#endif

#if ONLINE_DOWNLOAD
        public OnlineReadinessResult CheckDownloadReadiness(string softwarePath)
        {
            var issues = new List<string>();

            if (_session.IsProjectNull())
                return new OnlineReadinessResult {
                    Ready = false, Issues = new[] { "No project open." }, Meta = new HmiObject { ["v4Rejection"] = "PROJECT_NOT_BOUND" } };

            var plcSoftware = _session.GetPlcSoftware(softwarePath);
            if (plcSoftware == null)
                return new OnlineReadinessResult {
                    Ready = false, Issues = new[] { $"PLC software not found: '{softwarePath}'." + _session.AvailablePlcPathsSuffix() } };

            bool hasProvider = false;
            bool hasConfig = false;
            bool? isConsistent = null;
            var routes = new List<DownloadRoute>();

            try
            {
                var provider = _session.ResolvePlcService<DownloadProvider>(softwarePath, plcSoftware);
                hasProvider = provider != null;
                if (!hasProvider)
                    issues.Add("DownloadProvider service not available. Check hardware/network configuration.");
                else
                {
                    hasConfig = provider!.Configuration != null;
                    if (!hasConfig)
                        issues.Add("No network configuration for this PLC. Set the IP address in hardware configuration.");
                    else
                        // Read-only: enumerate the PG/PC routes WITHOUT applying any of them, so a
                        // multi-NIC PC can be diagnosed before touching the CPU (issue #14).
                        routes = EnumerateDownloadRoutes(provider.Configuration);
                }
            }
            catch (Exception ex)
            {
                issues.Add($"Error accessing DownloadProvider: {ex.Message}");
            }

            try
            {
                var consistency = _session.ReadPlcConsistency(softwarePath);
                isConsistent = consistency.Any(x => x.Consistent == false) ? false : consistency.Count > 0 && consistency.All(x => x.Consistent == true) ? true
                    : (bool?)null;
                if (isConsistent == false) issues.Add("One or more root/unit blocks or types are inconsistent; compilation must be reviewed.");
                if (isConsistent == null) issues.Add("Compile consistency is unknown (no complete block/type evidence).");
            }
            catch (Exception ex) when (_session.RecoverableAuditError(ex)) { issues.Add("Consistency read failed: " + ex.GetBaseException().Message); }

            ScoreDownloadRoutes(routes, null);
            var routesJson = new HmiArray();
            foreach (var route in routes.OrderByDescending(r => r.Score))
                routesJson.Add(new HmiObject
                {
                    ["mode"] = route.ModeName,
                    ["pgPcInterface"] = route.PcInterfaceName,
                    ["pgPcInterfaceNumber"] = route.PcInterfaceNumber,
                    ["pgPcAddresses"] = string.Join(", ", route.PcAddresses),
                    ["targetInterface"] = route.TargetName,
                    ["targetAddresses"] = string.Join(", ", route.TargetAddresses),
                    ["preferred"] = route.Score > 0
                });

            bool? ready = !hasProvider || !hasConfig || isConsistent == false || issues.Count != 0 ? false : (bool?)null;
            return new OnlineReadinessResult
            {
                Ready = ready,
                HasDownloadProvider = hasProvider,
                HasConfiguration = hasConfig,
                IsConsistent = isConsistent,
                Message = ready == false ? $"PLC '{softwarePath}' has {issues.Count} readiness issue(s)."
                    : "Offline configuration checks passed; actual download readiness remains unknown.",
                Issues = issues.Count > 0 ? issues.ToArray() : null,
                Meta = new HmiObject
                {
                    ["success"] = true,   // the check itself ran; Ready carries the verdict
                    ["configurationReady"] = hasProvider && hasConfig,
                    ["consistencyScope"] = "root, software units and safety units: blocks and types",
                    ["unchecked"] = new HmiArray("deviceReachability", "accessAuthorization", "hardwareConsistency", "downloadPrompts"),
                    ["downloadRouteCount"] = routes.Count,
                    // Ordered best-first — the same ranking DownloadToPlc applies. preferred=true
                    // means the PG/PC adapter shares an IPv4 /24 with the CPU it has to reach.
                    ["downloadRoutes"] = routesJson,
                    ["note"] = "Override the automatic pick with DownloadPlc(pgPcInterface:…) or DownloadPlc(targetIpAddress:…)."
                }
            };
        }
#endif

#if ONLINE_DOWNLOAD
        private OnlineDownloadResult BuildDownloadResponse(
            DownloadResult result,
            string softwarePath,
            DownloadRouteSelection? route,
            DownloadPromptPolicy? prompts = null,
            HmiObject? legitimation = null)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            CollectDownloadMessages(result.Messages, errors, warnings);

            bool ok = result.State == DownloadResultState.Success
                   || result.State == DownloadResultState.Information
                   || result.State == DownloadResultState.Warning;

            var meta = new HmiObject
            {
                ["softwarePath"] = softwarePath,
                ["timestamp"] = DateTime.Now,
                ["downloadState"] = result.State.ToString(),
                // Which PG/PC adapter the download actually left through — the thing you need
                // to see first when a multi-NIC PC downloads "successfully" to the wrong place.
                ["pgPcRoute"] = route?.Description ?? string.Empty,
                ["pgPcRouteCandidates"] = route?.Candidates.Count ?? 0,
                ["targetAddress"] = route?.Address?.Address,
                ["targetAddressSource"] = route?.AddressSource ?? string.Empty
            };
            if (prompts != null)
                foreach (var kv in prompts.Summary()) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
            if (legitimation != null)
                foreach (var kv in legitimation) meta[kv.Key] = kv.Value == null ? null : HmiValues.DeepClone(kv.Value);
            meta["success"] = ok;
            PlcB5Result.Record(meta, result.State, true, messages: new HmiArray(errors.Concat(warnings).Select(m => (object)HmiScalar.Json(m)!).ToArray()));

            return new OnlineDownloadResult
            {
                Ok = ok,
                Message = $"Download {result.State}: {result.ErrorCount} error(s), {result.WarningCount} warning(s)."
                    + (prompts?.ReinitializationSummary() ?? string.Empty) + (prompts?.UnansweredSummary() ?? string.Empty),
                State = result.State.ToString(),
                ErrorCount = result.ErrorCount,
                WarningCount = result.WarningCount,
                Errors = errors.Count > 0 ? errors.ToArray() : null,
                Warnings = warnings.Count > 0 ? warnings.ToArray() : null,
                Meta = meta
            };
        }
#endif

#if ONLINE_DOWNLOAD
        private static void CollectDownloadMessages(
            IEnumerable? messages,
            List<string> errors,
            List<string> warnings)
        {
            if (messages == null) return;
            foreach (var obj in messages)
            {
                if (obj == null) continue;
                try
                {
                    // typed result messages (DownloadResultMessage / UploadResultMessage carry State, Message, counts, nested Messages).
                    if (obj is DownloadResultMessage downloadMessage)
                    {
                        var text = downloadMessage.Message ?? string.Empty; var state = downloadMessage.State;
                        if (state == DownloadResultState.Error && text.Length > 0) errors
                            .Add(text + (downloadMessage.ErrorCount > 1 ? " [" + downloadMessage.ErrorCount + " errors]" : ""));
                        else if (state == DownloadResultState.Warning && text.Length > 0) warnings
                            .Add(text + (downloadMessage.WarningCount > 1 ? " [" + downloadMessage.WarningCount + " warnings]" : ""));
                        CollectDownloadMessages(downloadMessage.Messages, errors, warnings);
                        continue;
                    }
                    if (obj is UploadResultMessage uploadMessage)
                    {
                        var text = uploadMessage.Message ?? string.Empty; var state = uploadMessage.State;
                        if (state == UploadResultState.Error && text.Length > 0) errors
                            .Add(text + (uploadMessage.ErrorCount > 1 ? " [" + uploadMessage.ErrorCount + " errors]" : ""));
                        else if (state == UploadResultState.Warning && text.Length > 0) warnings
                            .Add(text + (uploadMessage.WarningCount > 1 ? " [" + uploadMessage.WarningCount + " warnings]" : ""));
                        CollectDownloadMessages(uploadMessage.Messages, errors, warnings);
                        continue;
                    }
                    var msgText = obj.GetType().GetProperty("Message")?.GetValue(obj) as string ?? string.Empty;
                    var stateObj = obj.GetType().GetProperty("State")?.GetValue(obj);
                    var stateName = stateObj?.ToString() ?? string.Empty;

                    if (stateName == "Error" && !string.IsNullOrWhiteSpace(msgText))
                        errors.Add(msgText);
                    else if (stateName == "Warning" && !string.IsNullOrWhiteSpace(msgText))
                        warnings.Add(msgText);

                    // Recurse into nested Messages
                    var nested = obj.GetType().GetProperty("Messages")?.GetValue(obj) as IEnumerable;
                    if (nested != null)
                        CollectDownloadMessages(nested, errors, warnings);
                }
                catch /* swallow(enumerate-optional): one unreadable result message must not discard the other download or upload diagnostics */ { }
            }
        }
#endif

        #endregion

    }
}
