using System;
using System.Text.Json.Nodes;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class AlarmsService : PlcB5PortService
    {
        public AlarmsService() { }
        internal AlarmsService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
    }
    internal sealed class OpcUaService : PlcB5PortService
    {
        public OpcUaService() { }
        internal OpcUaService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
    }
    internal sealed class EngineeringAuditService : PlcB5PortService
    {
        public EngineeringAuditService() { }
        internal EngineeringAuditService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
    }
    internal sealed class SoftwareUnitDeepService : PlcB5PortService
    {
        public SoftwareUnitDeepService() { }
        internal SoftwareUnitDeepService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
    }
    internal sealed class SpecializedExchangeService : PlcB5PortService
    {
        public SpecializedExchangeService() { }
        internal SpecializedExchangeService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
    }
}
