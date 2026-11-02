using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Slice S3: the connection check with the real Gleam program and a fake
/// Site, which answers each command from a script and records whether it was
/// asked through a tunnel of the check's own.
/// </summary>
public sealed class SiteConnectionTests
{
    const string Profile =
        """{"profile_id":"autodl-bj-5090","executor":"ssh","ssh_destination":"root@site-b2.example.invalid:50300","python":"/root/python","work_root":"/root/work"}""";

    const string Supervisor =
        """{"schema":"fern.site_supervisor.v1","site":"autodl-bj-5090","remote_forward":{"site_port":8622,"home_port":8621},"ssh_compression":true,"fern_run":"/root/bin/fern-run.8e41","process_environment":{},"restart":"always"}""";

    sealed class FakeSite(bool reachable, bool homeWithoutForward, bool homeWithForward, bool binary) : SiteConnection.IRemoteRunner
    {
        public List<(string Target, bool Forward, string Command)> Asked { get; } = [];

        public (bool Completed, string Output) Run(string sshTarget, bool throughForward, string shellCommand)
        {
            Asked.Add((sshTarget, throughForward, shellCommand));
            if (!reachable) return (false, "ssh: connect to host site-b2.example.invalid port 50300: Connection refused");
            if (shellCommand.Contains("fern-site-check", StringComparison.Ordinal)) return (true, "fern-site-check: reached");
            if (shellCommand.Contains("v1/health", StringComparison.Ordinal))
            {
                var answers = throughForward ? homeWithForward : homeWithoutForward;
                return (true, answers ? "fern-home: answered 404" : "fern-home: silent <urlopen error [Errno 111] Connection refused>");
            }
            return (true, binary ? "fern-binary: present" : "fern-binary: absent");
        }
    }

    static SiteConnection.Verdict Check(FakeSite site, string office) =>
        SiteConnection.Check(GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment()),
            Profile, Supervisor, office, "/root/bin/fern-run.8e41", site);

    [Fact]
    public void AReachableSiteWhoseTunnelIsOpenIsReached()
    {
        var site = new FakeSite(true, true, false, true);

        var verdict = Check(site, "held");

        Assert.True(verdict.Reached);
        Assert.All(site.Asked, asked => Assert.False(asked.Forward));
        Assert.All(site.Asked, asked => Assert.Equal("fern-autodl-bj-5090:50300", asked.Target));
        Assert.Contains(site.Asked, asked => asked.Command.Contains("http://127.0.0.1:8622/v1/health", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnreachableSiteStopsAtTheFirstQuestion()
    {
        var site = new FakeSite(false, false, false, false);

        var verdict = Check(site, "free");

        Assert.False(verdict.Reached);
        Assert.Single(site.Asked);
        Assert.Contains("the SSH address root@site-b2.example.invalid:50300 does not answer", verdict.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NoTunnelIsAskedForWhileASupervisorHoldsTheOffice()
    {
        var site = new FakeSite(true, false, true, true);

        var verdict = Check(site, "held");

        Assert.False(verdict.Reached);
        Assert.DoesNotContain(site.Asked, asked => asked.Forward);
        Assert.Contains("although a supervisor holds the Site's office", verdict.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AFreeSiteIsAskedAgainThroughTheDeclaredTunnel()
    {
        var site = new FakeSite(true, false, true, true);

        var verdict = Check(site, "free");

        Assert.True(verdict.Reached);
        Assert.Contains(site.Asked, asked => asked.Forward);
    }

    [Fact]
    public void AMissingBinaryIsNamed()
    {
        var site = new FakeSite(true, true, false, false);

        var verdict = Check(site, "held");

        Assert.False(verdict.Reached);
        Assert.Contains("the runtime binary /root/bin/fern-run.8e41 is not on the Site", verdict.Lines[0], StringComparison.Ordinal);
    }
}
