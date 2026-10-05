using System;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class CertificateManagementTools
    {
        private readonly CertificateManagementService _service;

        public CertificateManagementTools(CertificateManagementService service) => _service = service;

        [McpServerTool(Name="ManagePlcCertificate"), Description("[L2][Security][WRITE] Offline engineering certificates of the exact PLC DeviceItem (LocalCertificateManager.LocalCertificateStore): list, read, template (default CertificateTemplate for usage Tls/WebServer/OpcUaServer/OpcUaClient/OpcUaClientServer: Signature, SubjectCommonName, Usage, ValidFrom, ValidUntil, SubjectAlternativeNames), create (usage + template properties Signature Sha1RSA/Sha256RSA, SubjectCommonName, ValidFrom/ValidUntil ISO dates + subjectAlternativeNames [{type Dns/Email/IP/Uri, value}] via SubjectAlternativeNameComposition.Create), import (filePath; optional password for protected files via Import(file, SecureString), never echoed), export (new file), delete (by exact certificateId), assign/unassign (WebserverCertificate/OpcUaServerCertificate dynamic attribute on the selected owner). dryRun=true default; no download/save; export refuses overwrite; private keys are never exported.")]
        public CallToolResult ManagePlcCertificate(
            string[] devicePath,
            string[] itemPath,
            [Description("list | read | template | create | import | export | delete | assign | unassign. ")] string action,
            [Description("certificateId: certificate id from the read action.")] string certificateId="",
            string filePath="",
            [Description("usage: Tls | WebServer | OpcUaServer | OpcUaClient | OpcUaClientServer.")] string usage="",
            AttributeMap<Scalar> properties=null!,
            [Description("assignment: assignment kind as the read action lists it.")] string assignment="",
            bool dryRun=true,
            [Description("assignmentItemPath: array of exact device-item names; omission uses the selected owner.")] string[] assignmentItemPath=null!,
            [Description("subjectAlternativeNames: up to 64 closed {type, value} entries; type is Dns, Email, IP or Uri and value has 1..255 characters. Duplicate values of the same type are refused case-insensitively.")] SubjectAlternativeName[] subjectAlternativeNames=null!,
            string password="")
            => SecurityToolContract.Invoke("ManagePlcCertificate", dryRun || action == "list" || action == "read" || action == "template", false,
                () => _service.ManagePlcCertificate(SecurityToolContract.Path(devicePath),SecurityToolContract.Path(itemPath),action,certificateId,filePath,usage,SecurityToolContract.Map(properties),assignment,dryRun,
                    assignmentItemPath == null ? "" : SecurityToolContract.Path(assignmentItemPath),V4Json.Serialize(subjectAlternativeNames ?? Array.Empty<SubjectAlternativeName>()),password), password);
    }
}
