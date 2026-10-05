"""Compile and execute the actual dispatch method bodies against managed fake API objects.

No Siemens assembly or TIA process is used. System-group and user-group success paths
exercise the API inheritance that a compile-only check cannot validate.
"""
from pathlib import Path
import argparse
import subprocess
from engine_sources import EngineSources

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--work-dir', type=Path, required=True)
    args = parser.parse_args()
    sources = EngineSources()
    methods = '\n'.join(sources.member(name, signature='public void') for name in
                        ('ImportPlcExternalSource', 'GenerateBlocksFromExternalSource'))
    methods += '\n' + sources.member('ExternalSourceNameMatches')
    primitive_source = (ROOT / 'src/Adapters/Native/Plc/PlcDocumentPrimitives.cs').read_text(encoding='utf-8')
    declarations = ('Software(SoftwareContainer container)', 'Sources(PlcExternalSourceGroup group)',
                    'CreateFromFile(PlcExternalSourceComposition sources, string name, string path)',
                    'Generate(PlcExternalSource source)')
    primitive_methods = []
    for declaration in declarations:
        matches = [line for line in primitive_source.splitlines() if declaration in line and 'public static' in line]
        if len(matches) != 1:
            raise ValueError('Missing or ambiguous dispatch primitive: ' + declaration)
        primitive_methods.append(matches[0])
    code = r'''
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Documents = TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives;
using Siemens.Engineering.SW.ExternalSources;

namespace TiaMcp.Adapters.Native.Plc {
    public static class PlcDocumentPrimitives {
'''+ '\n'.join(primitive_methods) +r'''
    }
}
namespace Siemens.Engineering.HW { public abstract class Software { } }
namespace Siemens.Engineering.HW.Features {
    public sealed class SoftwareContainer { public Software Software { get; } = new PlcSoftware(); }
}
namespace Siemens.Engineering.SW.ExternalSources {
    public abstract class PlcExternalSourceGroup { public PlcExternalSourceComposition ExternalSources { get; } = new(); }
    public sealed class PlcExternalSourceSystemGroup : PlcExternalSourceGroup { }
    public sealed class PlcExternalSourceUserGroup : PlcExternalSourceGroup { }
    public sealed class PlcExternalSourceComposition : List<PlcExternalSource> {
        public int Calls; public bool Fail; public bool ReturnNull; public string? Name, Path;
        public PlcExternalSource? CreateFromFile(string name, string path) {
            Calls++; Name=name; Path=path;
            if(Fail) throw new IOException("native fake failed after dispatch");
            if(ReturnNull) return null;
            var source = new PlcExternalSource { Name=name }; Add(source); return source;
        }
    }
    public sealed class PlcExternalSource {
        public string Name { get; set; } = ""; public int Calls; public bool Fail;
        public void GenerateBlocksFromSource() { Calls++; if(Fail) throw new IOException("generation failed"); }
    }
}
namespace Siemens.Engineering.SW {
    public sealed class PlcSoftware : Software { public PlcExternalSourceSystemGroup ExternalSourceGroup { get; } = new(); public PlcExternalSourceUserGroup UserGroup { get; } = new(); }
}
public enum PortalErrorCode { InvalidState, NotFound, InvalidParams, OpennessError }
public sealed class PortalException : Exception { public PortalException(PortalErrorCode code,string text,Exception? inner=null):base(text,inner){} }
public sealed class Portal {
    public SoftwareContainer Container { get; } = new();
    private Portal _session => this;
    private bool IsProjectNull()=>false;
    private SoftwareContainer GetSoftwareContainer(string path)=>Container;
    private object TryGetExternalSourceGroupByPath(PlcSoftware plc,string path)=>path=="sub" ? plc.UserGroup : plc.ExternalSourceGroup;
    private static IEnumerable<object> TryGetExternalSourcesCollection(PlcSoftware plc)=>plc.ExternalSourceGroup.ExternalSources;
'''+methods+r'''
}
internal static class Program {
    private static int checks;
    private static void Check(bool value,string label) { if(!value) throw new Exception(label); checks++; }
    private static void Fails(Action action) { try { action(); } catch(PortalException) { checks++;return; } throw new Exception("Expected reported failure"); }
    private static int Main() {
        try { RunChecks(); return 0; }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void RunChecks() {
        var path=Path.Combine(AppContext.BaseDirectory,"source.scl"); File.WriteAllText(path,"FUNCTION Test : Void\nBEGIN\nEND_FUNCTION");
        var p=new Portal(); var plc=(PlcSoftware)p.Container.Software;
        p.ImportPlcExternalSource("PLC", "", path);
        Check(plc.ExternalSourceGroup.ExternalSources.Calls==1,"system group imports once");
        Check(plc.ExternalSourceGroup.ExternalSources.Name=="source.scl","source name argument");
        Check(plc.ExternalSourceGroup.ExternalSources.Path==path,"absolute file argument");
        p.ImportPlcExternalSource("PLC", "sub", path);
        Check(plc.UserGroup.ExternalSources.Calls==1,"user group imports once");
        p.GenerateBlocksFromExternalSource("PLC","source.scl");
        Check(plc.ExternalSourceGroup.ExternalSources[0].Calls==1,"generate normal path");
        plc.UserGroup.ExternalSources.Fail=true;
        Fails(()=>p.ImportPlcExternalSource("PLC","sub",path));
        Check(plc.UserGroup.ExternalSources.Calls==2,"failed import not replayed");
        plc.UserGroup.ExternalSources.Fail=false; plc.UserGroup.ExternalSources.ReturnNull=true;
        Fails(()=>p.ImportPlcExternalSource("PLC","sub",path));
        Check(plc.UserGroup.ExternalSources.Calls==3,"null outcome not replayed");
        plc.ExternalSourceGroup.ExternalSources[0].Fail=true;
        Fails(()=>p.GenerateBlocksFromExternalSource("PLC","source.scl"));
        Check(plc.ExternalSourceGroup.ExternalSources[0].Calls==2,"failed generation not replayed");
        Console.WriteLine($"External-source actual method bodies: {checks} passed; managed fakes only.");
    }
}
'''
    args.work_dir.mkdir(parents=True, exist_ok=True)
    (args.work_dir/'Program.cs').write_text(code, encoding='utf-8')
    project=args.work_dir/'DispatchChecks.csproj'
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>',encoding='utf-8')
    config=args.work_dir/'NuGet.Config'
    config.write_text('<configuration><packageSources><clear /></packageSources></configuration>',encoding='utf-8')
    subprocess.run(['dotnet','restore',str(project),'--configfile',str(config)],check=True)
    subprocess.run(['dotnet','run','--project',str(project),'-c','Release','--no-restore'],check=True)


if __name__ == '__main__':
    main()
