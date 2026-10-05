// Headless host adapter for the Siemens MIT source; no UI calls or automatic import.
#nullable disable
using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Siemens.Engineering.SW.Units;
namespace AddInOpcUaInterface.Other
{
 internal sealed class AddInExecutionContext : IDisposable
 {
  [ThreadStatic] private static AddInExecutionContext current;
  public static AddInExecutionContext Current => current ?? throw new InvalidOperationException("No generation context.");
  public bool IsSoftwareUnit;
  public string SoftwareUnitNamespace;
  public PlcUnit Unit;
  public PlcUnit GetSelectedSoftwareUnit() => Unit;
  public readonly List<string> Warnings = new List<string>();
  public AddInExecutionContext() { if (current != null) throw new InvalidOperationException("Nested generation is not supported."); current = this; }
  public void Dispose() { current = null; }
        // Interface name and URI
        public string InterfaceName;
        public string InterfaceURI;
        public string FilePath;

        // XDocument of the server interface
        public XDocument OpcUaInterface;             // XML document to build the server interface
        public XNamespace RootNameSpace;             // Namespace of the OPC Foundation 
        public XNamespace RootNameSpaceSi;           // Namespace used to map nodes with project variables

        // Variables for the Access Level filter:
        // Possible values: "Not Accessible" "Read only" "Write only" "Read Write" "Project's access levels"
        public Dictionary<int, string> AccessLevelDictionary = new Dictionary<int, string>
        {
            { 0, "Not Accessible" },
            { 1, "Read only" },
            { 2, "Write only" },
            { 3, "Read Write" },
            { 4, "Project's access levels" }
        };

        // Default access level values for the "Create" option
        public int InputsAccessLevel = 4;    // Access level for input variables
        public int MemoryAccessLevel = 4;    // Access level for memory variables
        public int OutputsAccessLevel = 4;    // Access level for output variables
        public int CountersAccessLevel = 4;    // Access level for counters
        public int TimersAccessLevel = 4;    // Access level for timers
        public int GlobalDBsAccessLevel = 4;    // Access level for Global DB variables
        public int InstanceDBsAccessLevel = 4;    // Access level for Instance DB variables
        public int SafetyGlobalDBsAccessLevel = 1;    // Access level for Safety Global DB variables
        public int SafetyInstanceDBsAccessLevel = 1;    // Access level for Safety Instance DB variables

        // Other settings for "Extend Create"
        public string NodeIdentifier = "String";     // Use "string"/"numeric" node identifiers
        public bool OptimizedData = false;        // Use "Not optimized"/"optimized" server interface
        public bool KeepEmptyDBs = false;        // Remove empty Data Blocks
        public bool KeepFolderStructure = false;        // Keep the folder structure present in the project

        // Number of nodes added to the server interface
        public int NumberDefaultNodes = 0;     // Number of nodes imported from the InterfaceTemplate.xml
        public int NumberUserSystemDataTypes = 0;     // Number of User and System Data Types defined in the project
        public int NumberTags = 0;     // Number of tags defined on all Tag Tables
        public int NumberGlobalDBs = 0;     // Number of nodes (folders, data blocks, variables) associated with Global DBs
        public int NumberInstanceDBs = 0;     // Number of nodes (folders, data blocks, variables) associated with Instance DBs

 }
 internal static class LogMessages { public static void PublishLog(string message) { AddInExecutionContext.Current.Warnings.Add(message); } }
 internal static class DisplayMessage
 {
  internal sealed class Progress { public string Text { get; set; } }
  private static readonly Progress progress = new Progress();
  public static Progress GetExclusiveAccess() => progress;
  public static void ErrorMessage(string message) { throw new InvalidOperationException(message); }
 }
}
