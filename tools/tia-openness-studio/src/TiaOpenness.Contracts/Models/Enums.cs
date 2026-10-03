namespace TiaOpenness.Contracts.Models
{
    /// <summary>Kind of a software block inside a PLC program.</summary>
    public enum BlockKind
    {
        Unknown = 0,
        OB = 1,
        FB = 2,
        FC = 3,
        DB = 4,
        InstanceDB = 5,
        UDT = 6,
        TagTable = 7,

        /// <summary>An HMI screen, template, popup or slide-in.</summary>
        HmiScreen = 8,
    }

    /// <summary>Export flavour requested from the bridge.</summary>
    public enum ExportFormat
    {
        /// <summary>SimaticML (.xml) &#8212; works for every block kind.</summary>
        SimaticMl = 0,
        /// <summary>External source (.scl / .db / .udt) &#8212; text, only for compilable blocks.</summary>
        Source = 1,
    }

    /// <summary>Severity of a compiler message.</summary>
    public enum CompileSeverity
    {
        Information = 0,
        Warning = 1,
        Error = 2,
    }

    /// <summary>Outcome of a single environment check.</summary>
    public enum CheckStatus
    {
        Pass = 0,
        Warn = 1,
        Fail = 2,
    }

    /// <summary>Which backend the bridge is running against.</summary>
    public enum SessionMode
    {
        /// <summary>Real Siemens.Engineering session &#8212; requires TIA Portal on this machine.</summary>
        Openness = 0,
        /// <summary>In-memory fake used for development and tests without TIA Portal.</summary>
        Mock = 1,
    }
}
