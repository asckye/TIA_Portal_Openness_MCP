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
        public ResponseMessage ManagePlcCertificate(string devicePathJson, string itemPathJson, string action, string certificateId = "",
            string filePath = "", string usage = "", string propertiesJson = "{}", string assignment = "", bool dryRun = true, string assignmentItemPathJson = "")
            => RunHmiStepTool("ManagePlcCertificate", meta => {
                if (!new[] { "list", "read", "create", "import", "export", "delete", "assign", "unassign" }.Contains(action)) throw new ArgumentException("Unsupported certificate action.");
                bool writing = action != "list" && action != "read" && !dryRun;
                using var access = writing ? AcquireHmiEditAccess() : null;
                var item = ExactEngineeringHardware(devicePathJson, itemPathJson) as DeviceItem ?? throw new ArgumentException("Exact PLC DeviceItem path required.");
                var manager = item.GetService<LocalCertificateManager>() ?? throw new NotSupportedException("LocalCertificateManager unavailable at this DeviceItem.");
                var store = manager.LocalCertificateStore;
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                if (action == "list") { meta["certificates"] = new JsonArray(store.Certificates.Select(c => (JsonNode)EngineeringScalarProperties.Read(c)).ToArray()); return "Public certificate metadata listed; no private key exported."; }
                Certificate? certificate = null;
                if (action != "create" && action != "import" && action != "unassign")
                {
                    var matches = store.Certificates.Where(c => c.Id.ToString() == certificateId).Take(2).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException("Exact certificate ID did not uniquely match.");
                    certificate = matches[0]; meta["before"] = EngineeringScalarProperties.Read(certificate);
                }
                if (action == "create")
                {
                    var kind = (CertificateUsage)EngineeringScalarProperties.ConvertValue(JsonValue.Create(usage), typeof(CertificateUsage))!;
                    var template = store.GetCertificateTemplate(kind);
                    var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                    var prepared = EngineeringScalarProperties.Prepare(template.GetType(), changes);
                    meta["template"] = EngineeringScalarProperties.Read(template); meta["requestedProperties"] = changes.DeepClone();
                    if (writing) { EngineeringScalarProperties.Apply(template, prepared, meta); certificate = store.Certificates.Create(template); meta["after"] = EngineeringScalarProperties.Read(certificate); }
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
                        if (action == "import") { certificate = store.Certificates.Import(file); meta["after"] = EngineeringScalarProperties.Read(certificate); }
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
                return writing ? "Certificate operation completed; no download or explicit save. Password-protected import/private-key export are not exposed." : "Certificate read/preview completed; no persistent change.";
            });
    }
}
