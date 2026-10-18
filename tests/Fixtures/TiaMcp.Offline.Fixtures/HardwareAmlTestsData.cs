using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcp.Engine.Tests
{
    /// <summary>
    /// Offline AML generation: spec parsing, CAEX structure (roles last, attributes, subnet links) and reference-file mode.
    /// </summary>
    internal static class HardwareAmlTests
    {
        private const string Spec = @"{""projectName"":""P1"",""devices"":[{""name"":""PLC_1"",""typeIdentifier"":""System:Device.S71500"",""deviceItems"":[
  {""name"":""Rack_0"",""typeIdentifier"":""OrderNumber:6ES7 590-1AE80-0AA0"",""deviceItems"":[
    {""name"":""PLC_1"",""typeIdentifier"":""OrderNumber:6ES7 516-3AN02-0AB0/V2.9"",""positionNumber"":1,""firmwareVersion"":""V2.9"",""deviceItems"":[
      {""name"":""PROFINET interface_1"",""role"":""CommunicationInterface"",""builtIn"":true,""nodes"":[{""name"":""E1"",""networkAddress"":""192.168.0.1"",""subnetMask"":""255.255.255.0"",""subnetName"":""PN/IE_1""}]}]},
    {""name"":""DI 32x24VDC HF_1"",""typeIdentifier"":""OrderNumber:6ES7 521-1BL00-0AB0/V2.0"",""positionNumber"":2,""attributes"":{""Comment"":""inputs""}}]}]}]}";
    }
}
