using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseBlockLogic : ResponseMessage
    {
        public string? BlockPath { get; set; }
        public string? Language { get; set; }
        public string? Readable { get; set; }
    }

}
