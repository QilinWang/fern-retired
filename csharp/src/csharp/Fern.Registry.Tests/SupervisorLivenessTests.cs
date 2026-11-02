using Fern.Registry;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Whether an office's holder still exists, and what that permits.
///
/// Written as the four situations of the night of 2026-09-26, because each one
/// happened: a holder that was alive and should never be displaced; a holder on
/// this machine that had exited, where ten minutes were spent waiting for a
/// fact already in hand; a holder on a machine that could not be reached, which
/// was training the whole time; and an id that had been handed to somebody else.
/// </summary>
public sealed class SupervisorLivenessTests
{
    static readonly DateTimeOffset Recorded = DateTimeOffset.Parse("2026-09-26T04:00:00Z");
    static readonly DateTimeOffset LeaseEnds = DateTimeOffset.Parse("2026-09-26T04:10:00Z");
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-26T04:05:00Z");

    static SupervisorLiveness.Office AnOffice(string host = "MSI", bool ended = false) =>
        new("local-4070", host, 1366929, Recorded, LeaseEnds, ended);

    static SupervisorLiveness.Look LookedOnItsHost(bool exists, DateTimeOffset? began = null) =>
        new("MSI", new SupervisorLiveness.ProcessSighting(exists, began), Now);

    // ── the holder is there ────────────────────────────────────────────────

    [Fact]
    public void ALiveHolderIsNeverDisplaced()
    {
        var standing = SupervisorLiveness.Of(AnOffice(), LookedOnItsHost(true, Recorded));
        Assert.Equal(SupervisorLiveness.Standing.Held, standing);
        Assert.False(SupervisorLiveness.MayBeTakenOnThisLook(standing));
    }

    [Fact]
    public void ALiveHolderIsNotDisplacedEvenPastItsLease()
    {
        // It may be training. The lease running out is not evidence of death;
        // it is the absence of evidence, and here there is evidence of life.
        var late = new SupervisorLiveness.Look("MSI",
            new SupervisorLiveness.ProcessSighting(true, Recorded),
            DateTimeOffset.Parse("2026-09-26T05:00:00Z"));
        Assert.Equal(SupervisorLiveness.Standing.Held, SupervisorLiveness.Of(AnOffice(), late));
    }

    // ── the holder is gone, and we can see that ────────────────────────────

    [Fact]
    public void AHolderThatHasExitedOnThisMachineIsGoneAtOnce()
    {
        // 2026-09-26: the process had stopped renewing thirteen minutes
        // earlier, nothing was training, and the cleanup waited for the lease.
        var standing = SupervisorLiveness.Of(AnOffice(), LookedOnItsHost(false));
        Assert.Equal(SupervisorLiveness.Standing.HolderGone, standing);
        Assert.True(SupervisorLiveness.MayBeTakenOnThisLook(standing));
    }

    [Fact]
    public void AnIdHandedToAnotherProcessIsNotTheHolder()
    {
        // The id exists again, but that process began after the office was
        // recorded, so it is a stranger wearing the number. Without this the
        // dead office would stand for as long as the stranger lives.
        var stranger = LookedOnItsHost(true, Recorded.AddMinutes(1));
        Assert.Equal(SupervisorLiveness.Standing.HolderGone, SupervisorLiveness.Of(AnOffice(), stranger));
    }

    [Fact]
    public void AProcessOlderThanTheOfficeIsTakenAsTheHolder()
    {
        // A supervisor starts, then records its office a moment later, so the
        // process is always the older of the two. This must not read as a
        // stranger, or every live office would be declared dead.
        var itself = LookedOnItsHost(true, Recorded.AddSeconds(-2));
        Assert.Equal(SupervisorLiveness.Standing.Held, SupervisorLiveness.Of(AnOffice(), itself));
    }

    [Fact]
    public void AProcessWhoseStartCannotBeReadIsTakenAsTheHolder()
    {
        // Unreadable is not absent. The safe reading of "it exists but I could
        // not learn when it began" is that the holder is there.
        var unreadable = LookedOnItsHost(true, null);
        Assert.Equal(SupervisorLiveness.Standing.Held, SupervisorLiveness.Of(AnOffice(), unreadable));
    }

    // ── the holder is somewhere we cannot look ─────────────────────────────

    [Fact]
    public void AnotherHostIsNeverJudgedFromHereWhileItsLeaseStands()
    {
        // The three AutoDL machines were unreachable all night and two of them
        // were training. Not seeing a process here says nothing about there.
        var fromHere = new SupervisorLiveness.Look("MSI", null, Now);
        var standing = SupervisorLiveness.Of(AnOffice("autodl-bj-5090"), fromHere);
        Assert.Equal(SupervisorLiveness.Standing.CannotTellFromHere, standing);
        Assert.False(SupervisorLiveness.MayBeTakenOnThisLook(standing));
    }

    [Fact]
    public void AnUnreachableHostPastItsLeaseIsTheSlowPathAndNotATakeover()
    {
        var after = new SupervisorLiveness.Look("MSI", null, LeaseEnds.AddSeconds(1));
        var standing = SupervisorLiveness.Of(AnOffice("autodl-bj-5090"), after);
        Assert.Equal(SupervisorLiveness.Standing.LeaseExpired, standing);
        // Deliberately false: an expired lease is the Registry's own slower
        // path, not something this look authorises.
        Assert.False(SupervisorLiveness.MayBeTakenOnThisLook(standing));
    }

    [Fact]
    public void LookingFromTheWrongHostIsNotLooking()
    {
        // A sighting carried from another machine must not be read as a local
        // look, or one machine's empty process table condemns another's office.
        var elsewhere = new SupervisorLiveness.Look("autodl-4090d",
            new SupervisorLiveness.ProcessSighting(false, null), Now);
        Assert.Equal(SupervisorLiveness.Standing.CannotTellFromHere,
            SupervisorLiveness.Of(AnOffice(), elsewhere));
    }

    // ── what the operator is told ──────────────────────────────────────────

    [Fact]
    public void EachStandingSaysWhatToDoAboutIt()
    {
        var office = AnOffice();
        Assert.Contains("that process is gone on this machine; the office may be taken now",
            SupervisorLiveness.Sentence(office, SupervisorLiveness.Standing.HolderGone),
            StringComparison.Ordinal);
        Assert.Contains("may still be training",
            SupervisorLiveness.Sentence(office, SupervisorLiveness.Standing.CannotTellFromHere),
            StringComparison.Ordinal);
        foreach (var standing in Enum.GetValues<SupervisorLiveness.Standing>())
        {
            var sentence = SupervisorLiveness.Sentence(office, standing);
            Assert.Contains("local-4070", sentence, StringComparison.Ordinal);
            Assert.Contains("1366929", sentence, StringComparison.Ordinal);
        }
    }

    // ── the offices a refusal should be able to name ───────────────────────

    [Fact]
    public void ALiveOfficeIsReadBackWithItsSiteHostAndProcess()
    {
        // CHG 2026-09-26, after an audit: this asserted a Site name the test
        // had typed in itself, so deleting t.name from the SELECT would not
        // have failed it. It now reads the Site back from the row.
        var writer = Home(out var siteId);
        Supervisor(writer, siteId, pid: 1366929, until: DateTimeOffset.UtcNow.AddHours(1), ended: false);

        var office = Assert.Single(PinnedBinaryReclaimAuthority.LiveOffices(writer.Connection));
        Assert.Equal("local-4070", office.Site);
        Assert.Equal("MSI", office.Host);
        Assert.Equal(1366929, office.Pid);
        Assert.False(office.Ended);
        // And the Site in the sentence is the one that was read, not one typed here.
        Assert.Contains("Site local-4070",
            SupervisorLiveness.Sentence(office, SupervisorLiveness.Standing.HolderGone), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEndedOfficeIsReadBackAsEndedAndIsNeverHeld()
    {
        // CHG 2026-09-26, after an audit: this used to open an empty registry
        // and assert emptiness, so it passed for any query at all. It now
        // inserts a supervisor that ended and requires the answer.
        var writer = Home(out var siteId);
        Supervisor(writer, siteId, pid: 4242, until: DateTimeOffset.UtcNow.AddHours(1), ended: true);

        var office = Assert.Single(PinnedBinaryReclaimAuthority.LiveOffices(writer.Connection));
        Assert.True(office.Ended);

        // Even with its process alive on this host, an ended row is not an
        // office: the Registry requires not-ended and a standing lease together.
        Assert.Equal(SupervisorLiveness.Standing.HolderGone,
            SupervisorLiveness.Of(office, new SupervisorLiveness.Look("MSI",
                new SupervisorLiveness.ProcessSighting(true, Recorded), Now)));
    }

    [Fact]
    public void AnOfficeWhoseLeaseRanOutIsNotReadBackAsLive()
    {
        var writer = Home(out var siteId);
        Supervisor(writer, siteId, pid: 99, until: DateTimeOffset.UtcNow.AddHours(-1), ended: false);
        Assert.Empty(PinnedBinaryReclaimAuthority.LiveOffices(writer.Connection));
    }

    static RegistryWriter Home(out long siteId)
    {
        var writer = RegistryHome.OpenWriter(ProductionHome.CreateRegistry());
        using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
            VALUES ('local-4070', '{"device":"cuda"}', '{"slots":1}', '2026-09-26') RETURNING id
            """;
        siteId = Convert.ToInt64(command.ExecuteScalar());
        return writer;
    }

    static void Supervisor(RegistryWriter writer, long siteId, long pid, DateTimeOffset until, bool ended)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO supervisors (site_id, host, pid, epoch, lease_until, binary_sha256, started_at, ended_at)
            VALUES ($site, 'MSI', $pid, 1, $until, 'sha256:0', '2026-09-26T04:00:00+00:00', $ended)
            """;
        command.Parameters.AddWithValue("$site", siteId);
        command.Parameters.AddWithValue("$pid", pid);
        command.Parameters.AddWithValue("$until", until.ToString("o"));
        command.Parameters.AddWithValue("$ended", ended ? "2026-09-26T04:30:00+00:00" : (object)DBNull.Value);
        command.ExecuteNonQuery();
    }
}
