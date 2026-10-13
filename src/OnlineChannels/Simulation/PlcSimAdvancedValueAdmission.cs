using System;
using System.Text.Json.Nodes;
using TiaMcpServer.Runtime;

namespace TiaMcpServer.ModelContextProtocol
{
    // The host retains the complete typed V4 write contract. Here the already-read
    // simulator type admits the same scalar conversion/finite-number rule without
    // exporting a second V4 type system from the net48 component.
    internal static class RuntimeToolContract
    {
        private sealed class InputFailureException : Exception { }

        internal static object SimulationValue(JsonNode? value, string type, bool dryRun, bool confirmWrite)
        {
            if (!(dryRun || confirmWrite) || value is not JsonValue) throw new InputFailureException();
            object converted;
            try { converted = PlcSimAdvancedLogic.ConvertValue(value, type); }
            catch (Exception error) when (error is ArgumentException or OverflowException)
            { throw new InputFailureException(); }
            if (!PlcSimAdvancedLogic.IsFiniteConvertedValue(converted)) throw new InputFailureException();
            return converted;
        }
    }
}
