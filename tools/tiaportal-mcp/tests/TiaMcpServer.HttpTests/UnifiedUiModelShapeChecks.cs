using System;
using System.Linq;
using System.Reflection;

// Every native type/member the UnifiedUiModel family calls through reflection, checked against the loaded official API.
internal static class UnifiedUiModelShapeChecks
{
    internal static void Run(Assembly server, Action<bool,string> check)
    {
        Assembly Api(string split,string legacy) { try { return Assembly.Load(split); } catch(System.IO.FileNotFoundException) { return Assembly.Load(legacy); } }
        var core=Api("Siemens.Engineering.Base","Siemens.Engineering");
        var unified=Api("Siemens.Engineering.WinCCUnified","Siemens.Engineering.Hmi");
        var hmi=unified.GetType("Siemens.Engineering.HmiUnified.HmiSoftware") ?? core.GetType("Siemens.Engineering.HmiUnified.HmiSoftware");
        if(hmi==null) { Console.WriteLine("CAPABILITY Unified API absent from these assemblies; UnifiedUiModel shape checks NOT PERFORMED."); return; }
        var api=hmi.Assembly; bool v20=core.GetName().Version!.Major==20;
        Type T(string n)=>api.GetType(n,true)!;
        void Method(string t,string name,Type[] signature)=>check(T(t).GetMethod(name,signature)!=null,t+"."+name+"("+string.Join(",",signature.Select(x=>x.Name))+")");
        void Property(string t,string name,bool writable=false)
        {
            var p=T(t).GetProperty(name);
            check(p?.GetMethod?.IsPublic==true && (!writable || p.SetMethod?.IsPublic==true),t+"."+name+(writable?" writable":""));
        }
        const string Ui="Siemens.Engineering.HmiUnified.UI.";
        // Events (ReadUnifiedObjectEvents)
        Property(Ui+"Screens.HmiScreen","EventHandlers"); Property(Ui+"Screens.HmiScreen","PropertyEventHandlers");
        Property(Ui+"Base.HmiScreenItemBase","PropertyEventHandlers"); Property(Ui+"Base.HmiScreenItemBase","Dynamizations");
        Property(Ui+"Events.PropertyEventHandler","EventType"); Property(Ui+"Events.PropertyEventHandler","PropertyName"); Property(Ui+"Events.PropertyEventHandler","Script");
        Method(Ui+"Events.PropertyEventHandlerComposition","Create",new[]{typeof(string),T(Ui+"Events.PropertyEventType")});
        Method(Ui+"Events.HmiScreenEventHandlerComposition","Create",new[]{T(Ui+"Enum.HmiScreenEventType")});
        Property(Ui+"Events.HmiScreenEventHandler","Script");
        check(T(Ui+"Dynamization.Script.IHmiScript").GetProperty("ScriptCode")!=null && T(Ui+"Dynamization.Script.IHmiScript").GetProperty("GlobalDefinitionAreaScriptCode")!=null && T(Ui+"Dynamization.Script.IHmiScript").GetProperty("Async")!=null,"IHmiScript script fields");
        // Parts (ManageUnifiedObjectParts)
        foreach(var named in new[]{"HmiTrendAreaPart","HmiFunctionTrendAreaPart","HmiTimeAxisPart","HmiXValueAxisPart","HmiYValueAxisPart"})
        {
            Method(Ui+"Parts."+named+"Composition","Create",new[]{typeof(string)});
            Method(Ui+"Parts."+named+"Composition","Find",new[]{typeof(string)});
            Property(Ui+"Parts."+named,"Name",true); Method(Ui+"Parts."+named,"Delete",Type.EmptyTypes);
        }
        foreach(var unnamed in new[]{"HmiTrendPart","HmiFunctionTrendPart","HmiHelpLinePart","HmiScalingEntryPart","HmiSelectionItemPart","HmiSystemDiagnosisHardwareDetailPart","HmiCustomControlInterface"})
        {
            Method(Ui+"Parts."+unnamed+"Composition","Create",Type.EmptyTypes);
            check(T(Ui+"Parts."+unnamed).GetProperty("Name")==null,unnamed+" is unnamed (partIndex addressing)");
            Method(Ui+"Parts."+unnamed,"Delete",Type.EmptyTypes);
        }
        foreach(var v21Only in new[]{"HmiPressedStateTagPart","HmiProcessDiagnosisOverviewElementPart"})
        {
            if(api.GetType(Ui+"Parts."+v21Only)==null) { Console.WriteLine("CAPABILITY "+v21Only+" absent on V"+core.GetName().Version!.Major+"; collection must surface NotSupported, not an empty success."); continue; }
            Method(Ui+"Parts."+v21Only+"Composition","Create",Type.EmptyTypes); Method(Ui+"Parts."+v21Only,"Delete",Type.EmptyTypes);
        }
        foreach(var readOnly in new[]{"HmiThresholdPart","HmiDataGridColumnPartBase"})
        {
            check(!T(Ui+"Parts."+readOnly+"Composition").GetMethods().Any(m=>m.Name=="Create"),readOnly+"Composition has no Create (TIA-created parts)");
            Method(Ui+"Parts."+readOnly+"Composition","Find",new[]{typeof(string)}); Property(Ui+"Parts."+readOnly,"Name",true); Method(Ui+"Parts."+readOnly,"Delete",Type.EmptyTypes);
        }
        if(api.GetType(Ui+"Parts.HmiAlarmLineColumnPart")==null) Console.WriteLine("CAPABILITY HmiAlarmLineColumnPart absent on this version.");
        else { Method(Ui+"Parts.HmiAlarmLineColumnPartComposition","Find",new[]{typeof(string)}); Method(Ui+"Parts.HmiAlarmLineColumnPart","Delete",Type.EmptyTypes); }
        Property(Ui+"Controls.HmiTrendControl","TrendAreas"); Property(Ui+"Parts.HmiTrendAreaPart","Trends"); Property(Ui+"Parts.HmiTrendAreaPart","BottomTimeAxes");
        Property(Ui+"Parts.HmiTrendAreaPartBase","LeftValueAxes"); Property(Ui+"Parts.HmiTrendPartBase","Thresholds"); Property(Ui+"Parts.HmiValueAxisPartBase","HelpLines"); Property(Ui+"Parts.HmiValueAxisPartBase","ScalingEntries");
        Property(Ui+"Widgets.HmiIOField","Thresholds"); Property(Ui+"Widgets.HmiSelectionGroupBase","SelectionItems"); Property(Ui+"Parts.HmiDataGridViewPart","Columns");
        // Dynamizations (ManageUnifiedDynamization)
        var dynComposition=T(Ui+"Dynamization.DynamizationBaseComposition");
        check(dynComposition.GetMethods().Any(m=>m.Name=="Create" && m.IsGenericMethodDefinition && m.GetParameters().Length==1 && m.GetParameters()[0].ParameterType==typeof(string)),"DynamizationBaseComposition.Create<T>(string)");
        Property(Ui+"Dynamization.DynamizationBase","PropertyName"); Property(Ui+"Dynamization.DynamizationBase","DynamizationType"); Method(Ui+"Dynamization.DynamizationBase","Delete",Type.EmptyTypes);
        foreach(var kind in new[]{"TagDynamization","Script.ScriptDynamization","ResourceListDynamization","Flashing.FlashingDynamization","ExpressionDynamization","TagParameterDynamization"})
            check(T(Ui+"Dynamization."+kind).IsSubclassOf(T(Ui+"Dynamization.DynamizationBase")),kind+" derives from DynamizationBase");
        Property(Ui+"Dynamization.TagDynamization","Tag",true); Property(Ui+"Dynamization.TagDynamization","ReadOnly",true); Property(Ui+"Dynamization.TagDynamization","UseIndirectAddressing",true);
        Property(Ui+"Dynamization.TagDynamization","ValueConverter"); Property(Ui+"Dynamization.TagDynamization","Address"); Property(Ui+"Dynamization.TagDynamization","PlcTag"); Property(Ui+"Dynamization.TagDynamization","DataType");
        Property(Ui+"Dynamization.ExpressionDynamization","ValueConverter");
        Property(Ui+"Dynamization.ResourceListDynamization","ResourceList",true); Property(Ui+"Dynamization.ResourceListDynamization","Tag",true);
        Property(Ui+"Dynamization.TagParameterDynamization","DynamicTagName",true);
        Property(Ui+"Dynamization.Flashing.FlashingDynamization","Color",true); Property(Ui+"Dynamization.Flashing.FlashingDynamization","AlternateColor",true);
        Property(Ui+"Dynamization.Flashing.FlashingDynamization","FlashingCondition",true); Property(Ui+"Dynamization.Flashing.FlashingDynamization","FlashingRate",true);
        check(T(Ui+"Dynamization.Flashing.FlashingDynamization").GetProperty("Color")!.PropertyType==typeof(System.Drawing.Color),"Flashing colors are System.Drawing.Color (#AARRGGBB conversion)");
        Property(Ui+"Dynamization.Script.ScriptDynamization","ScriptCode",true); Property(Ui+"Dynamization.Script.ScriptDynamization","Trigger");
        Property(Ui+"Dynamization.Script.Trigger","Type",true); Property(Ui+"Dynamization.Script.Trigger","CustomDuration",true);
        Property(Ui+"Dynamization.Tag.ValueConverter","Formula",true); Property(Ui+"Dynamization.Tag.ValueConverter","IsFormulaSelected",true); Property(Ui+"Dynamization.Tag.ValueConverter","MappingTable");
        Property(Ui+"Dynamization.Tag.MappingTable","ConditionType",true); Property(Ui+"Dynamization.Tag.MappingTable","Entries");
        check(T(Ui+"Dynamization.Tag.MappingTableEntryBaseComposition").GetMethods().Any(m=>m.Name=="Create" && m.IsGenericMethodDefinition && m.GetParameters().Length==0),"MappingTableEntryBaseComposition.Create<T>()");
        Method(Ui+"Dynamization.Tag.MappingTableEntryBase","Delete",Type.EmptyTypes);
        foreach(var field in new[]{"Value","AlternateValue","Flashing","FlashingRate"}) Property(Ui+"Dynamization.Tag.MappingTableEntryBase",field,true);
        Property(Ui+"Dynamization.Tag.MappingTableEntrySimple","Condition",true); Property(Ui+"Dynamization.Tag.MappingTableEntryRange","From",true); Property(Ui+"Dynamization.Tag.MappingTableEntryRange","To",true); Property(Ui+"Dynamization.Tag.MappingTableEntryBitmask","Condition",true);
        foreach(var e in new[]{"Dynamization.DynamizationType","Dynamization.Flashing.FlashingCondition","Dynamization.Flashing.FlashingRate","Dynamization.Script.TriggerType","Dynamization.Tag.ConditionType","Dynamization.Tag.RangeType","Dynamization.Tag.BitDynamizationType"}) check(T(Ui+e).IsEnum,e+" enum");
        // Screens (ManageUnifiedScreenLayout)
        Method(Ui+"Screens.HmiScreen","ResizeScreen",Type.EmptyTypes); Method(Ui+"Base.HmiScreenBase","Delete",Type.EmptyTypes);
        Property(Ui+"Screens.HmiScreen","Name",true); Property(Ui+"Screens.HmiScreen","DisplayName"); Property(Ui+"Screens.HmiScreen","ScreenItems");
        foreach(var field in new[]{"Width","Height","BackColor","AlternateBackColor","BackGraphic","BackFillPattern","BackgroundFillMode","ScreenNumber"}) Property(Ui+"Screens.HmiScreen",field,true);
        Method(Ui+"Screens.HmiScreenComposition","Create",new[]{typeof(string)}); Method(Ui+"Screens.HmiScreenComposition","Find",new[]{typeof(string)});
        Property(Ui+"ScreenGroup.HmiScreenGroup","Screens"); Property(Ui+"ScreenGroup.HmiScreenGroup","Groups");
        check(!T(Ui+"Screens.HmiScreen").GetMethods().Any(m=>m.Name.Contains("Copy")||m.Name.Contains("Duplicate")||m.Name.Contains("LayoutField")),"HmiScreen exposes no copy/duplicate/layout-field members (tool reports them as unsupported)");
        // Lists (ManageUnifiedListEntries)
        foreach(var list in new[]{"HmiTextList","HmiSystemTextList"})
        {
            check(!T("Siemens.Engineering.HmiUnified.TextGraphicList."+list).GetProperties().Any(p=>typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType) && p.PropertyType!=typeof(string)),list+" has no typed entry composition (entries NotSupported)");
            check(T("Siemens.Engineering.HmiUnified.TextGraphicList."+list).GetInterfaces().Any(i=>i.FullName=="Siemens.Engineering.IEngineeringObject"),list+" self-description via IEngineeringObject");
        }
        if(hmi.GetProperty("HmiGraphicLists")==null) Console.WriteLine("CAPABILITY HmiGraphicLists absent on V"+core.GetName().Version!.Major+"; graphicLists must return NotSupported.");
        else Method("Siemens.Engineering.HmiUnified.TextGraphicList.HmiGraphicListComposition","Find",new[]{typeof(string)});
        // Alarm common (ReadUnifiedAlarmCommon)
        foreach(var state in new[]{"RaisedState","AcknowledgedState","ClearedState","AcknowledgedClearedState"}) Property("Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmClass",state);
        foreach(var field in new[]{"BackColor","TextColor","Flashing"}) Property("Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon.AlarmStatusVisuals",field,true);
        foreach(var field in new[]{"AlarmClass","Origin","Area","Priority","Id","RaisedStateTag","AuditClass"}) Property("Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon.AlarmBase",field,true);
        foreach(var text in new[]{"EventText","InfoText","EventText1","EventText9"}) check(T("Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon.AlarmBase").GetProperty(text)?.PropertyType.Name=="MultilingualText","AlarmBase."+text+" is MultilingualText");
        foreach(var e in new[]{"HmiAlarmStateMachine","HmiDiscreteAlarmTriggerMode","HmiAlarmCondition"}) check(T("Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon."+e).IsEnum,e+" enum");
        // Audit (ReadUnifiedAuditSettings)
        check(hmi.GetProperty("HmiAlarmAuditClass")!=null && hmi.GetProperty("AuditTrails")!=null,"HmiSoftware audit entry points");
        foreach(var field in new[]{"CommentRequired","ConfirmationMode","DisplayName","IsGmpEnabled","Name","RequiredFunctionRights"}) Property("Siemens.Engineering.HmiUnified.HmiAudit.HmiAuditClass",field);
        check(T("Siemens.Engineering.HmiUnified.HmiAudit.AuditConfirmationMode").IsEnum,"AuditConfirmationMode enum");
        foreach(var field in new[]{"Backup","Segment","Settings"}) Property("Siemens.Engineering.HmiUnified.HmiLogging.HmiAuditTrail",field);
        Property("Siemens.Engineering.HmiUnified.HmiLogging.HmiLoggingCommon.LogSettings","LogTimePeriod"); Property("Siemens.Engineering.HmiUnified.HmiLogging.HmiLoggingCommon.LogSegment","SegmentTimePeriod");
        // Tool surface
        var tools=server.GetType("TiaMcpServer.ModelContextProtocol.McpServer",true)!;
        foreach(var name in new[]{"ManageUnifiedObjectParts","ManageUnifiedDynamization","ManageUnifiedScreenLayout","ManageUnifiedListEntries"})
        {
            var method=tools.GetMethod(name)!;
            check(Equals(method.GetParameters().Single(p=>p.Name=="dryRun").DefaultValue,true),name+" defaults to preview");
            check(Equals(method.GetParameters().Single(p=>p.Name=="confirmDelete").DefaultValue,false),name+" delete requires explicit confirmation");
        }
        foreach(var name in new[]{"ReadUnifiedObjectEvents","ReadUnifiedAlarmCommon","ReadUnifiedAuditSettings"})
            check(tools.GetMethod(name)!.GetParameters().Any(p=>p.Name=="limit") && !tools.GetMethod(name)!.GetParameters().Any(p=>p.Name=="dryRun"),name+" is a paginated read");
        if(v20) Console.WriteLine("CAPABILITY V20: Unified UI model checked against merged Siemens.Engineering; properties exposed only as dynamic attributes are refused by the CLR-setter rule.");
    }
}
