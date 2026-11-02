using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Slice 7 of the cell port (design note row 37): what a Site is doing, from
/// the rows its supervisor and the installers wrote — the office, the last
/// supervisor lifetimes, the requests standing over it, the sources it
/// holds, its cards, and the attempts open on it.
/// </summary>
public sealed class SiteProjectionTests
{
    static (RegistryWriter Writer, RegistryOperations.Site Site, long StudyId, IReadOnlyList<long> CellIds, long SourceId) Home()
    {
        var path = TestRegistry.CreateRegistry();
        var writer = RegistryHome.OpenWriter(path);
        long sourceId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('5e87434e', '[]', 'commit', '2026-09-11') RETURNING id
                """;
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        var registered = RegistryOperations.RegisterSite(
            writer, "pod-5090",
            """{"device":"cuda","admitted_models":["fern"],"executor":"ssh","devices":["GPU-aaaa"]}""",
            """{"slots":1}""");
        Assert.True(registered.TryGetValue(out var site));
        var study = RegistryOperations.RegisterStudy(
            writer, "watched", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId,
            [new RegistryOperations.CompiledCell(0, "cell-0", "rossler_param_shock.series", 42,
                """{"schema":"fern.training.v2","model":{"type":"fern"}}""",
                """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")]);
        Assert.True(study.TryGetValue(out var value));
        var one = RegistryOperations.ListSites(writer.Connection).Single(candidate => candidate.SiteId == site.SiteId);
        return (writer, one, value.StudyId, value.CellIds, sourceId);
    }

    [Fact]
    public void ASiteNobodyHasSupervisedSaysSoAndThatItDeclaresNoCode()
    {
        var (writer, site, _, _, _) = Home();
        using var open = writer;

        var rendered = SiteProjection.Render(writer.Connection, site);

        Assert.Contains("Site pod-5090  executor=ssh  device=cuda  slots=1  cards=GPU-aaaa  admits=fern", rendered, StringComparison.Ordinal);
        Assert.Contains("office: no supervisor has ever held it (install-supervisor-service", rendered, StringComparison.Ordinal);
        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): no sources are
        // held; what the Site says instead is where its cells take their code from.
        Assert.Contains("code: not declared (register-site with code_from working-tree or commit)", rendered, StringComparison.Ordinal);
        Assert.Contains("card GPU-aaaa: free", rendered, StringComparison.Ordinal);
        Assert.Contains("attempts: none open", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ASupervisedSiteShowsItsOfficeSourcesCardsRequestsAndOpenAttempts()
    {
        var (writer, site, _, cellIds, sourceId) = Home();
        using var open = writer;
        TestRegistry.DeclareCodeFrom(writer, site.SiteId);
        Assert.True(RegistryOperations.StartSupervisor(writer, site.SiteId, "pod", 4242, "sha256:cafe", 600).TryGetValue(out var office));
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[0], site.SiteId, "{}").TryGetValue(out var attempt));
        Assert.True(RegistryOperations.RenewAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, "pod/4242").IsOk);
        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestHold, RegistryOperations.SiteScope(site.SiteId), "cable work", "qilin").IsOk);

        var declared = RegistryOperations.ListSites(writer.Connection).Single(candidate => candidate.SiteId == site.SiteId);
        var rendered = SiteProjection.Render(writer.Connection, declared);

        Assert.Contains("office: supervisor pod/4242 epoch 1 holds it, lease until", rendered, StringComparison.Ordinal);
        Assert.Contains("epoch 1  pod/4242  started", rendered, StringComparison.Ordinal);
        Assert.Contains("hold by qilin over site:" + site.SiteId + ": cable work", rendered, StringComparison.Ordinal);
        Assert.Contains("code: working-tree of /repository", rendered, StringComparison.Ordinal);
        Assert.Contains("card GPU-aaaa: held by cell attempt:" + attempt.AttemptId, rendered, StringComparison.Ordinal);
        Assert.Contains($"attempt {attempt.AttemptId} epoch {attempt.FencingEpoch}: cell cell-0 of study watched  running since", rendered, StringComparison.Ordinal);
        Assert.Contains("held by pod/4242", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("CRASH LOOP", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnconfirmedOfficeAndACrashLoopAreSaidInCapitals()
    {
        var (writer, site, _, _, _) = Home();
        using var open = writer;
        for (var round = 0; round < 3; round++)
        {
            Assert.True(RegistryOperations.StartSupervisor(writer, site.SiteId, "pod", 100 + round, "sha256:cafe", 600).TryGetValue(out var office));
            Assert.True(RegistryOperations.EndSupervisor(writer, office, "office_lost", RegistryOperations.DispositionRetryable, 1).IsOk);
        }
        Assert.True(RegistryOperations.StartSupervisor(writer, site.SiteId, "pod", 200, "sha256:cafe", 1).TryGetValue(out _));
        Thread.Sleep(1500);

        var rendered = SiteProjection.Render(writer.Connection, site);

        Assert.Contains("office: UNCONFIRMED, supervisor pod/200 epoch 4 stopped renewing at", rendered, StringComparison.Ordinal);
        Assert.Contains("CRASH LOOP: 3 retryable endings within 60 min", rendered, StringComparison.Ordinal);
    }
}
