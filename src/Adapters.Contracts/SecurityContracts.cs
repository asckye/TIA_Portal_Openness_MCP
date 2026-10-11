using System.Collections.Generic;
namespace TiaMcp.Adapters.Contracts;
public sealed class SecurityResult : IWorkerOperationReply
{
    public string Message { get; set; } = "";
    public Dictionary<string, object?> Meta { get; set; } = new Dictionary<string, object?>();
    public bool RequiresSessionReset { get; set; }
    public bool MayHaveChanged => Meta.TryGetValue("mayHaveChanged", out var changed)
        && string.Equals(System.Convert.ToString(changed, System.Globalization.CultureInfo.InvariantCulture), "true", System.StringComparison.OrdinalIgnoreCase);
    public bool BlockReadsAfterUncertain => true;
}

public sealed class SecurityDataResult : Dictionary<string, object?>, IWorkerOperationReply
{
    public SecurityDataResult(IDictionary<string, object?> values) : base(values) { }
    private bool Flag(string name, bool expected) => TryGetValue(name, out var value)
        && string.Equals(System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), expected ? "true" : "false", System.StringComparison.OrdinalIgnoreCase);
    public bool MayHaveChanged => Flag("mayHaveChanged", true);
    public bool RequiresSessionReset => MayHaveChanged && (Flag("writeOutcomeKnown", false) || Flag("ok", false));
    public bool BlockReadsAfterUncertain => true;
}

public sealed class SecurityValidationFault
{
    public string Kind { get; set; } = "argument";
    public string Message { get; set; } = "";
}
public sealed class ProjectUserPlan
{
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public bool NeedsPassword { get; set; }
    public bool NeedsRole { get; set; }
    public bool NeedsRight { get; set; }
    public bool NeedsDevice { get; set; }
    public bool NeedsGroup { get; set; }
    public bool Creates { get; set; }
    public bool Deletes { get; set; }
    public bool NoName { get; set; }
}
public sealed class ProjectUserRequest
{
    public string Operation { get; set; } = "";
    public string Category { get; set; } = "users";
    public string Name { get; set; } = "";
    public string Action { get; set; } = "";
    public string Password { get; set; } = "";
    public string RoleName { get; set; } = "";
    public string RightName { get; set; } = "";
    public string Comment { get; set; } = "";
    public string Group { get; set; } = "";
    public string[] DevicePath { get; set; } = System.Array.Empty<string>();
    public string[] ItemPath { get; set; } = System.Array.Empty<string>();
    public int Offset { get; set; }
    public int Limit { get; set; } = 100;
    public bool ConfirmChange { get; set; }
    public bool DryRun { get; set; } = true;
    public ProjectUserPlan Plan { get; set; } = new ProjectUserPlan();
    public Dictionary<string, SecurityValidationFault> Faults { get; set; } = new Dictionary<string, SecurityValidationFault>();
}

public sealed class CertificateSubjectName
{
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
}
public sealed class CertificateRequest
{
    public string[] DevicePath { get; set; } = System.Array.Empty<string>();
    public string[] ItemPath { get; set; } = System.Array.Empty<string>();
    public string[]? AssignmentItemPath { get; set; }
    public string Action { get; set; } = "";
    public string CertificateId { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Usage { get; set; } = "";
    public Dictionary<string, HardwareScalar> Properties { get; set; } = new Dictionary<string, HardwareScalar>();
    public string Assignment { get; set; } = "";
    public bool DryRun { get; set; } = true;
    public CertificateSubjectName[] SubjectAlternativeNames { get; set; } = System.Array.Empty<CertificateSubjectName>();
    public string Password { get; set; } = "";
    public Dictionary<string, SecurityValidationFault> Faults { get; set; } = new Dictionary<string, SecurityValidationFault>();
}

public sealed class UmcPlan
{
    public string Kind { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Reads { get; set; }
    public bool Creates { get; set; }
    public bool Deletes { get; set; }
    public bool NeedsRole { get; set; }
    public bool NeedsNewName { get; set; }
    public bool NeedsCredentials { get; set; }
    public bool Writes { get; set; }
}
public sealed class SecurityDeepRequest
{
    public string Operation { get; set; } = "";
    public string Scope { get; set; } = "project";
    public string Action { get; set; } = "read";
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Kind { get; set; } = "user";
    public string NewName { get; set; } = "";
    public string RoleName { get; set; } = "";
    public string ServerUserName { get; set; } = "";
    public string ServerPassword { get; set; } = "";
    public Dictionary<string, HardwareScalar> Properties { get; set; } = new Dictionary<string, HardwareScalar>();
    public Dictionary<string, HardwareScalar> Attributes { get; set; } = new Dictionary<string, HardwareScalar>();
    public string[] DevicePath { get; set; } = System.Array.Empty<string>();
    public string[] ItemPath { get; set; } = System.Array.Empty<string>();
    public string ServerAddress { get; set; } = "";
    public int ServerPort { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 100;
    public bool ConfirmDelete { get; set; }
    public bool ConfirmChange { get; set; }
    public bool DryRun { get; set; } = true;
    public UmcPlan UmcPlan { get; set; } = new UmcPlan();
    public Dictionary<string, SecurityValidationFault> Faults { get; set; } = new Dictionary<string, SecurityValidationFault>();
}

public sealed class DeviceUserRequest
{
    public string[] DevicePath { get; set; } = System.Array.Empty<string>();
    public string[] ItemPath { get; set; } = System.Array.Empty<string>();
    public string Family { get; set; } = "";
    public string Action { get; set; } = "read";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string[] Permissions { get; set; } = System.Array.Empty<string>();
    public bool Active { get; set; } = true;
    public string NewName { get; set; } = "";
    public bool ConfirmDelete { get; set; }
    public bool DryRun { get; set; } = true;
    public Dictionary<string, SecurityValidationFault> Faults { get; set; } = new Dictionary<string, SecurityValidationFault>();
}

public sealed class HardwareSecurityRequest
{
    public string Operation { get; set; } = "";
    public string[] DevicePath { get; set; } = System.Array.Empty<string>();
    public string[] ItemPath { get; set; } = System.Array.Empty<string>();
    public string DeviceName { get; set; } = "";
    public bool Enable { get; set; } = true;
    public string Action { get; set; } = "read";
    public string SoftwarePath { get; set; } = "";
    public string TableKind { get; set; } = "watch";
    public string TablePath { get; set; } = "";
    public string Access { get; set; } = "Read";
    public string AccessLevel { get; set; } = "";
    public string Password { get; set; } = "";
    public string NewPassword { get; set; } = "";
    public bool ConfirmChange { get; set; }
    public bool DryRun { get; set; } = true;
    public string NormalizedAction { get; set; } = "";
    public string NormalizedLevel { get; set; } = "";
    public Dictionary<string, SecurityValidationFault> Faults { get; set; } = new Dictionary<string, SecurityValidationFault>();
}
