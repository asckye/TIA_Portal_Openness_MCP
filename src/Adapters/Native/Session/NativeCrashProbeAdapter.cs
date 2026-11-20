using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Optional;
using TiaMcp.Adapters.Native.Plc;
#if OPTIONAL_V20_PLUS
using Siemens.Engineering.SiVArc;
#endif
#if PLC_ALARM_TEXTS
using Siemens.Engineering.SW.Alarm;
#endif

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private static string? CrashProbesEnabled => Environment.GetEnvironmentVariable("TIA_MCP_CRASH_PROBES");

        public Dictionary<string, object?> ReadProbeSivarc(string probe, string softwarePath, string blockPath, string name = "")
        {
            Check();
#if OPTIONAL_V20_PLUS
            var binding = new NativeCrashProbes.SivarcBinding {
                Provider = block => ((CodeBlock)block).GetService<SivarcDataProvider>()
                    ?? throw new PlcSoftwareException("NotSupportedOnVersion", "SivarcDataProvider unavailable on this block."),
                TagDefinitions = provider => ((SivarcDataProvider)provider).TagDefinitions,
                TextDefinitions = provider => ((SivarcDataProvider)provider).TextDefinitions,
                Count = definitions => definitions is TagDefinitionComposition tags ? tags.Count : ((TextDefinitionComposition)definitions).Count,
                Find = (definitions, key) => definitions is TagDefinitionComposition tags ? tags.Find(key) : ((TextDefinitionComposition)definitions).Find(key),
                Item = (definitions, index) => definitions is TagDefinitionComposition tags ? tags[index] : ((TextDefinitionComposition)definitions)[index],
                Name = item => item is TagDefinition tag ? tag.Name : ((TextDefinition)item).Name
            };
#else
            var binding = NativeCrashProbes.ReflectionBinding(block => Hardware.HardwareOfficialService.Require(block,
                "Siemens.Engineering.SiVArc.SivarcDataProvider", "Siemens.Engineering"));
#endif
            return NativeCrashProbes.Sivarc(CrashProbesEnabled, ReleaseKey, probe, name, () =>
            {
                var block = ExactMasterCopyPlcSource(softwarePath, blockPath, true) as PlcBlock;
                SivarcPolicy.RequireSupportedBlockClass(block?.GetType().Name ?? "non-code block");
                return block!;
            }, binding, Diagnostics.AdapterJournal.BeforeProbeStep);
        }

        // Deliberately no ExclusiveAccess: this probe isolates the V16 master-copy crash from the access dialog.
        public Dictionary<string, object?> ExecuteProbeMasterCopy(string sourceKind, string softwarePath, string sourcePath, string libraryName = "",
            bool dryRun = true)
        {
            Check();
            return NativeCrashProbes.MasterCopy(CrashProbesEnabled, sourceKind, sourcePath, dryRun, () =>
            {
                object source = sourceKind switch {
                    "block" => ExactMasterCopyPlcSource(softwarePath, sourcePath, true),
                    "plcType" => ExactMasterCopyPlcSource(softwarePath, sourcePath, false),
                    "blockGroup" => PlcGroupOperations.Group(EngineeringPlcSession.ExactPlcForEngineering(softwarePath, false).BlockGroup, sourcePath),
                    _ => ProbeTagTable(softwarePath, sourcePath)
                };
                return source as IMasterCopySource
                    ?? throw new NotSupportedException("Selected object is not a supported native master-copy source.");
            }, () => ((MasterCopyFolder)EngineeringLibraryFolder(ExactOpenEngineeringLibrary(libraryName), "", "MasterCopyFolder")).MasterCopies,
                (copies, source) => ((MasterCopyComposition)copies).Create((IMasterCopySource)source), Diagnostics.AdapterJournal.BeforeProbeStep);
        }

        private object ProbeTagTable(string softwarePath, string sourcePath)
        {
            var parts = PlcGroupOperations.Parts(sourcePath);
            var group = PlcGroupOperations.Group(EngineeringPlcSession.ExactPlcForEngineering(softwarePath, false).TagTableGroup,
                string.Join("/", parts.Take(parts.Length - 1)));
            return PlcGroupOperations.Find(PlcGroupOperations.Get(group, "TagTables"), parts.Last())
                ?? throw new PlcSoftwareException("NotFound", "Exact PLC tag table not found.");
        }

        public Dictionary<string, object?> ExportProbeAlarmTextLists(string softwarePath, string exportPath, string textListNames, string cultures)
        {
            Check();
#if PLC_ALARM_TEXTS
            return NativeCrashProbes.AlarmExport(CrashProbesEnabled, ReleaseKey, exportPath,
                TiaOpenness.Shared.DataLocations.Current.OutputRoot, textListNames, cultures,
                () => EngineeringPlcSession.ExactPlcForEngineering(softwarePath, false).GetService<PlcAlarmTextListProvider>()
                    ?? throw new PlcSoftwareException("NotSupportedOnVersion", "PlcAlarmTextListProvider unavailable on this PLC."),
                selected => selected.Select(culture => project!.LanguageSettings.Languages.Find(CultureInfo.GetCultureInfo(culture))
                    ?? throw new PlcSoftwareException("NotFound", "Project language not found.")).ToList(),
                (provider, file, names, languages) => ((PlcAlarmTextListProvider)provider)
                    .ExportToXlsx(file, names, (List<Language>)languages)?.State.ToString() ?? "Unknown",
                Diagnostics.AdapterJournal.BeforeProbeStep);
#else
            return NativeCrashProbes.AlarmExport(CrashProbesEnabled, ReleaseKey, exportPath,
                TiaOpenness.Shared.DataLocations.Current.OutputRoot, textListNames, cultures,
                () => throw new NotSupportedException(), _ => throw new NotSupportedException(), (_, _, _, _) => throw new NotSupportedException(),
                Diagnostics.AdapterJournal.BeforeProbeStep);
#endif
        }
    }
}
