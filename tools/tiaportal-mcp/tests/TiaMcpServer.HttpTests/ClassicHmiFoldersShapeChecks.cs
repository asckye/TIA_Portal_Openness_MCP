using System;
using System.IO;
using System.Linq;
using System.Reflection;

// Native members used by the 2.7.37 phase 5 classic WinCC folder hierarchy (Portal.ClassicHmiFolders.cs and the typed VB-script /
// library-type / value-type retrofits), verified member by member against the installed V20 (Siemens.Engineering) or V21
// (Siemens.Engineering.Base / Siemens.Engineering.WinCC / Siemens.Engineering.WinCC.Extension) PublicAPI.
internal static class ClassicHmiFoldersShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(FileNotFoundException) { return Assembly.Load(legacy); } }
        var wincc=Api("Siemens.Engineering.WinCC","Siemens.Engineering");
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        bool v20=core.GetName().Version!.Major==20;
        Type T(Assembly a,string n)=>a.GetType(n,true)!;
        void Method(Assembly a,string t,string name,Type[] signature,string? returns=null)
        {
            var m=T(a,t).GetMethod(name,signature);
            check(m!=null && (returns==null || m.ReturnType.Name==returns),t+"."+name+"("+string.Join(",",signature.Select(x=>x.Name))+")"+(returns==null?"":" -> "+returns));
        }
        void Property(Assembly a,string t,string name,string? type=null,bool? writable=null)
        {
            var p=T(a,t).GetProperty(name);
            check(p!=null && (type==null || p.PropertyType.Name==type) && (writable==null || p.CanWrite==writable),t+"."+name+(type==null?"":" : "+type)+(writable==null?"":writable.Value?" (writable)":" (read-only)"));
        }
        void Enum(Assembly a,string t,params string[] names)
        {
            var type=T(a,t); var missing=names.Where(n=>!System.Enum.IsDefined(type,n)).ToArray();
            check(type.IsEnum && missing.Length==0,t+" defines "+string.Join("/",names)+(missing.Length==0?"":" (missing "+string.Join(",",missing)+")"));
        }
        var engineeringService=T(core,"Siemens.Engineering.IEngineeringService");
        const string hmi="Siemens.Engineering.Hmi.", screen="Siemens.Engineering.Hmi.Screen.", tag="Siemens.Engineering.Hmi.Tag.", script="Siemens.Engineering.Hmi.RuntimeScripting.", glob="Siemens.Engineering.Hmi.Globalization.";
        var text=typeof(string); var file=typeof(FileInfo); var exportOptions=T(core,"Siemens.Engineering.ExportOptions"); var importOptions=T(core,"Siemens.Engineering.ImportOptions");

        // ---- HmiTarget roots ----
        foreach(var p in new[]{("ScreenFolder","ScreenSystemFolder"),("ScreenPopupFolder","ScreenPopupSystemFolder"),("ScreenSlideinFolder","ScreenSlideinSystemFolder"),("ScreenTemplateFolder","ScreenTemplateSystemFolder"),("TagFolder","TagSystemFolder"),("VBScriptFolder","VBScriptSystemFolder"),("ScreenOverview","ScreenOverview"),("ScreenGlobalElements","ScreenGlobalElements")}) Property(wincc,hmi+"HmiTarget",p.Item1,p.Item2);
        Method(wincc,hmi+"HmiTarget","ImportScreenOverview",new[]{file,importOptions},"Void"); Method(wincc,hmi+"HmiTarget","ImportScreenGlobalElements",new[]{file,importOptions},"Void");

        // ---- screen folders ----
        check(T(wincc,screen+"ScreenSystemFolder").IsSubclassOf(T(wincc,screen+"ScreenFolder")) && T(wincc,screen+"ScreenUserFolder").IsSubclassOf(T(wincc,screen+"ScreenFolder")),"ScreenSystemFolder / ScreenUserFolder derive from ScreenFolder");
        Property(wincc,screen+"ScreenFolder","Name","String"); Property(wincc,screen+"ScreenFolder","Screens","ScreenComposition"); Property(wincc,screen+"ScreenFolder","Folders","ScreenUserFolderComposition");
        Method(wincc,screen+"ScreenUserFolder","Delete",Type.EmptyTypes,"Void"); Method(wincc,screen+"ScreenUserFolderComposition","Create",new[]{text},"ScreenUserFolder"); Method(wincc,screen+"ScreenUserFolderComposition","Find",new[]{text},"ScreenUserFolder");
        // ---- pop-up screens ----
        check(T(wincc,screen+"ScreenPopupSystemFolder").IsSubclassOf(T(wincc,screen+"ScreenPopupFolder")) && T(wincc,screen+"ScreenPopupUserFolder").IsSubclassOf(T(wincc,screen+"ScreenPopupFolder")),"pop-up folders derive from ScreenPopupFolder");
        Property(wincc,screen+"ScreenPopupFolder","Name","String"); Property(wincc,screen+"ScreenPopupFolder","ScreenPopups","ScreenPopupComposition"); Property(wincc,screen+"ScreenPopupFolder","Folders","ScreenPopupUserFolderComposition");
        Method(wincc,screen+"ScreenPopupUserFolder","Delete",Type.EmptyTypes,"Void"); Method(wincc,screen+"ScreenPopupUserFolderComposition","Create",new[]{text},"ScreenPopupUserFolder"); Method(wincc,screen+"ScreenPopupUserFolderComposition","Find",new[]{text},"ScreenPopupUserFolder");
        Property(wincc,screen+"ScreenPopup","Name","String"); Method(wincc,screen+"ScreenPopup","Export",new[]{file,exportOptions},"Void"); Method(wincc,screen+"ScreenPopup","Delete",Type.EmptyTypes,"Void");
        Method(wincc,screen+"ScreenPopupComposition","Find",new[]{text},"ScreenPopup"); Method(wincc,screen+"ScreenPopupComposition","Import",new[]{file,importOptions});
        // ---- screen templates ----
        check(T(wincc,screen+"ScreenTemplateSystemFolder").IsSubclassOf(T(wincc,screen+"ScreenTemplateFolder")) && T(wincc,screen+"ScreenTemplateUserFolder").IsSubclassOf(T(wincc,screen+"ScreenTemplateFolder")),"template folders derive from ScreenTemplateFolder");
        Property(wincc,screen+"ScreenTemplateFolder","Name","String"); Property(wincc,screen+"ScreenTemplateFolder","ScreenTemplates","ScreenTemplateComposition"); Property(wincc,screen+"ScreenTemplateFolder","Folders","ScreenTemplateUserFolderComposition");
        Method(wincc,screen+"ScreenTemplateUserFolder","Delete",Type.EmptyTypes,"Void"); Method(wincc,screen+"ScreenTemplateUserFolderComposition","Create",new[]{text},"ScreenTemplateUserFolder"); Method(wincc,screen+"ScreenTemplateUserFolderComposition","Find",new[]{text},"ScreenTemplateUserFolder");
        Property(wincc,screen+"ScreenTemplate","Name","String"); Method(wincc,screen+"ScreenTemplate","Export",new[]{file,exportOptions},"Void"); Method(wincc,screen+"ScreenTemplate","Delete",Type.EmptyTypes,"Void");
        Method(wincc,screen+"ScreenTemplateComposition","Find",new[]{text},"ScreenTemplate"); Method(wincc,screen+"ScreenTemplateComposition","Import",new[]{file,importOptions});
        // ---- slide-ins / overview / global elements ----
        Property(wincc,screen+"ScreenSlideinSystemFolder","ScreenSlideins","ScreenSlideinComposition"); Property(wincc,screen+"ScreenSlidein","SlideinType","SlideinType"); Method(wincc,screen+"ScreenSlidein","Export",new[]{file,exportOptions},"Void");
        check(T(wincc,screen+"ScreenSlidein").GetMethod("Delete",Type.EmptyTypes)==null,"ScreenSlidein has no Delete (tool refuses slide-in deletion)");
        Method(wincc,screen+"ScreenSlideinComposition","Find",new[]{T(wincc,screen+"SlideinType")},"ScreenSlidein"); Method(wincc,screen+"ScreenSlideinComposition","Import",new[]{file,importOptions});
        Enum(wincc,screen+"SlideinType","Top","Bottom","Left","Right");
        Method(wincc,screen+"ScreenOverview","Export",new[]{file,exportOptions},"Void"); Method(wincc,screen+"ScreenGlobalElements","Export",new[]{file,exportOptions},"Void");
        // ---- tag folders ----
        check(T(wincc,tag+"TagSystemFolder").IsSubclassOf(T(wincc,tag+"TagFolder")) && T(wincc,tag+"TagUserFolder").IsSubclassOf(T(wincc,tag+"TagFolder")),"tag folders derive from TagFolder");
        Property(wincc,tag+"TagFolder","Name","String"); Property(wincc,tag+"TagFolder","TagTables","TagTableComposition"); Property(wincc,tag+"TagFolder","Folders","TagUserFolderComposition"); Property(wincc,tag+"TagSystemFolder","DefaultTagTable","TagTable");
        Method(wincc,tag+"TagUserFolder","Delete",Type.EmptyTypes,"Void"); Method(wincc,tag+"TagUserFolderComposition","Create",new[]{text},"TagUserFolder"); Method(wincc,tag+"TagUserFolderComposition","Find",new[]{text},"TagUserFolder");
        // ---- VB scripts ----
        check(T(wincc,script+"VBScriptSystemFolder").IsSubclassOf(T(wincc,script+"VBScriptFolder")) && T(wincc,script+"VBScriptUserFolder").IsSubclassOf(T(wincc,script+"VBScriptFolder")),"script folders derive from VBScriptFolder");
        Property(wincc,script+"VBScriptFolder","Name","String"); Property(wincc,script+"VBScriptFolder","VBScripts","VBScriptComposition"); Property(wincc,script+"VBScriptFolder","Folders","VBScriptUserFolderComposition");
        Method(wincc,script+"VBScriptUserFolder","Delete",Type.EmptyTypes,"Void"); Method(wincc,script+"VBScriptUserFolderComposition","Create",new[]{text},"VBScriptUserFolder"); Method(wincc,script+"VBScriptUserFolderComposition","Find",new[]{text},"VBScriptUserFolder");
        Property(wincc,script+"VBScript","Name","String"); Method(wincc,script+"VBScript","Export",new[]{file,exportOptions},"Void"); Method(wincc,script+"VBScript","Delete",Type.EmptyTypes,"Void"); Method(wincc,script+"VBScriptComposition","Find",new[]{text},"VBScript");
        // ---- multilingual graphics (V21) ----
        if(v20) { Console.WriteLine("CAPABILITY GraphicsProvider absent on V20 (ManageClassicHmiGraphic answers NotSupported there)"); check(wincc.GetType(glob+"GraphicsProvider")==null,"V20 has no GraphicsProvider"); }
        else
        {
            check(engineeringService.IsAssignableFrom(T(wincc,glob+"GraphicsProvider")),"GraphicsProvider is an IEngineeringService"); Property(wincc,glob+"GraphicsProvider","Graphics","MultiLingualGraphicComposition");
            Property(wincc,glob+"MultiLingualGraphic","Name","String"); Method(wincc,glob+"MultiLingualGraphic","Export",new[]{file,exportOptions},"Void"); Method(wincc,glob+"MultiLingualGraphic","Delete",Type.EmptyTypes,"Void");
            Method(wincc,glob+"MultiLingualGraphicComposition","Find",new[]{text},"MultiLingualGraphic"); Method(wincc,glob+"MultiLingualGraphicComposition","Import",new[]{file,importOptions});
        }
        // ---- classic library type subclasses ----
        foreach(var t in new[]{screen+"ScreenLibraryType",screen+"StyleLibraryType",screen+"StyleSheetLibraryType",tag+"HmiUdtLibraryType"}) { check(T(wincc,t).IsSubclassOf(T(core,"Siemens.Engineering.Library.Types.LibraryType")),t+" derives from LibraryType"); Property(wincc,t,"Name","String"); }
        // ---- WinCC.Extension value types (V21) ----
        if(v20) Console.WriteLine("CAPABILITY Siemens.Engineering.Hmi.ConstValue / NullableDateTime absent on V20 (value renderer returns null)");
        else
        {
            Assembly ext; try { ext=Assembly.Load("Siemens.Engineering.WinCC.Extension"); } catch(FileNotFoundException) { ext=wincc; }
            Property(ext,hmi+"ConstValue","Value","Object",true); check(T(ext,hmi+"ConstValue").GetConstructor(new[]{typeof(object)})!=null,"ConstValue(object) constructor");
            foreach(var p in new[]{"Year","Month","Day","Hour","Minute","Second"}) { var np=T(ext,hmi+"NullableDateTime").GetProperty(p); check(np!=null && np.PropertyType==typeof(int?) && np.CanWrite,hmi+"NullableDateTime."+p+" : Nullable<Int32> (writable)"); }
            Property(ext,hmi+"NullableDateTime","DateTimeValues","DateTimeValues"); Enum(ext,hmi+"DateTimeValues","None","Year","Month","Day","Hour","Minute","Second");
            check(T(ext,hmi+"NullableDateTime").GetConstructor(new[]{typeof(DateTime)})!=null && T(ext,hmi+"NullableDateTime").GetConstructor(new[]{text})!=null,"NullableDateTime(DateTime) / (string) constructors");
        }
    }
}
