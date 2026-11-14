using System;
using System.IO;

namespace TiaMcp.Adapters.Contracts
{
    public static class StepFailureStatus
    {
        public static string Of(Exception error) => error.GetBaseException() switch
        {
            PlcSoftwareException plc => plc.Code,
            HardwareAddressingException hardware => hardware.Status,
            AdapterPreconditionException { Condition: "not-found" } => "NotFound",
            FileNotFoundException _ => "NotFound",
            DirectoryNotFoundException _ => "NotFound",
            AdapterPreconditionException { IsArgument: true } => "InvalidParams",
            ArgumentException _ => "InvalidParams",
            FormatException _ => "InvalidParams",
            NotSupportedException _ => "NotSupportedOnVersion",
            _ => "ReadOrWriteFailed"
        };

        public static string? Parameter(Exception error) => error.GetBaseException() switch
        {
            AdapterPreconditionException precondition => precondition.ParamName,
            ArgumentException argument => argument.ParamName,
            _ => null
        };
    }
}
