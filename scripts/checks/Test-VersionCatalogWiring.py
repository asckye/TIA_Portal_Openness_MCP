"""Source-contract checks only: these do NOT compile or execute the C# engine."""
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET
from engine_sources import EngineSources

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / 'src/Engine'
LOGIC = ROOT / 'src/Logic'
sources = EngineSources()


def read(path):
    return path.read_text(encoding='utf-8-sig')


class VersionCatalogWiring(unittest.TestCase):
    def test_policy_wraps_direct_and_isolated_dispatch(self):
        wiring = sources.member('WrapTools')
        # Direct and isolated dispatch are chosen first; readiness and version policy then wrap whichever was chosen.
        self.assertIn('Isolation.IsolatedWorkerHost.Wrap(tools)', wiring)
        self.assertIn('WrapWithSerializedCalls(WrapWithResponseGuard(tools))', wiring)
        self.assertIn('WrapWithVersionPolicy(Isolation.OpennessReadinessGuard.Wrap(guarded))', wiring)
        wrapper = sources.member('InvokeAsync', owner='VersionPolicyTool')
        self.assertLess(wrapper.index('V4Admission'), wrapper.index('return inner.InvokeAsync'))
        self.assertNotIn('VersionCallProblem', wrapper)
        binding = sources.member('BindV4Call')
        self.assertLess(binding.index('VersionCallProblem'), binding.index('ToolInputSchema'))
        profile = sources.member('GetLiteTools') + sources.member('GetAllTools')
        self.assertEqual(profile.count('VersionToolProblem(name).Length == 0'), 2)
        invoke = sources.member('InvokeToolMethod')
        # Version admission, then binding check, then target resolution, then the one reflective call.
        version = invoke.index('VersionCallProblem')
        binding_check = invoke.index('ValidateRuntimeBinding(method)')
        target = invoke.index('object? target = method.IsStatic ? null : EngineServices.Get(method.DeclaringType!);')
        self.assertTrue(version < binding_check < target < invoke.index('method.Invoke(target, call)'))
        self.assertIn('AvailableToolMethods((_bridgeCatalog ?? ToolCatalog.Engine).Methods, includeUnavailable)', sources.member('AllToolMethods', owner='McpServer'))
        self.assertIn('includeUnavailable || VersionToolProblem(kv.Key).Length == 0', sources.member('AvailableToolMethods'))
        self.assertIn('BindV4Call', sources.member('CallTool', tool=True))
        self.assertIn('BindV4Call', sources.member('PreviewToolCall', tool=True))
        self.assertIn('versionAvailable', sources.member('PreflightToolCall', signature='string argumentsJson)'))

    def test_version_exclusions_have_real_registered_names(self):
        policy = sources.type_text('ToolVersionPolicy').split('internal static string ToolProblem', 1)[0]
        excluded = re.findall(r'\["([^"]+)"\]\s*=', policy)
        self.assertEqual(len(excluded), 11)
        registered = set(re.findall(r'McpServerTool\(Name\s*=\s*"([^"]+)"', sources.all_text()))
        self.assertFalse(set(excluded) - registered)
        self.assertIn('expected_full = len(catalog)', read(ROOT / 'scripts/checks/Test-WorkerIsolation.py'))
        self.assertIn("['releases'][str(args.major)]", read(ROOT / 'scripts/checks/Test-WorkerIsolation.py'))
        stability = read(ROOT / 'scripts/checks/Test-LocalStability.py')
        self.assertIn('ToolProfiles.resx', stability)
        self.assertIn('args.full_tool_count = len(roster)', stability)
        self.assertIn("sum('lite' in row['profiles'] for row in roster)", stability)

    def test_legacy_contract_remains_unbound(self):
        source = sources.type_text('OpennessReleaseContract')
        self.assertIn('expectedIdentity == null', source)
        self.assertIn('EngineeringAssemblyIdentity.RequireMatch', source)
        self.assertIn('HW.ISoftwareContainer', source)
        self.assertIn('HW.Features.SoftwareContainer', source)
        self.assertIn('typeof(string) : typeof(FileInfo)', source)
        self.assertIn('"Siemens.Engineering.IEngineeringObject"', source)
        self.assertNotIn('Assembly.Load', source)

    def test_move_refusal_and_recovery_precede_cleanup(self):
        source = sources.member('MoveBlockToGroup', owner='PlcBlocksService', tool=False)
        self.assertLess(source.index('if (block is OB)'), source.index('EnsurePlcBlockGroup'))
        self.assertLess(source.index('moveVerified = verifyGroup'), source.index('finally'))
        self.assertIn('if (moveVerified)', source.split('finally', 1)[1])
        self.assertIn('Recovery export retained', source)

    def test_precise_keys_and_existing_engines(self):
        source = read(LOGIC / 'Siemens/TiaVersionCatalog.cs')
        keys = re.findall(r'(?:Foundation|new TiaVersionDescriptor)\("([0-9.a-z]+)",', source)
        self.assertEqual(keys, ['14sp1', '15.1', '16', '17', '18', '19', '20', '21'])
        runnable = re.findall(r'new TiaVersionDescriptor\("([^"]+)", "[^"]+", \d+, true,', source)
        self.assertEqual(runnable, ['20', '21'])
        self.assertIn('major, true, "v" + key, null', source)

    def test_guard_precedes_resolution_and_native_host(self):
        source = sources.member('Main')
        guard = source.index('TiaVersionCatalog.RequireMatchingEngine(tiaMajorVersion, EngineRouter.CompiledTiaMajorVersion)')
        self.assertLess(guard, source.index('AssemblyResolve += Engineering.Resolver'))
        self.assertLess(guard, source.index('Openness.Initialize('))
        self.assertIn('detected ?? EngineRouter.CompiledTiaMajorVersion', source)
        self.assertNotIn('assembly load will likely fail', source)

    def test_diagnostics_remain_reachable(self):
        source = sources.member('Main')
        self.assertLess(source.index('CliOptions.IsInformationalCommand(args)'), source.index('CliOptions.ParseArgs(args)'))
        self.assertLess(source.index('string.Equals(args[0], "doctor"'), source.index('TiaVersionCatalog.RequireRunnable(tiaMajorVersion)'))
        doctor = sources.member('DoctorCli')
        self.assertIn('ready &= supported;', doctor)

    def test_registration_reuses_selected_version(self):
        source = sources.member('Config', signature='string[] args').split('string exe =', 1)[0]
        self.assertIn('Engineering.TiaMajorVersion', source)
        self.assertIn('RequireMatchingEngine', source)
        self.assertNotIn('DetectTiaMajorVersion', source)
        self.assertNotIn('Opt(args', source)

    def test_configurator_uses_keys_not_indices(self):
        source = read(ROOT / 'src/Studio/Gui/Configuration/ConfigurationView.xaml.cs')
        selected = source.split('private string SelectedVersion', 1)[1].split('private string StatePath', 1)[0]
        self.assertIn('selected.Key', selected)
        self.assertNotIn('SelectedIndex', selected)
        self.assertIn('versions.ItemsSource = TiaVersionCatalog.Runnable', source)
        tree = ET.parse(ROOT / 'src/Studio/Gui/Configuration/ConfigurationView.xaml')
        version = next(e for e in tree.iter() if e.get('{http://schemas.microsoft.com/winfx/2006/xaml}Name') == 'Version')
        self.assertEqual(version.get('SelectedValuePath'), 'Key')
        self.assertFalse(list(version))

    def test_shared_source_build_and_package_inputs(self):
        build = read(ROOT / 'build-tools/release/ReleaseCommands.cs')
        self.assertIn('TiaOpenness.Configuration.Tests.csproj', build)
        self.assertIn('build-configurator', build)
        self.assertNotIn('/main:TiaMcpConfigurator.Tests', build)
        package = read(ROOT / 'scripts/build/Package-Release.py')
        self.assertIn('Logic/Siemens/TiaVersionCatalog.cs', package)
        project = ET.parse(ROOT / 'tests/Engine/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj')
        linked = [e.get('Include', '').replace('\\', '/') for e in project.iter('Compile')]
        references = [e.get('Include', '').replace('\\', '/') for e in project.iter('ProjectReference')]
        self.assertTrue(any(p.endswith('/Logic/TiaMcp.Logic.csproj') for p in references))
        for suffix in ['Siemens/TiaVersionCatalog.cs', 'Siemens/Capability.cs', 'CliOptions.cs']:
            self.assertFalse(any(p.endswith(suffix) for p in linked), suffix)
            self.assertTrue((LOGIC / suffix).is_file(), suffix)
        self.assertIn('CheckSuite.Run(nameof(TiaVersionCatalogTests), TiaVersionCatalogTests.Run)',
                      read(ROOT / 'tests/Engine/TiaMcpServer.Tests/OfflineChecks.cs'))


if __name__ == '__main__':
    print('Static source-contract checks; C#/WPF/native execution is not covered.', flush=True)
    unittest.main()
