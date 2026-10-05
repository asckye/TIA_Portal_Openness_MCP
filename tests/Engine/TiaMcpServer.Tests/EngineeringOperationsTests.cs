using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    internal static class EngineeringOperationsTests
    {
        public sealed class Groups : List<Group>
        {
            public Group Create(string name) { var g = new Group { Name = name, Owner = this }; Add(g); return g; }
        }
        public sealed class Group
        {
            public string Name { get; set; } = "";
            public Groups Groups { get; } = new Groups();
            public List<string> Types { get; } = new List<string>();
            public Groups? Owner;
            public void Delete() => Owner!.Remove(this);
        }
        public enum Mode { One, Two }
        public sealed class Settings
        {
            public int Writes;
            private int number;
            public int Number { get => number; set { Writes++; number = value; } }
            public Mode Mode { get; set; }
            public object Complex { get; set; } = new object();
            public string ReadOnly => "fixed";
            public string Broken => throw new InvalidOperationException("getter failed");
            public int Coerced { get => 0; set { Writes++; } }
        }
        internal static void Run(Action<bool,string> check)
        {
            var root = new Group();
            JsonObject Op(string path, string action, string name = "", bool preview = true) => EngineeringGroupOperations.Manage(root,path,action,name,preview,"Types");
            bool Fails(Action action) { try { action(); return false; } catch { return true; } }
            Op("A/B","create"); check(root.Groups.Count == 0,"group preview never creates parents");
            Op("A/B","create",preview:false); check(root.Groups[0].Groups.Single().Name == "B","nested group creation");
            Op("A/B","rename","C"); check(root.Groups[0].Groups[0].Name == "B","rename preview does not write");
            Op("A/B","rename","C",false); check(root.Groups[0].Groups[0].Name == "C","rename readback");
            root.Groups[0].Groups.Create("D");
            check(Fails(()=>Op("A/C","rename","D",false)),"rename rejects collision");
            check(Fails(()=>Op("A","deleteEmpty",preview:false)),"group deletion rejects subgroups");
            root.Groups[0].Groups[0].Types.Add("UDT");
            check(Fails(()=>Op("A/C","deleteEmpty",preview:false)),"type group deletion rejects contained UDT");
            root.Groups[0].Groups[0].Types.Clear();
            Op("A/C","deleteEmpty"); check(root.Groups[0].Groups.Count==2,"delete preview preserves group");
            Op("A/C","deleteEmpty",preview:false); check(root.Groups[0].Groups.Count==1,"delete verifies absence");
            check(Fails(()=>Op("","deleteEmpty",preview:false)),"root group deletion refused");
            Op("PLC data types/Child","create",preview:false);
            check(root.Groups.Any(x=>x.Name=="PLC data types"),"generic group names do not lose type-root-looking prefix");
            root.Groups.Create("A"); check(Fails(()=>Op("A/X","create",preview:false)),"ambiguous parents refused");
            check(Fails(()=>Op("A","rename","../X",false)),"rename rejects path traversal");
            var settings = new Settings();
            check(Fails(()=>EngineeringScalarProperties.Prepare(typeof(Settings),new JsonObject { ["Number"]=2,["ReadOnly"]="x" })) && settings.Writes==0,"all property conversions validate before writing");
            check(Fails(()=>EngineeringScalarProperties.Prepare(typeof(Settings),new JsonObject { ["Mode"]="999" })),"undefined enum rejected");
            check(Fails(()=>EngineeringScalarProperties.Prepare(typeof(Settings),new JsonObject { ["Number"]=2147483648L })),"numeric overflow rejected");
            var prepared=EngineeringScalarProperties.Prepare(typeof(Settings),new JsonObject { ["Number"]=7,["Mode"]="Two" });
            var meta=new JsonObject(); EngineeringScalarProperties.Apply(settings,prepared,meta);
            check(settings.Number==7 && settings.Mode==Mode.Two && meta["appliedProperties"]!.AsArray().Count==2,"scalar patch applies and reads back");
            meta=new JsonObject();
            check(Fails(()=>EngineeringScalarProperties.Apply(settings,EngineeringScalarProperties.Prepare(typeof(Settings),new JsonObject { ["Number"]=8,["Coerced"]=3 }),meta)) && settings.Number==8 && meta["mayHaveChanged"]!.GetValue<bool>(),"partial edits and coercion failure remain explicit");
            var read=EngineeringScalarProperties.Read(settings);
            check(!read["dataComplete"]!.GetValue<bool>() && read["failures"]!.AsArray().Count==1,"getter failure cannot report complete data");
            check(read["excludedComplexProperties"]!.AsArray().Any(x=>x!.ToString()=="Complex"),"complex fields are explicitly excluded");
            string file=Path.GetTempFileName();
            try
            {
                void Xml(string value) => File.WriteAllText(file,value);
                Xml("<Document><SW.WatchAndForceTables.PlcWatchTable ID='1'/></Document>");
                check(WatchTableImportValidation.Validate(file)==1,"native watch namespace is accepted despite WatchAndForceTables name");
                Xml("<Document><SW.WatchAndForceTables.PlcForceTable ID='1'/></Document>");
                check(Fails(()=>WatchTableImportValidation.Validate(file)),"force table import refused");
                Xml("<Document><SW.WatchAndForceTables.PlcWatchTable ID='1'/><SW.Blocks.FB ID='2'/></Document>");
                check(Fails(()=>WatchTableImportValidation.Validate(file)),"mixed object import refused");
                Xml("<!DOCTYPE Document [<!ENTITY x 'unsafe'>]><Document><SW.WatchAndForceTables.PlcWatchTable ID='1'/></Document>");
                check(Fails(()=>WatchTableImportValidation.Validate(file)),"DTD rejected");
            }
            finally { File.Delete(file); }
        }
    }
}
