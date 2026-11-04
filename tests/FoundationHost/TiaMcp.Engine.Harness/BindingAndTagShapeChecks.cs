using System;
using System.IO;
using System.Reflection;

internal static class BindingAndTagShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string modern, string legacy) { try { return Assembly.Load(modern); } catch (FileNotFoundException) { return Assembly.Load(legacy); } }
        var core = Api("Siemens.Engineering.Base", "Siemens.Engineering");
        var classic = Api("Siemens.Engineering.WinCC", "Siemens.Engineering");
        var unified = Api("Siemens.Engineering.WinCCUnified", "Siemens.Engineering");
        foreach (var type in new[] { "Siemens.Engineering.ProjectBase", "Siemens.Engineering.TiaPortalProcess" })
            check(core.GetType(type, true)!.GetProperty(type.EndsWith("ProjectBase") ? "Path" : "ProjectPath")?.PropertyType == typeof(FileInfo), type + " exposes full file path");
        foreach (var pair in new[] { (classic, "Siemens.Engineering.Hmi.Tag.Tag"), (unified, "Siemens.Engineering.HmiUnified.HmiTags.HmiTag") })
            check(pair.Item1.GetType(pair.Item2, true)!.GetMethod("Delete", Type.EmptyTypes)?.ReturnType == typeof(void), pair.Item2 + ".Delete() available");
        var tool = EngineSurface.For(server).Tool("DeleteHmiTag")!;
        check((bool)tool.GetParameters()[3].DefaultValue && !(bool)tool.GetParameters()[4].DefaultValue, "HMI tag deletion defaults to preview without confirmation");
    }
}
