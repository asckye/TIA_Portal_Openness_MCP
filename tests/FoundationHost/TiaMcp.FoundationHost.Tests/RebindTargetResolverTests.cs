using TiaMcp.FoundationHost;
using Xunit;

public sealed class RebindTargetResolverTests
{
    public static IEnumerable<object[]> Cases()
    {
        var same = new PortalProcessFact(123, "2026-11-13T10:00:00.0000000Z", "C:/P.ap19", 1);
        var moved = new PortalProcessFact(456, "2026-11-13T11:00:00.0000000Z", "C:/P.ap19", 0);
        yield return new object[] { new[] { same }, RebindDisposition.Same, 123 };
        yield return new object[] { new[] { moved }, RebindDisposition.Moved, 456 };
        yield return new object[] { new[] { moved with { AttachedSessions = 1 } }, RebindDisposition.Held, 456 };
        yield return new object[] { Array.Empty<PortalProcessFact>(), RebindDisposition.Gone, 0 };
        yield return new object[] { new[] { moved, moved with { Id = 789 } }, RebindDisposition.Ambiguous, 0 };
        yield return new object[] {
            new[] { same with { ProcessStartUtc = "2026-11-13T12:00:00.0000000Z", AttachedSessions = 0 } }, RebindDisposition.Moved, 123 };
        yield return new object[] { new[] { moved with { ProjectPath = "C:/Other.ap19" } }, RebindDisposition.Gone, 0 };
        yield return new object[] { new[] { moved with { ProcessStartUtc = null } }, RebindDisposition.Moved, 456 };
        yield return new object[] { new[] { moved with { ProjectPath = "C:/P|.ap19" } }, RebindDisposition.Gone, 0 };
        yield return new object[] { new[] { moved with { ProjectPath = "C:/" + new string('p', 300) } }, RebindDisposition.Gone, 0 };
        yield return new object[] { new[] { same, same with { AttachedSessions = 0 } }, RebindDisposition.Ambiguous, 0 };
    }

    [Theory, MemberData(nameof(Cases))]
    public void Exact_identity_and_project_select_the_only_eligible_process(object facts, object expected, int pid)
    {
        var result = RebindTargetResolver.Resolve(new(123, "2026-11-13T10:00:00.0000000Z", "C:/P.ap19", "P"), (PortalProcessFact[])facts);
        Assert.Equal((RebindDisposition)expected, result.Disposition);
        Assert.Equal(pid, result.Process?.Id ?? 0);
    }

    [Fact]
    public void A_target_without_a_project_never_selects_a_different_process()
    {
        var result = RebindTargetResolver.Resolve(new(123, "2026-11-13T10:00:00.0000000Z", null, null),
            new[] { new PortalProcessFact(456, "2026-11-13T11:00:00.0000000Z", null, 0) });
        Assert.Equal(RebindDisposition.Gone, result.Disposition);
    }
}
