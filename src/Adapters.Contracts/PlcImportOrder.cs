namespace TiaMcp.Adapters.Contracts
{
    // Official import order (blocks skill, "Import Order Rule"; API audit 2026-10-19 F9): PLC data types before blocks, and blocks
    // as FB/FC, then global DBs, then instance DBs and OBs.
    public static class PlcImportOrder
    {
        public static int BlockRank(string kind) => kind switch
        {
            "FB" or "FC" => 0,
            "GlobalDB" or "ArrayDB" => 1,
            "InstanceDB" => 2,
            "OB" => 3,
            _ => 4
        };
    }
}
