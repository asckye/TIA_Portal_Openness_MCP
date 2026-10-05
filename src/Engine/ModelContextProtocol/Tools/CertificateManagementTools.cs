using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class CertificateManagementTools
    {
        private readonly CertificateManagementService _service;

        public CertificateManagementTools(CertificateManagementService service) => _service = service;

        [McpServerTool(Name="ManagePlcCertificate"), Description("[L2][Security][WRITE] Offline engineering certificates of the exact PLC DeviceItem (LocalCertificateManager.LocalCertificateStore): list, read, template (default CertificateTemplate for usage Tls/WebServer/OpcUaServer/OpcUaClient/OpcUaClientServer: Signature, SubjectCommonName, Usage, ValidFrom, ValidUntil, SubjectAlternativeNames), create (usage + template propertiesJson Signature Sha1RSA/Sha256RSA, SubjectCommonName, ValidFrom/ValidUntil ISO dates + subjectAlternativeNamesJson [{type Dns/Email/IP/Uri, value}] via SubjectAlternativeNameComposition.Create), import (filePath; optional password for protected files via Import(file, SecureString), never echoed), export (new file), delete (by exact certificateId), assign/unassign (WebserverCertificate/OpcUaServerCertificate dynamic attribute on the selected owner). dryRun=true default; no download/save; export refuses overwrite; private keys are never exported.")]
        public ResponseMessage ManagePlcCertificate(
            string devicePathJson,
            string itemPathJson,
            [Description("list | read | template | create | import | export | delete | assign | unassign. ")] string action,
            [Description("certificateId: certificate id from the read action.")] string certificateId="",
            string filePath="",
            [Description("usage: Tls | WebServer | OpcUaServer | OpcUaClient | OpcUaClientServer.")] string usage="",
            string propertiesJson="{}",
            [Description("assignment: assignment kind as the read action lists it.")] string assignment="",
            bool dryRun=true,
            [Description("assignmentItemPathJson: JSON array of device-item names the certificate is assigned to.")] string assignmentItemPathJson="",
            [Description("subjectAlternativeNamesJson: JSON array of subject alternative names for the certificate.")] string subjectAlternativeNamesJson="[]",
            string password="")
            => _service.ManagePlcCertificate(devicePathJson,itemPathJson,action,certificateId,filePath,usage,propertiesJson,assignment,dryRun,assignmentItemPathJson,subjectAlternativeNamesJson,password);
    }
}
