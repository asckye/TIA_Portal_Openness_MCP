using TiaMcpServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Reflection;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseImportBatch : ResponseMessage
    { public IEnumerable<string>? Imported { get; set; } public IEnumerable<ImportFailure>? Failed { get; set; } }
    public class TechnologyObjectInfo
    { public string? Name { get; set; } public string? OfSystemLibElement { get; set; } public string? OfSystemLibVersion { get; set; } public string? TypeHint { get; set; } public string? Folder { get; set; } }
    public class ResponseTechnologyObjectList : ResponseMessage
    { public bool? Ok { get; set; } public string? SoftwarePath { get; set; } public int? Count { get; set; } public TechnologyObjectInfo[]? Items { get; set; } }
}

namespace TiaMcpServer.ModelContextProtocol
{
    public class ImportFailure
    {
        public string? Path { get; set; }
        public string? Error { get; set; }
    }
}
