using System;
using System.IO;
using System.Linq;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Hardware;
using Siemens.Engineering.HW;
#if SECURITY_CERTIFICATE
using Siemens.Engineering.Security;
#endif


namespace TiaMcp.Adapters.Native.Security
{
#if SECURITY_CERTIFICATE
    internal sealed class CertificateAdapter
    {
        private readonly SecuritySession _session;

        private readonly CertificateRequest request;
        public CertificateAdapter(SecuritySession session, CertificateRequest request) { _session = session; this.request = request; }

        // CertificateTemplate is read typed (Signature / SubjectCommonName / Usage / ValidFrom / ValidUntil + SubjectAlternativeNames).
        private static HmiObject TemplateRow(CertificateTemplate template)
        {
            var row = new HmiObject { ["signature"] = template.Signature.ToString(), ["subjectCommonName"] = template.SubjectCommonName, ["usage"] = template.Usage.ToString(), ["validFrom"] = HmiScalar.Json(template.ValidFrom), ["validUntil"] = HmiScalar.Json(template.ValidUntil) };
            try { row["subjectAlternativeNames"] = new HmiArray(HardwareGroupOperations.Items(template.SubjectAlternativeNames).Cast<SubjectAlternativeName>().Select(s => (object?)new HmiObject { ["type"] = s.Type.ToString(), ["value"] = s.Value }).ToArray()); }
            catch (Exception ex) { row["subjectAlternativeNamesError"] = ex.GetBaseException().Message; }
            return row;
        }

        public SecurityResult ManagePlcCertificate(string[] devicePathJson, string[] itemPathJson, string action, string certificateId = "",
            string filePath = "", string usage = "", string propertiesJson = "{}", string assignment = "", bool dryRun = true, string[]? assignmentItemPathJson = null,
            string subjectAlternativeNamesJson = "[]", string password = "")
            => _session.RunHmiStepTool("ManagePlcCertificate", meta => {
                meta["mayHaveChanged"] = false; meta["mayHaveWrittenFiles"] = false;
                if (!new[] { "list", "read", "template", "create", "import", "export", "delete", "assign", "unassign" }.Contains(action)) throw new ArgumentException("Unsupported certificate action.");
                var subjectAlternativeNames = SubjectAlternativeNames();
                if (subjectAlternativeNames.Length > 0 && action != "create") throw new ArgumentException("subjectAlternativeNamesJson applies to action=create only.");
                if (!string.IsNullOrEmpty(password) && action != "import") throw new ArgumentException("password applies to action=import only (CertificateComposition.Import(file, SecureString)); it is never echoed.");
                if ((action == "create" || action == "template") && string.IsNullOrWhiteSpace(usage)) throw new ArgumentException("usage (" + string.Join("/", SecurityCatalog.CertificateUsages) + ") is required for template/create.");
                bool writing = action != "list" && action != "read" && action != "template" && !dryRun;
                meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                using var access = writing ? _session.AcquireHmiEditAccess() : null;
                var item = _session.ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("Exact PLC DeviceItem path required.");
                var manager = item.GetService<LocalCertificateManager>() ?? throw new NotSupportedException("LocalCertificateManager unavailable at this DeviceItem.");
                var store = manager.LocalCertificateStore;
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "list")
                {
                    meta["certificates"] = new HmiArray(store.Certificates.Select(c => (object?)HmiScalar.Read(c)).ToArray());
                    try { meta["enableGlobalCertificatesStore"] = manager.EnableGlobalCertificatesStore; } catch (Exception ex) { meta["enableGlobalCertificatesStoreError"] = ex.GetBaseException().Message; }
                    return "Public certificate metadata listed; no private key exported.";
                }
                Certificate? certificate = null;
                if (action != "create" && action != "import" && action != "unassign" && action != "template")
                {
                    var matches = store.Certificates.Where(c => c.Id.ToString() == certificateId).Take(2).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException("Exact certificate ID did not uniquely match.");
                    certificate = matches[0]; meta["before"] = HmiScalar.Read(certificate);
                }
                if (action == "template")
                {
                    // Default template values for one usage (LocalCertificateStore.GetCertificateTemplate); nothing is created.
                    var kind = (CertificateUsage)HmiScalar.ConvertValue(HmiValues.Value(SecurityCatalog.RequireOneOf(usage, SecurityCatalog.CertificateUsages, "usage")), typeof(CertificateUsage))!;
                    meta["template"] = TemplateRow(store.GetCertificateTemplate(kind)); meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                    meta["catalogs"] = new HmiObject { ["usages"] = new HmiArray(SecurityCatalog.CertificateUsages.Select(x => (object?)x).ToArray()), ["signatures"] = new HmiArray(SecurityCatalog.SignatureAlgorithms.Select(x => (object?)x).ToArray()), ["subjectAlternativeNameTypes"] = new HmiArray(SecurityCatalog.SubjectAlternativeNameTypes.Select(x => (object?)x).ToArray()) };
                    return "Certificate template defaults read for usage " + kind + "; nothing created.";
                }
                if (action == "create")
                {
                    var kind = (CertificateUsage)HmiScalar.ConvertValue(HmiValues.Value(SecurityCatalog.RequireOneOf(usage, SecurityCatalog.CertificateUsages, "usage")), typeof(CertificateUsage))!;
                    var template = store.GetCertificateTemplate(kind);
                    var changes = Properties();
                    _session.Validate("template");
                    var prepared = SecurityProperties.Prepare(template.GetType(), changes);
                    meta["template"] = TemplateRow(template); meta["requestedProperties"] = changes.DeepClone();
                    meta["requestedSubjectAlternativeNames"] = new HmiArray(subjectAlternativeNames.Select(s => (object?)new HmiObject { ["type"] = s.Type, ["value"] = s.Value }).ToArray());
                    if (writing)
                    {
                        meta["mayHaveChanged"] = true;
                        SecurityProperties.Apply(template, prepared, meta);
                        foreach (var san in subjectAlternativeNames) template.SubjectAlternativeNames.Create((SubjectAlternativeNameType)Enum.Parse(typeof(SubjectAlternativeNameType), san.Type), san.Value);
                        meta["templateApplied"] = TemplateRow(template);
                        int before = store.Certificates.Count();
                        certificate = store.Certificates.Create(template); meta["after"] = HmiScalar.Read(certificate); meta["certificateId"] = certificate.Id.ToString();
                        meta["countBefore"] = before; meta["countAfter"] = store.Certificates.Count();
                        if (!store.Certificates.Any(c => c.Id == certificate.Id)) throw new InvalidOperationException("Create returned but the certificate is not listed in the local store.");
                    }
                }
                else if (action == "import" || action == "export")
                {
                    if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("filePath required.");
                    var file = new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(filePath));
                    if (action == "import" && !file.Exists) throw new FileNotFoundException("Certificate file missing.", filePath);
                    if (action == "export" && file.Exists) throw new InvalidOperationException("Export refuses to overwrite an existing file.");
                    if (writing)
                    {
                        meta["mayHaveChanged"] = true;
                        if (action == "import")
                        {
                            if (string.IsNullOrEmpty(password)) certificate = store.Certificates.Import(file);
                            else using (var secure = EngineeringCredentialRules.ToSecureString(password)) certificate = store.Certificates.Import(file, secure);
                            meta["after"] = HmiScalar.Read(certificate); meta["certificateId"] = certificate.Id.ToString(); meta["hasPrivateKey"] = certificate.HasPrivateKey;
                        }
                        else
                        {
                            meta["mayHaveWrittenFiles"] = true;
#if SECURITY_CERT_EXPORT_IMPLICIT
                            certificate!.Export(file);
#else
                            certificate!.Export(file, file.Extension.Equals(".cer", StringComparison.OrdinalIgnoreCase) ? CertificateExportFormat.Cer : CertificateExportFormat.Der);
#endif
                            file.Refresh(); if (!file.Exists || file.Length == 0) throw new InvalidOperationException("Native export produced no file."); meta["fileBytes"] = file.Length;
                        }
                    }
                }
                else if (action == "delete" && writing)
                {
                    meta["mayHaveChanged"] = true; certificate!.Delete();
                    if (store.Certificates.Any(c => c.Id.ToString() == certificateId)) throw new InvalidOperationException("Certificate remains after Delete.");
                    meta["verifiedAbsent"] = true;
                }
                else if (action == "assign" || action == "unassign")
                {
                    if (assignment != "WebserverCertificate" && assignment != "OpcUaServerCertificate") throw new ArgumentException("assignment must be WebserverCertificate or OpcUaServerCertificate on the selected DeviceItem.");
                    // Read first: do not write a guessed dynamic attribute on the wrong owner.
                    _session.Validate("assignmentLocator");
                    var assignmentOwner = assignmentItemPathJson == null ? item : _session.ExactEngineeringHardware(devicePathJson, assignmentItemPathJson) as DeviceItem
                        ?? throw new ArgumentException("Assignment owner must be a DeviceItem.");
                    var old = assignmentOwner.GetAttribute(assignment);
                    meta["previousCertificateId"] = (old as Certificate)?.Id.ToString();
                    meta["assignment"] = assignment;
                    if (writing)
                    {
                        meta["mayHaveChanged"] = true; assignmentOwner.SetAttribute(assignment, action == "assign" ? certificate : null!);
                        var actual = assignmentOwner.GetAttribute(assignment) as Certificate;
                        if (actual?.Id.ToString() != (action == "assign" ? certificateId : null)) throw new InvalidOperationException("Certificate assignment readback differs.");
                    }
                }
                return writing ? "Certificate operation completed; no download or explicit save. Private keys are never exported." : "Certificate read/preview completed; no persistent change.";
            });
        private CertificateSubjectName[] SubjectAlternativeNames()
        { _session.Validate("san"); return request.SubjectAlternativeNames; }
        private HmiObject Properties()
        { _session.Validate("properties"); return new HmiObject(HardwareScalarEvidence.Requested(request.Properties)) { ScalarInputs = request.Properties }; }
    }
#endif
}
