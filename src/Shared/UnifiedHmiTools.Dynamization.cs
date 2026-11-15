using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Hmi;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    internal sealed partial class UnifiedHmiTools
    {

        public ResponseMessage EnsureUnifiedHmiDynamization(string hmiSoftwarePath,
            string screenName,
            string itemName,
            string propertyName,
            string dynamizationType = "", bool dryRun = true)
        {
            try
            {
                return _service.EnsureUnifiedHmiDynamization(hmiSoftwarePath, screenName, itemName, propertyName, dynamizationType, dryRun);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex, $"Unexpected error ensuring dynamization '{itemName}.{propertyName}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }

        public ResponseMessage BindUnifiedHmiTagDynamization(string hmiSoftwarePath,
            string screenName,
            string itemName,
            string propertyName,
            string tagName,
            string dataType = "",
            string plcTag = "",
            string address = "", bool dryRun = true)
        {
            try
            {
                return _service.BindUnifiedHmiTagDynamization(hmiSoftwarePath, screenName, itemName, propertyName, tagName, dataType, plcTag, address, dryRun);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw LegacyFailure(ex, $"Unexpected error binding tag dynamization '{itemName}.{propertyName}': {ex.Message}{McpHints.Recovery(ex)}");
            }
        }
    }
}
