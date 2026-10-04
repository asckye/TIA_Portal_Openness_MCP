"""Compile synthetic IL and prove callback/guard handling without native execution."""
import importlib.util
from pathlib import Path
import subprocess
import unittest

spec = importlib.util.spec_from_file_location('proof', Path(__file__).with_name('Compare-SharedNativePaths.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)

SOURCE = r'''
using System;
using System.Linq;
namespace Fixture;
internal interface IClosed { string Read(int value); }
public interface IOpen { string Read(int value); }
internal interface IAmbiguous { string Read(int value); }
internal sealed class Implementation : IClosed {
    string IClosed.Read(int value) => Native.AccessName(null);
    public string Read(int value) => Native.AccessOther(null);
}
internal sealed class First : IAmbiguous { public string Read(int value) => Native.AccessName(null); }
internal sealed class Second : IAmbiguous { public string Read(int value) => Native.AccessOther(null); }
internal class VirtualBase { public virtual string Read(int value) => Native.AccessOther(null); }
internal sealed class Virtual : VirtualBase { public override string Read(int value) => Native.AccessName(null); }
internal class HiddenSlot : VirtualBase { public new virtual string Read(int value) => Native.AccessName(null); }
internal sealed class HiddenOverride : HiddenSlot { public override string Read(int value) => Native.AccessName(null); }
internal struct Pair { public int First { get; set; } public int Second { get; set; } }
internal sealed class Receiver { public Pair Payload; public string Read() => Native.AccessName(this); }
internal static class Native {
    public static string AccessName(Receiver receiver) => "value";
    public static string AccessOther(Receiver receiver) => "other";
    public static int AccessState(Receiver receiver) => 1;
}
internal static class Effects { public static void Touch() {} }
internal static class HelperClass {
    public static string Use(Func<string> callback) => callback();
    public static string Optional(Func<string> callback) => callback == null ? null : callback();
    public static System.Collections.Generic.IEnumerable<string> Iterate() { yield return Native.AccessName(null); }
    public static string Default(Func<string> callback = null) { callback ??= () => Native.AccessName(null); return callback(); }
}
internal static class Primitive {
    public static string Read(Receiver r) => r == null ? null : Native.AccessName(r);
    public static string Dropped(Receiver r) => Native.AccessName(r);
    public static string Changed(Receiver r) => r == null ? "changed" : Native.AccessName(r);
    public static string Effect(Receiver r) { if (r == null) return null; Effects.Touch(); return Native.AccessName(r); }
    public static string EffectStruct(Receiver r) { if (r == null) return null; r.Payload = default; return Native.AccessName(r); }
    public static string Forward(Receiver r) => Native.AccessName(r);
    public static string ForwardGuard(Receiver r) => r == null ? null : Forward(r);
    public static string ForwardChanged(Receiver r) => r == null ? "changed" : Forward(r);
    public static string ForwardEffect(Receiver r) { if (r == null) return null; Effects.Touch(); return Forward(r); }
    public static int? State(Receiver r) => r == null ? (int?)null : Native.AccessState(r);
    public static int? WrongState(Receiver r) => r == null ? (int?)42 : Native.AccessState(r);
    public static int? ZeroState(Receiver r) => r == null ? (int?)0 : Native.AccessState(r);
}
internal static class Host {
    public static object Group(IClosed receiver, int[] items) => items.Select(receiver.Read);
    public static object Lambda(Receiver[] items) => items.Select(x => x.Read());
    public static object VirtualGroup(Virtual receiver, int[] items) => items.Select(receiver.Read);
    public static object VirtualUnknown(VirtualBase receiver, int[] items) => items.Select(receiver.Read);
    public static object BaseSlot(HiddenOverride receiver, int[] items) => items.Select(((VirtualBase)receiver).Read);
    public static object OpenGroup(IOpen receiver, int[] items) => items.Select(receiver.Read);
    public static object Ambiguous(IAmbiguous receiver, int[] items) => items.Select(receiver.Read);
    public static object UnknownLambda(IOpen[] items) => items.Select(x => x.Read(1));
    public static string Helper(Func<string> callback) => callback();
    public static string HelperUse(Receiver r) => Helper(r.Read);
    public static string ExternalHelperUse(Receiver r) => HelperClass.Use(r.Read);
    public static string IteratorHelperUse() => HelperClass.Use(() => HelperClass.Iterate().First());
    public static string DefaultHelperUse() => HelperClass.Default();
    public static string OptionalHelperUse(Receiver r, bool enabled) => HelperClass.Optional(enabled ? r.Read : null);
    public static string UnguardedOptional(Receiver r, bool enabled) => HelperClass.Use(enabled ? r.Read : null);
    public static string AmbiguousInvoke(bool choose) { Func<string> action = choose ? () => Native.AccessName(null) : () => Native.AccessOther(null); return action(); }
    public static string OtherHelperUse(Receiver r) => Helper(() => Native.AccessOther(r));
    public static object Unknown(Func<int,string> callback, int[] items) => items.Select(callback);
    public static object Stale(Func<int,string> unknown, int[] items) {
        var first = items.Select(x => Native.AccessName(null));
        return items.Select(unknown);
    }
    public static object Multiple(int[] items) => items.Aggregate("", (s, i) => Native.AccessName(null), s => Native.AccessOther(null));
    public static string Before(Receiver r) => r == null ? null : Native.AccessName(r);
    public static string After(Receiver r) => Primitive.Read(r);
    public static string Dropped(Receiver r) => Primitive.Dropped(r);
    public static string Changed(Receiver r) => Primitive.Changed(r);
    public static string Effect(Receiver r) => Primitive.Effect(r);
    public static string EffectStruct(Receiver r) => Primitive.EffectStruct(r);
    public static string ForwardAfter(Receiver r) => Primitive.ForwardGuard(r);
    public static string ForwardChanged(Receiver r) => Primitive.ForwardChanged(r);
    public static string ForwardEffect(Receiver r) => Primitive.ForwardEffect(r);
    public static string ChainBefore(Receiver r) => r == null ? null : Native.AccessName(r).ToString();
    public static string ChainAfter(Receiver r) { var value = Primitive.Read(r); return r == null ? null : value.ToString(); }
    public static string ChainChanged(Receiver r) { var value = Primitive.Read(r); return r == null ? "changed" : value.ToString(); }
    public static string WrongGuard(Receiver r, Receiver other) => r == null ? null : Native.AccessName(other);
    public static string WrongReceiver(Receiver r, Receiver other) { var value = Primitive.Read(r); return other == null ? null : value.ToString(); }
    public static string BetweenEffect(Receiver r) { var value = Primitive.Read(r); Effects.Touch(); return r == null ? null : value.ToString(); }
    public static string StateBefore(Receiver r) => r == null ? null : Native.AccessState(r).ToString();
    public static string StateAfter(Receiver r) => Primitive.State(r)?.ToString();
    public static string StateChanged(Receiver r) => Primitive.WrongState(r)?.ToString();
    public static int? DirectStateBefore(Receiver r) => r == null ? (int?)null : Native.AccessState(r);
    public static int? DirectStateAfter(Receiver r) => Primitive.State(r);
    public static int? DirectStateZero(Receiver r) => Primitive.ZeroState(r);
}
'''


class ReaderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = proof.ROOT / 'bin-build/shared-native-reader-self-test'
        cls.directory.mkdir(parents=True, exist_ok=True)
        (cls.directory / 'Fixture.cs').write_text(SOURCE, encoding='utf-8', newline='\n')
        (cls.directory / 'Fixture.csproj').write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
            '<Optimize>true</Optimize><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>', encoding='utf-8')
        subprocess.run(['dotnet', 'build', str(cls.directory / 'Fixture.csproj'), '-c', 'Release', '-v:q',
                        '-m:1', '-nr:false', '-p:RestoreSources=' + str(cls.directory)], check=True)
        config = proof.SelfTests.config('fixture')
        config['engine']['types'] = ['Fixture.Host']
        config['primitives'] = [dict(adapterType='Fixture.Primitive', engineNamespace='Fixture.Local')]
        config['tools'][0]['entryMethods'][0]['type'] = 'Fixture.Host'
        proof.write(cls.directory / 'config.json', config)
        proof.write(cls.directory / 'inventory.json', dict(sites=[
            dict(wrapper=name, member=member, category=category, opcode=opcode)
            for name, member, category, opcode in [
                ('AccessName', 'System.String Siemens.Receiver::get_Name()', 'direct', 'callvirt'),
                ('AccessOther', 'System.String Siemens.Receiver::get_Other()', 'direct', 'callvirt'),
                ('AccessState', 'System.Int32 Siemens.Receiver::get_State()', 'direct', 'callvirt'),
                ('ToString', 'System.String System.Object::ToString()', 'object-dispatch', 'callvirt')]]))
        doc = proof.read(proof.dump(cls.directory / 'bin/Release/net10.0/Fixture.dll',
            cls.directory / 'inventory.json', cls.directory, cls.directory / 'config.json'))
        cls.paths = proof.Paths([doc])

    def graph(self, name):
        key = next(key for key, method in self.paths.methods.items()
                   if method['owner'] == 'Fixture.Host' and proof.method_name(key) == name)
        return self.paths.build(key)

    def test_internal_interface_method_group(self):
        graph = str(self.graph('Group'))
        self.assertIn('get_Name', graph)
        self.assertNotIn('get_Other', graph)

    def test_sealed_virtual_method_group(self):
        self.assertIn('get_Name', str(self.graph('VirtualGroup')))
        self.assertIn('get_Other', str(self.graph('BaseSlot')))
        self.assertNotIn('get_Name', str(self.graph('BaseSlot')))

    def test_lambda_receiver_method(self):
        self.assertIn('get_Name', str(self.graph('Lambda')))

    def test_callback_passed_to_host_helper(self):
        self.assertIn('get_Name', str(self.graph('HelperUse')))
        self.assertEqual(self.graph('HelperUse'), self.graph('ExternalHelperUse'))
        self.assertIn('get_Name', str(self.graph('IteratorHelperUse')))
        self.assertIn('get_Name', str(self.graph('DefaultHelperUse')))
        self.assertIn('get_Name', str(self.graph('OptionalHelperUse')))
        self.assertNotEqual(self.graph('HelperUse'), self.graph('OtherHelperUse'))

    def test_multiple_callback_parameters(self):
        graph = str(self.graph('Multiple'))
        self.assertIn('get_Name', graph)
        self.assertIn('get_Other', graph)

    def test_unresolved_callbacks_fail_with_call_site(self):
        for name in ('OpenGroup', 'Ambiguous', 'UnknownLambda', 'Unknown', 'Stale', 'AmbiguousInvoke', 'UnguardedOptional', 'VirtualUnknown'):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, r'Unresolved .*IL_[0-9a-f]+'):
                self.graph(name)

    def test_moved_receiver_guard(self):
        self.assertEqual(self.graph('Before'), self.graph('After'))
        self.assertEqual(self.graph('Before'), self.graph('ForwardAfter'))

    def test_guard_mutations_remain_strict(self):
        for name in ('Dropped', 'Changed', 'Effect', 'EffectStruct', 'ForwardChanged', 'ForwardEffect'):
            with self.subTest(name=name):
                self.assertNotEqual(self.graph('Before'), self.graph(name))

    def test_repeated_receiver_check(self):
        self.assertEqual(self.graph('ChainBefore'), self.graph('ChainAfter'))
        for name in ('WrongReceiver', 'BetweenEffect', 'ChainChanged'):
            with self.subTest(name=name):
                self.assertNotEqual(self.graph('ChainBefore'), self.graph(name))
        self.assertNotEqual(self.graph('Before'), self.graph('WrongGuard'))

    def test_nullable_has_value_check(self):
        self.assertEqual(self.graph('StateBefore'), self.graph('StateAfter'))
        self.assertNotEqual(self.graph('StateBefore'), self.graph('StateChanged'))
        self.assertEqual(self.graph('DirectStateBefore'), self.graph('DirectStateAfter'))
        self.assertNotEqual(self.graph('DirectStateBefore'), self.graph('DirectStateZero'))


if __name__ == '__main__':
    unittest.main(verbosity=2)
