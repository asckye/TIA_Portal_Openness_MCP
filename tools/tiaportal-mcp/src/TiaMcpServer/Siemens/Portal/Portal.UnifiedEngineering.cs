using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {

        private static string UnifiedCollection(string category) => category switch {
            "alarmClasses" => "AlarmClasses", "discreteAlarms" => "DiscreteAlarms", "analogAlarms" => "AnalogAlarms",
            "alarmLogs" => "AlarmLogs", "dataLogs" => "DataLogs", "textLists" => "HmiTextLists", "graphicLists" => "HmiGraphicLists",
            "systemTags" => "SystemTags", "systemTextLists" => "HmiSystemTextLists", "auditTrails" => "AuditTrails", "opcUaAlarmTypes" => "OpcUaAlarmTypes",
            _ => throw new ArgumentException("Unknown Unified collection category.") };

    }
}
