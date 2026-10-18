using TiaMcpServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;
using TiaMcp.Shared;
using TiaMcpServer.Siemens.Services;
using Xunit;



// Only the application service boundary is replaced; registered tools and admission are production code.
namespace TiaMcp.Shared
{
    internal sealed class CfcPortService
    {
        internal int Calls;
        internal string[] Arguments = Array.Empty<string>();
        public ResponseMessage ExchangeCfcCharts(string softwarePath, string action, string filePath, string modelVersion = "", long filter = 0, bool unattended = true, bool deleteAtTarget = false, bool dryRun = true, string chartNamesJson = "[]", bool skipChartPreflight = false)
        {
            Calls++; Arguments = new[] { softwarePath, action, filePath, modelVersion, chartNamesJson };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageCfcChartProtection(string softwarePath, string chartName, string action = "read", string currentPassword = "", string newHashedPassword = "", bool dryRun = true, string modelVersion = "V2.0", bool skipChartPreflight = false)
        {
            Calls++; Arguments = new[] { softwarePath, chartName, action, currentPassword, newHashedPassword, modelVersion };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
    }
    internal sealed class TestSuitePortService
    {
        internal int Calls;
        internal string[] Arguments = Array.Empty<string>();
        public ResponseMessage ReadTestSuiteCases(string category, string name = "", int offset = 0, int limit = 100, string kind = "case")
        {
            Calls++; Arguments = new[] { category, name, kind };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ExchangeTestSuiteCase(string category, string action, string name, string filePath = "", string importOptions = "None", string loadOptions = "", bool dryRun = true, string kind = "case")
        {
            Calls++; Arguments = new[] { category, action, name, filePath, importOptions, loadOptions, kind };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage RunTestSuiteCase(string category, string name = "", bool confirmExternalExecution = false, bool dryRun = true, string namesJson = "[]", bool runAll = false, string kind = "case")
        {
            Calls++; Arguments = new[] { category, name, namesJson, kind };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageTestSuiteCase(string category, string name, string action = "read", string kind = "case", string newName = "", string softwarePath = "", string instanceName = "", string executionMode = "", string opcUaServerAddress = "", string serverInterfaceType = "", string interfaceFolderPath = "", string updateOptions = "", string scopeJson = "[]", string targetName = "", string masterCopyPath = "", string libraryName = "", bool dryRun = true)
        {
            Calls++; Arguments = new[] { category, name, action, kind, newName, softwarePath, instanceName, executionMode, opcUaServerAddress, serverInterfaceType, interfaceFolderPath, updateOptions, scopeJson, targetName, masterCopyPath, libraryName };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
    }
    internal sealed class OptionalProductsPortService
    {
        internal int Calls;
        internal string[] Arguments = Array.Empty<string>();
        public ResponseMessage ManageSinumerikArchive(string action, string filePath, string devicePathJson = "[]", string itemPathJson = "[]", string modifiedDevicePathJson = "[]", string modifiedItemPathJson = "[]", string mode = "HardwareAndAllProgramBlocks", string comment = "", string author = "", string password = "", bool dryRun = true)
        {
            Calls++; Arguments = new[] { action, filePath, devicePathJson, itemPathJson, modifiedDevicePathJson, modifiedItemPathJson, mode, comment, author, password };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ImportSinumerikAlarmTexts(string devicePathJson, string filesJson, bool dryRun = true)
        {
            Calls++; Arguments = new[] { devicePathJson, filesJson };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageSinumerikSafetyMode(string devicePathJson, string action = "read", string mode = "", bool dryRun = true, bool confirmSafetyChange = false)
        {
            Calls++; Arguments = new[] { devicePathJson, action, mode };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage InitializeSimotionScripting(bool dryRun = true)
        {
            Calls++; Arguments = Array.Empty<string>();
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ExportScadaData(string filePath, string softwarePath = "", bool dryRun = true)
        {
            Calls++; Arguments = new[] { filePath, softwarePath };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
    }
}

namespace TiaMcpServer.Siemens.Services
{
    internal sealed class OptionalEngineeringService
    {
        internal int Calls;
        internal string[] Arguments = Array.Empty<string>();
        internal JsonObject Result = new JsonObject { ["success"] = true };
        public ResponseMessage ReadSiVArcRules(string category,string objectPathJson="[]",int offset=0,int limit=100)
        {
            Calls++; Arguments = new[] { category, objectPathJson };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
        public ResponseMessage ManageSiVArcRule(string category,string collectionPathJson,string name,string action,string propertiesJson="{}",bool dryRun=true)
        {
            Calls++; Arguments = new[] { category, collectionPathJson, name, action, propertiesJson };
            return new ResponseMessage { Message = "fixture", Meta = Result };
        }
    }
    internal sealed class SpecializedExchangeService
    {
        internal int Calls;
        internal string[] Arguments = Array.Empty<string>();
        public ResponseMessage ExchangePlcSupervisions(string softwarePath,string action,string filePath,string importOptions="None",bool dryRun=true)
        {
            Calls++; Arguments = new[] { softwarePath, action, filePath, importOptions };
            return new ResponseMessage { Message = "fixture", Meta = new JsonObject { ["success"] = true } };
        }
    }
}
