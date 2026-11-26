namespace TiaMcp.Adapters.Contracts;

public static class PlcTagTableDeletionText
{
    public static string Constants(int user, int system)
    {
        string Count(int count, string kind) => $"{count} {kind} constant{(count == 1 ? "" : "s")}";
        return Count(user, "user") + " and " + Count(system, "system");
    }
}
