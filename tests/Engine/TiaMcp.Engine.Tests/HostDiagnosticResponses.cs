using System.Collections.Generic;
namespace TiaMcpServer.ModelContextProtocol
{
    public class CapabilitySelfTestItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Status { get; set; }
        public string? Detail { get; set; }
    }

    public class ResponseCapabilitySelfTest : ResponseMessage
    {
        public bool? Ok { get; set; }
        public bool? IncludeProjectTree { get; set; }
        public IEnumerable<CapabilitySelfTestItem>? Items { get; set; }
        public string? ProjectTree { get; set; }
    }

    public class DoctorCheck
    {
        public string? Name { get; set; }
        public bool Ok { get; set; }
        public string? Detail { get; set; }
        public string? Fix { get; set; }
    }

    public class ResponseDoctor : ResponseMessage
    {
        public bool? Ready { get; set; }
        public IEnumerable<DoctorCheck>? Checks { get; set; }
        public string? RecommendedNextTool { get; set; }
        public string? Summary { get; set; }
    }

    public class ResponseSafetySelfTest : ResponseMessage
    {
        public bool? Ok { get; set; }
        public IEnumerable<CapabilitySelfTestItem>? Items { get; set; }
        public IEnumerable<string>? Policy { get; set; }
    }

    public class ResponseAcceptanceReport : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? OperationId { get; set; }
        public string? OutputDirectory { get; set; }
        public string? MarkdownPath { get; set; }
        public string? JsonPath { get; set; }
        public ResponseCapabilitySelfTest? SelfTest { get; set; }
        public ResponseSafetySelfTest? SafetySelfTest { get; set; }
    }

    public class ResponseErrorReport : ResponseMessage
    {
        public bool? Ok { get; set; }
        public string? OperationId { get; set; }
        public string? ErrorCode { get; set; }
        public string? Severity { get; set; }
        public string? Summary { get; set; }
        public string? OutputDirectory { get; set; }
        public string? MarkdownPath { get; set; }
        public string? JsonPath { get; set; }
        public IEnumerable<string>? RecommendedNextActions { get; set; }
    }
}
