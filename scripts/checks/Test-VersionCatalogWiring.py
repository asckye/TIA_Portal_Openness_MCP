"""Source-contract checks only: these do NOT compile or execute the C# engine."""
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / 'tools/tiaportal-mcp/src/TiaMcpServer'
LOGIC = ROOT / 'tools/tiaportal-mcp/src/TiaMcp.Logic'


def read(path):
    return path.read_text(encoding='utf-8-sig')


class VersionCatalogWiring(unittest.TestCase):
    def test_policy_wraps_direct_and_isolated_dispatch(self):
        wiring = read(SERVER / 'ModelContextProtocol/Tools/McpServer.ArgDiagnostics.cs')
        self.assertIn('WrapWithVersionPolicy(Isolation.IsolatedWorkerHost.Current', wiring)
        wrapper = read(SERVER / 'ModelContextProtocol/Tools/McpServer.VersionPolicy.cs')
        self.assertLess(wrapper.index('VersionCallProblem'), wrapper.index('return inner.InvokeAsync'))
        profile = read(SERVER / 'ModelContextProtocol/Tools/McpServer.Profile.cs')
        self.assertEqual(profile.count('VersionToolProblem(name).Length == 0'), 2)
        bridge = read(SERVER / 'ModelContextProtocol/Tools/McpServer.ToolBridge.cs')
        invoke = bridge.split('private static object? InvokeToolMethod', 1)[1]
        self.assertLess(invoke.index('VersionCallProblem'), invoke.index('method.Invoke(null, call)'))
        self.assertIn('AvailableToolMethods(_allToolMethods, includeUnavailable)', bridge)
        self.assertIn('versionAvailable', bridge)

    def test_version_exclusions_have_real_registered_names(self):
        policy = read(SERVER / 'Siemens/ToolVersionPolicy.cs').split('internal static string ToolProblem', 1)[0]
        excluded = re.findall(r'\["([^"]+)"\]\s*=', policy)
        self.assertEqual(len(excluded), 11)
        sources = '\n'.join(read(p) for p in (SERVER / 'ModelContextProtocol/Tools').glob('*.cs'))
        registered = set(re.findall(r'McpServerTool\(Name\s*=\s*"([^"]+)"', sources))
        self.assertFalse(set(excluded) - registered)
        for script in ['Test-WorkerIsolation.py', 'Test-LocalStability.py']:
            self.assertIn('477 if args.major == 20 else 488', read(ROOT / 'scripts/checks' / script))

    def test_legacy_contract_remains_unbound(self):
        source = read(SERVER / 'Siemens/OpennessReleaseContract.cs')
        self.assertIn('expectedIdentity == null', source)
        self.assertIn('EngineeringAssemblyIdentity.RequireMatch', source)
        self.assertIn('HW.ISoftwareContainer', source)
        self.assertIn('HW.Features.SoftwareContainer', source)
        self.assertIn('typeof(string) : typeof(FileInfo)', source)
        self.assertIn('"Siemens.Engineering.IEngineeringObject"', source)
        self.assertNotIn('Assembly.Load', source)

    def test_move_refusal_and_recovery_precede_cleanup(self):
        source = read(SERVER / 'Siemens/Portal/Portal.Helpers.cs').split('public string MoveBlockToGroup', 1)[1].split('private PlcTypeGroup?', 1)[0]
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
        source = read(SERVER / 'Program.cs')
        guard = source.index('TiaVersionCatalog.RequireMatchingEngine(tiaMajorVersion, EngineRouter.CompiledTiaMajorVersion)')
        self.assertLess(guard, source.index('AssemblyResolve += Engineering.Resolver'))
        self.assertLess(guard, source.index('Openness.Initialize('))
        self.assertIn('detected ?? EngineRouter.CompiledTiaMajorVersion', source)
        self.assertNotIn('assembly load will likely fail', source)

    def test_diagnostics_remain_reachable(self):
        source = read(SERVER / 'Program.cs')
        self.assertLess(source.index('CliOptions.IsInformationalCommand(args)'), source.index('CliOptions.ParseArgs(args)'))
        self.assertLess(source.index('string.Equals(args[0], "doctor"'), source.index('TiaVersionCatalog.RequireRunnable(tiaMajorVersion)'))
        doctor = read(SERVER / 'Cli/CliCommands.cs').split('private static int DoctorCli', 1)[1]
        self.assertIn('ready &= supported;', doctor)

    def test_registration_reuses_selected_version(self):
        source = read(SERVER / 'Cli/CliCommands.cs').split('private static int Config(string[] args)', 1)[1].split('string exe =', 1)[0]
        self.assertIn('Engineering.TiaMajorVersion', source)
        self.assertIn('RequireMatchingEngine', source)
        self.assertNotIn('DetectTiaMajorVersion', source)
        self.assertNotIn('Opt(args', source)

    def test_configurator_uses_keys_not_indices(self):
        source = read(ROOT / 'tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration/ConfigurationView.xaml.cs')
        selected = source.split('private string SelectedVersion', 1)[1].split('private string StatePath', 1)[0]
        self.assertIn('selected.Key', selected)
        self.assertNotIn('SelectedIndex', selected)
        self.assertIn('versions.ItemsSource = TiaVersionCatalog.Runnable', source)
        tree = ET.parse(ROOT / 'tools/tia-openness-studio/src/TiaOpenness.Gui/Configuration/ConfigurationView.xaml')
        version = next(e for e in tree.iter() if e.get('{http://schemas.microsoft.com/winfx/2006/xaml}Name') == 'Version')
        self.assertEqual(version.get('SelectedValuePath'), 'Key')
        self.assertFalse(list(version))

    def test_shared_source_build_and_package_inputs(self):
        build = read(ROOT / 'scripts/build/Build-Configurator.ps1')
        self.assertIn('dotnet run --project $configurationTests -c Release', build)
        self.assertNotIn('/main:TiaMcpConfigurator.Tests', build)
        self.assertIn('TiaOpenness.Configuration.Tests.csproj', build)
        self.assertIn('@(Get-Item $versionCatalog)', build)
        package = read(ROOT / 'scripts/build/Package-Release.py')
        self.assertIn('TiaMcp.Logic/Siemens/TiaVersionCatalog.cs', package)
        project = ET.parse(ROOT / 'tools/tiaportal-mcp/tests/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj')
        linked = [e.get('Include', '').replace('\\', '/') for e in project.iter('Compile')]
        references = [e.get('Include', '').replace('\\', '/') for e in project.iter('ProjectReference')]
        self.assertTrue(any(p.endswith('/TiaMcp.Logic/TiaMcp.Logic.csproj') for p in references))
        for suffix in ['Siemens/TiaVersionCatalog.cs', 'Siemens/Capability.cs', 'CliOptions.cs']:
            self.assertFalse(any(p.endswith(suffix) for p in linked), suffix)
            self.assertTrue((LOGIC / suffix).is_file(), suffix)
        self.assertIn('TiaVersionCatalogTests.Run(Check)', read(ROOT / 'tools/tiaportal-mcp/tests/TiaMcpServer.Tests/Program.cs'))


if __name__ == '__main__':
    print('Static source-contract checks; C#/WPF/native execution is not covered.', flush=True)
    unittest.main()
