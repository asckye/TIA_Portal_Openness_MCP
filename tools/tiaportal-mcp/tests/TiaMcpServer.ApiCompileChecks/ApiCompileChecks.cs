using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;

// These methods are never executed by the check. Compilation binds the real
// authorized PublicAPI types, generic constraints, return types and overloads.
internal static class ApiCompileChecks
{
    internal static Software Software(DeviceItem item) =>
        ((IEngineeringServiceProvider)item).GetService<SoftwareContainer>().Software;
    internal static Project Open(ProjectComposition projects, FileInfo file) => projects.Open(file);
    internal static Project Create(ProjectComposition projects, DirectoryInfo directory, string name) => projects.Create(directory, name);
    internal static void Export(PlcBlock block, FileInfo file) => block.Export(file, ExportOptions.None);
    internal static IList<PlcBlock> Import(PlcBlockComposition blocks, FileInfo file) => blocks.Import(file, ImportOptions.None);
    internal static ICompilable Compiler(PlcSoftware plc) => ((IEngineeringServiceProvider)plc).GetService<ICompilable>();
    internal static CompilerResult Compile(ICompilable compiler) => compiler.Compile();
    internal static PlcUserConstantComposition UserConstants(PlcTagTable table) => table.UserConstants;
    internal static PlcSystemConstantComposition SystemConstants(PlcTagTable table) => table.SystemConstants;
    internal static PlcUserConstant CreateConstant(PlcTagTable table, string name, string type, string value) => table.UserConstants.Create(name, type, value);
}
