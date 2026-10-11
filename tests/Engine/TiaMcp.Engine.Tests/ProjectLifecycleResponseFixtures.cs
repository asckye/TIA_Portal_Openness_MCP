namespace TiaMcpServer.ModelContextProtocol;

public sealed class ResponseConnect : ResponseMessage { }
public sealed class ResponseSaveProject : ResponseMessage { }
public sealed class ResponseSaveAsProject : ResponseMessage
{
    public string? PreviousProjectFile { get; set; }
    public string? NewProjectFile { get; set; }
}
