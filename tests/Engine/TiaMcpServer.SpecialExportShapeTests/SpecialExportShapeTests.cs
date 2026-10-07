using Siemens.Engineering.SW.WatchAndForceTables;
using TiaMcp.PlcFoundation;

internal static class SpecialExportShapeTests
{
    internal static void Run(Action<bool,string> Check)
    {
        void Reject(Action action,string message)
        {
            try { action(); }
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException) { Check(true,message); return; }
            throw new Exception(message);
        }
        void Export(PlcFoundationEngine engine,string path) => engine.ExportPlcWatchTable("devices/D/CPU",path,"unused.xml",dryRun:false);
        var engine=new PlcFoundationEngine();
        var root=engine.Software.WatchAndForceTableGroup;
        var rootTable=new PlcWatchTable { Name="same/name" };
        var childTable=new PlcWatchTable { Name="same/name" };
        var nestedTable=new PlcWatchTable { Name="table %" };
        var child=new PlcWatchAndForceTableUserGroup { Name="folder/one" };
        var nested=new PlcWatchAndForceTableUserGroup { Name="nested %" };
        root.WatchTables.Add(rootTable); root.Groups.Add(child);
        child.WatchTables.Add(childTable); child.Groups.Add(nested); nested.WatchTables.Add(nestedTable);
        Export(engine,"same%2Fname");
        Check(rootTable.ExportCalls==1 && childTable.ExportCalls==0 && nestedTable.ExportCalls==0,"Root owns its table even when a user group has the same name.");
        Export(engine,"folder%2Fone/same%2Fname");
        Check(rootTable.ExportCalls==1 && childTable.ExportCalls==1 && nestedTable.ExportCalls==0,"Child canonical path selects the child-owned table.");
        Export(engine,"folder%2Fone/nested%20%25/table%20%25");
        Check(nestedTable.ExportCalls==1,"Nested user groups preserve escaped canonical paths.");
        Reject(()=>Export(engine,"folder/one/same/name"),"Noncanonical path was accepted.");
        Reject(()=>engine.ExportPlcWatchTable("alias","same%2Fname","unused.xml"),"Software alias was accepted.");
        engine.ExportPlcWatchTable("CPU","same%2Fname","unused.xml");
        Check(rootTable.ExportCalls==1,"The unique short PLC name selects the software; a preview exports nothing.");
        child.Groups.Add(child);
        Reject(()=>Export(engine,"same%2Fname"),"Cyclic user group was accepted."); child.Groups.Remove(child);
        root.Groups.Add(new PlcWatchAndForceTableUserGroup { Name=child.Name });
        Reject(()=>Export(engine,"same%2Fname"),"Duplicate sibling identity was accepted."); root.Groups.RemoveAt(1);
        root.WatchTables.Add(new PlcWatchTable { Name=rootTable.Name });
        Reject(()=>Export(engine,"same%2Fname"),"Duplicate table identity was accepted."); root.WatchTables.RemoveAt(1);
        var deep=new PlcFoundationEngine(); PlcWatchAndForceTableGroup current=deep.Software.WatchAndForceTableGroup;
        current.WatchTables.Add(new PlcWatchTable { Name="root" });
        for(int i=0;i<129;i++) { var next=new PlcWatchAndForceTableUserGroup { Name="G" }; current.Groups.Add(next); current=next; }
        Reject(()=>Export(deep,"root"),"Traversal depth budget was bypassed.");
        var wide=new PlcFoundationEngine();
        for(int i=0;i<10001;i++) wide.Software.WatchAndForceTableGroup.WatchTables.Add(new PlcWatchTable { Name="W"+i });
        Reject(()=>Export(wide,"W0"),"Table count budget was bypassed.");
        Check(rootTable.ExportCalls==1 && childTable.ExportCalls==1 && nestedTable.ExportCalls==1,"Invalid traversals perform no export calls.");
    }
}
