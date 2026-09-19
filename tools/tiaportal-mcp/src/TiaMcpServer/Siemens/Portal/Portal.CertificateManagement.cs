using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Siemens.Engineering.HW;
using Siemens.Engineering.Security;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // 2.7.32: CertificateTemplate is read typed (Signature / SubjectCommonName / Usage / ValidFrom / ValidUntil + SubjectAlternativeNames).
        private static JsonObject TemplateRow(CertificateTemplate template)
        {
            var row = new JsonObject { ["signature"] = template.Signature.ToString(), ["subjectCommonName"] = template.SubjectCommonName, ["usage"] = template.Usage.ToString(), ["validFrom"] = EngineeringScalarProperties.Json(template.ValidFrom), ["validUntil"] = EngineeringScalarProperties.Json(template.ValidUntil) };
            try { row["subjectAlternativeNames"] = new JsonArray(EngineeringGroupOperations.Items(template.SubjectAlternativeNames).Cast<SubjectAlternativeName>().Select(s => (JsonNode)new JsonObject { ["type"] = s.Type.ToString(), ["value"] = s.Value }).ToArray()); }
            catch (Exception ex) { row["subjectAlternativeNamesError"] = ex.GetBaseException().Message; }
            return row;
        }

        public ResponseMessage ManagePlcCertificate(string devicePathJson, string itemPathJson, string action, string certificateId = "",
            string filePath = "", string usage = "", string propertiesJson = "{}", string assignment = "", bool dryRun = true, string assignmentItemPathJson = "",
            string subjectAlternativeNamesJson = "[]", string password = "")
            => RunHmiStepTool("ManagePlcCertificate", meta => {
                if (!new[] { "list", "read", "template", "create", "import", "export", "delete", "assign", "unassign" }.Contains(action)) throw new ArgumentException("Unsupported certificate action.");
                var subjectAlternativeNames = SecurityDeepLogic.ParseSubjectAlternativeNames(subjectAlternativeNamesJson);
                if (subjectAlternativeNames.Length > 0 && action != "create") throw new ArgumentException("subjectAlternativeNamesJson applies to action=create only.");
                if (!string.IsNullOrEmpty(password) && action != "import") throw new ArgumentException("password applies to action=import only (CertificateComposition.Import(file, SecureString)); it is never echoed.");
                if ((action == "create" || action == "template") && string.IsNullOrWhiteSpace(usage)) throw new ArgumentException("usage (" + string.Join("/", SecurityDeepLogic.CertificateUsages) + ") is required for template/create.");
                bool writing = action != "list" && action != "read" && action != "template" && !dryRun;
                meta["passwordProvided"] = !string.IsNullOrEmpty(password);
                using var access = writing ? AcquireHmiEditAccess() : null;
                var item = ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("Exact PLC DeviceItem path required.");
                var manager = item.GetService<LocalCertificateManager>() ?? throw new NotSupportedException("LocalCertificateManager unavailable at this DeviceItem.");
                var store = manager.LocalCertificateStore;
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "list")
                {
                    meta["certificates"] = new JsonArray(store.Certificates.Select(c => (JsonNode)EngineeringScalarProperties.Read(c)).ToArray());
                    try { meta["enableGlobalCertificatesStore"] = manager.EnableGlobalCertificatesStore; } catch (Exception ex) { meta["enableGlobalCertificatesStoreError"] = ex.GetBaseException().Message; }
                    return "Public certificate metadata listed; no private key exported.";
                }
                Certificate? certificate = null;
                if (action != "create" && action != "import" && action != "unassign" && action != "template")
                {
                    var matches = store.Certificates.Where(c => c.Id.ToString() == certificateId).Take(2).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException("Exact certificate ID did not uniquely match.");
                    certificate = matches[0]; meta["before"] = EngineeringScalarProperties.Read(certificate);
                }
                if (action == "template")
                {
                    // Default template values for one usage (LocalCertificateStore.GetCertificateTemplate); nothing is created.
                    var kind = (CertificateUsage)EngineeringScalarProperties.ConvertValue(JsonValue.Create(SecurityDeepLogic.RequireOneOf(usage, SecurityDeepLogic.CertificateUsages, "usage")), typeof(CertificateUsage))!;
                    meta["template"] = TemplateRow(store.GetCertificateTemplate(kind)); meta["apiCallSuccess"] = true; meta["dataComplete"] = true;
                    meta["catalogs"] = new JsonObject { ["usages"] = new JsonArray(SecurityDeepLogic.CertificateUsages.Select(x => (JsonNode)x).ToArray()), ["signatures"] = new JsonArray(SecurityDeepLogic.SignatureAlgorithms.Select(x => (JsonNode)x).ToArray()), ["subjectAlternativeNameTypes"] = new JsonArray(SecurityDeepLogic.SubjectAlternativeNameTypes.Select(x => (JsonNode)x).ToArray()) };
                    return "Certificate template defaults read for usage " + kind + "; nothing created.";
                }
                if (action == "create")
                {
                    var kind = (CertificateUsage)EngineeringScalarProperties.ConvertValue(JsonValue.Create(SecurityDeepLogic.RequireOneOf(usage, SecurityDeepLogic.CertificateUsages, "usage")), typeof(CertificateUsage))!;
                    var template = store.GetCertificateTemplate(kind);
                    var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                    SecurityDeepLogic.ValidateTemplateProperties(changes);
                    var prepared = EngineeringScalarProperties.Prepare(template.GetType(), changes);
                    meta["template"] = TemplateRow(template); meta["requestedProperties"] = changes.DeepClone();
                    meta["requestedSubjectAlternativeNames"] = new JsonArray(subjectAlternativeNames.Select(s => (JsonNode)new JsonObject { ["type"] = s.Type, ["value"] = s.Value }).ToArray());
                    if (writing)
                    {
                        EngineeringScalarProperties.Apply(template, prepared, meta);
                        foreach (var san in subjectAlternativeNames) template.SubjectAlternativeNames.Create((SubjectAlternativeNameType)Enum.Parse(typeof(SubjectAlternativeNameType), san.Type), san.Value);
                        meta["templateApplied"] = TemplateRow(template);
                        int before = store.Certificates.Count();
                        certificate = store.Certificates.Create(template); meta["after"] = EngineeringScalarProperties.Read(certificate); meta["certificateId"] = certificate.Id.ToString();
                        meta["countBefore"] = before; meta["countAfter"] = store.Certificates.Count();
                        if (!store.Certificates.Any(c => c.Id == certificate.Id)) throw new InvalidOperationException("Create returned but the certificate is not listed in the local store.");
                    }
                }
                else if (action == "import" || action == "export")
                {
                    if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("filePath required.");
                    var file = new FileInfo(filePath);
                    if (action == "import" && !file.Exists) throw new FileNotFoundException("Certificate file missing.", filePath);
                    if (action == "export" && file.Exists) throw new InvalidOperationException("Export refuses to overwrite an existing file.");
                    if (writing)
                    {
                        meta["mayHaveChanged"] = true;
                        if (action == "import")
                        {
                            if (string.IsNullOrEmpty(password)) certificate = store.Certificates.Import(file);
                            else using (var secure = PlcBlockServicesLogic.ToSecureString(password)) certificate = store.Certificates.Import(file, secure);
                            meta["after"] = EngineeringScalarProperties.Read(certificate); meta["certificateId"] = certificate.Id.ToString(); meta["hasPrivateKey"] = certificate.HasPrivateKey;
                        }
                        else { certificate!.Export(file); file.Refresh(); if (!file.Exists || file.Length == 0) throw new InvalidOperationException("Native export produced no file."); meta["fileBytes"] = file.Length; }
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
                    var assignmentOwner = string.IsNullOrEmpty(assignmentItemPathJson) ? item : ExactEngineeringHardware(devicePathJson, assignmentItemPathJson) as DeviceItem
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
    }
}
