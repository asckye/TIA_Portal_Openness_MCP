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
        /// <summary>
        /// Subscribes the OnlineLegitimation handler of a ConnectionConfiguration for the duration of a GoOnline / Download /
        /// Upload call: password prompts (legacy and UMAC) are answered from the given credentials, and the TLS trust prompt of
        /// S7-1500 FW >= 2.9 CPUs (TlsVerificationConfiguration) is answered Trusted only when trustDeviceCertificate is set. A
        /// password goes to a prompt that reports IsSecureCommunication=false only with allowNonTlsLogin. Returns
        /// an IDisposable that unsubscribes when disposed; callers MUST dispose to avoid handler leaks. Returns null when the
        /// configuration object is not a ConnectionConfiguration (no event to hook).
        /// </summary>
        private static IDisposable
            ? AttachOnlineLegitimationHandler(object? configuration, string? password, HmiObject? meta, bool trustDeviceCertificate, bool allowNonTlsLogin)
            => AttachOnlineLegitimationHandler(configuration, password, null, null, meta, trustDeviceCertificate, allowNonTlsLogin);

        // besides the legacy password prompt, UMAC-protected PLCs raise OnlineAuthenticationConfiguration (user name +
        // password + UserType, IsSecureCommunication, GetSupportedAuthenticationTypes); the answered prompts are reported to meta.
        // Subscribe even without a password so the TLS trust decision is answered and recorded.
        // Historical PLCSIM Advanced Softbus observation (version/date not recorded): an unanswered TLS prompt
        // refuses the first connection; see docs/reference/real-machine-ledger.md.
        private static IDisposable
            ? AttachOnlineLegitimationHandler(object? configuration, string? password, string? userName, string? userType, HmiObject? meta,
                bool trustDeviceCertificate, bool allowNonTlsLogin)
        {
#if ONLINE_LEGITIMATION
            if (configuration is not ConnectionConfiguration conn)
                return null;

            // Build the SecureString once. The same instance can be reused across multiple
            // legitimation prompts within the same call (e.g., read-then-write access).
            SecureString? secure = null;
            if (!string.IsNullOrEmpty(password))
            {
                secure = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.ToSecureString(password!);
            }

            OnlineConfigurationDelegate handler = (cfg) =>
            {
#if ONLINE_TLS
                if (cfg is TlsVerificationConfiguration tls)
                {
                    var before = tls.CurrentSelection.ToString();
                    var apply = TiaMcp.Adapters.Contracts.EngineeringCredentialRules.TlsSelectionToApply(trustDeviceCertificate, before);
                    if (apply != null) tls.CurrentSelection = TlsVerificationConfigurationSelection.Trusted;
                    if (meta != null) meta["tlsVerification"] = TlsRecord(tls, before, trustDeviceCertificate, apply != null);
                }
                else
#endif
                if (secure == null) return;

#if ONLINE_AUTHENTICATION
                else if (cfg is OnlineAuthenticationConfiguration auth)
                {
                    OnlineCredentials credentials = auth.OnlineCredentials;
                    var supported = new HmiArray();
                    try { foreach (AuthenticationType type in auth.GetSupportedAuthenticationTypes()) supported.Add(type.CurrentUserType.ToString()); } catch { /* swallow(enumerate-optional): supported authentication types are diagnostic; credentials still answer the prompt */ }
                    var nonTlsRefusal = DownloadPromptPolicy.NonTlsLoginRefusal(auth.IsSecureCommunication, allowNonTlsLogin);
                    if (nonTlsRefusal != null)
                    {
                        if (meta != null) meta["onlineAuthentication"] = new HmiObject {
                            ["isSecureCommunication"] = false, ["answered"] = false, ["note"] = nonTlsRefusal, ["supportedUserTypes"] = supported,
                                ["userName"] = userName ?? ""
                        };
                        return;
                    }
                    if (!string.IsNullOrEmpty(userName)) credentials.Name = userName;
                    if (!string.IsNullOrEmpty(userType)) credentials.Type = (UserType)Enum.Parse(typeof(UserType), userType);
                    else if (!string.IsNullOrEmpty(userName)) credentials.Type = UserType.ProjectUser;
                    credentials.SetPassword(secure);
                    if (meta != null) meta["onlineAuthentication"] = new HmiObject {
                        ["isSecureCommunication"] = auth.IsSecureCommunication, ["supportedUserTypes"] = supported, ["userName"] = userName ?? "",
                            ["userType"] = credentials.Type.ToString()
                    };
                }
#endif
                else if (cfg is OnlinePasswordConfiguration pwdCfg)
                {
#if !ONLINE_PASSWORD_SECURITY
                    pwdCfg.SetPassword(secure);
                    if (meta != null) meta["onlinePassword"] = new HmiObject { ["answered"] = true };
#else
                    var nonTlsRefusal = DownloadPromptPolicy.NonTlsLoginRefusal(pwdCfg.IsSecureCommunication, allowNonTlsLogin);
                    if (nonTlsRefusal != null)
                    {
                        if (meta != null) meta["onlinePassword"] = new HmiObject {
                            ["isSecureCommunication"] = false, ["answered"] = false, ["note"] = nonTlsRefusal };
                        return;
                    }
                    pwdCfg.SetPassword(secure);
                    if (meta != null) meta["onlinePassword"] = new HmiObject { ["isSecureCommunication"] = pwdCfg.IsSecureCommunication };
#endif
                }
            };
            conn.OnlineLegitimation += handler;
            return new HandlerScope(() => { conn.OnlineLegitimation -= handler; secure?.Dispose(); });

#else
            return null;
#endif
        }

#if ONLINE_TLS
        // The TLS decision record. The certificates the device presented are read where the API offers them (V21
        // TlsVerificationConfiguration.Certificates -> VerificationCertificate.Certificate, an X509Certificate2); the property is
        // resolved by name so the earlier releases compile and simply report none.
        private static HmiObject TlsRecord(object tls, string before, bool trustDeviceCertificate, bool trusted)
        {
            var typed = (TlsVerificationConfiguration)tls;
            var record = new HmiObject
            {
                ["plcName"] = typed.PlcName, ["verificationInfo"] = typed.VerificationInfo,
                ["selectionBefore"] = before, ["selectionAfter"] = typed.CurrentSelection.ToString(),
                ["trustDeviceCertificate"] = trustDeviceCertificate,
                ["note"] = trusted
                    ? "Certificate trusted for this call on the caller's decision (trustDeviceCertificate=true); the same prompt TIA shows in the UI."
                    : trustDeviceCertificate ? "Already trusted."
                    : before == "Trusted" ? "Already trusted; no decision needed."
                    : "Left untrusted (trustDeviceCertificate=false); TIA refuses the connection. Check the presented certificate, then pass " +
                        "trustDeviceCertificate=true to trust it for this call."
            };
            var certificates = PresentedCertificates(tls);
            if (certificates != null) record["certificates"] = certificates;
            return record;
        }

        private static HmiArray? PresentedCertificates(object tls)
        {
            try
            {
                if (tls.GetType().GetProperty("Certificates")?.GetValue(tls) is not IEnumerable certificates) return null;
                var rows = new HmiArray();
                foreach (var item in certificates)
                {
                    if (item?.GetType().GetProperty("Certificate")?.GetValue(item) is not X509Certificate2 certificate) continue;
                    using var sha256 = SHA256.Create();
                    rows.Add(new HmiObject
                    {
                        ["subject"] = certificate.Subject, ["issuer"] = certificate.Issuer, ["serialNumber"] = certificate.SerialNumber,
                        ["notBefore"] = certificate.NotBefore.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                        ["notAfter"] = certificate.NotAfter.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                        ["thumbprintSha1"] = certificate.Thumbprint,
                        ["sha256"] = BitConverter.ToString(sha256.ComputeHash(certificate.RawData)).Replace("-", "")
                    });
                    if (rows.Count >= 8) break;
                }
                return rows;
            }
            catch (Exception ex)
            {
                // The record is diagnostic; an unreadable certificate list must not fail the TIA callback.
                return new HmiArray(new HmiObject { ["unreadable"] = ex.GetType().Name });
            }
        }
#endif

        private sealed class HandlerScope : IDisposable
        {
            private Action? _detach;
            public HandlerScope(Action detach) { _detach = detach; }
            public void Dispose() { _detach?.Invoke(); _detach = null; }
        }


    }
}
