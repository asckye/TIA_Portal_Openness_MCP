using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcp.Logic.Generation
{
    public sealed class StandardCheckResult
    {
        public string Schema { get; set; } = "";
        public StandardCheckResultPackage Package { get; set; } = new StandardCheckResultPackage();
        public StandardCheckResultTarget Target { get; set; } = new StandardCheckResultTarget();
        public List<StandardCheckResultFindingsItem> Findings { get; set; } = new List<StandardCheckResultFindingsItem>();
        public List<StandardCheckResultUnavailableItem> Unavailable { get; set; } = new List<StandardCheckResultUnavailableItem>();
    }

    public sealed class StandardCheckResultPackage
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    public sealed class StandardCheckResultTarget
    {
        public string Release { get; set; } = "";
        public string ProjectIdentity { get; set; } = "";
    }

    public sealed class StandardCheckResultFindingsItem
    {
        public string RuleId { get; set; } = "";
        public string Severity { get; set; } = "";
        public string ObjectPath { get; set; } = "";
        public string Message { get; set; } = "";
        public string FixHint { get; set; } = "";
        public bool AutoFixable { get; set; }
    }

    public sealed class StandardCheckResultUnavailableItem
    {
        public string RuleId { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
