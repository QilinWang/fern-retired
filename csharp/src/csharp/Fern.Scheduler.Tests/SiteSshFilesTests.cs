using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The SSH half of update-site-files, on temporary directories, with the real
/// Gleam program and ssh's settings given rather than read.
/// </summary>
public sealed class SiteSshFilesTests
{
    const string HandConfig =
        "Host fern-autodl-home\n    HostName home.example.invalid\n    User root\n    Port 12223\n\n"
        + "Host fern-autodl-bj-5090\n    HostName site-b2.example.invalid\n    User root\n    Port 50300\n"
        + "    Compression yes\n    RemoteForward 127.0.0.1:8622 127.0.0.1:8621\n    ExitOnForwardFailure yes\n";

    sealed class Settings(Func<int, IReadOnlyDictionary<string, string>> reading) : SiteSshFiles.ISshSettings
    {
        int calls;
        public IReadOnlyDictionary<string, string> Effective(string alias) => reading(calls++);
    }

    static readonly IReadOnlyDictionary<string, string> Beijing = new Dictionary<string, string>
    {
        ["hostname"] = "site-b2.example.invalid", ["user"] = "root", ["port"] = "50300",
    };

    static SiteFiles.Paths Layout(string root)
    {
        var paths = new SiteFiles.Paths(
            Directory.CreateDirectory(Path.Combine(root, "profiles")).FullName,
            Directory.CreateDirectory(Path.Combine(root, "ssh")).FullName,
            Directory.CreateDirectory(Path.Combine(root, "units")).FullName);
        File.WriteAllText(Path.Combine(paths.Profiles, "autodl-bj-5090.json"),
            """{"profile_id":"autodl-bj-5090","executor":"ssh","ssh_destination":"root@site-b2.example.invalid:50300","python":"/root/python","work_root":"/root/work"}""");
        File.WriteAllText(Path.Combine(paths.Profiles, "autodl-bj-5090.supervisor.json"),
            """{"schema":"fern.site_supervisor.v1","site":"autodl-bj-5090","remote_forward":{"site_port":8622,"home_port":8621},"ssh_compression":true,"fern_run":"/root/bin/fern-run.8e41","process_environment":{},"restart":"always"}""");
        File.WriteAllText(Path.Combine(paths.SshDirectory, "config"), HandConfig);
        return paths;
    }

    [Fact]
    public void TheHandBlockMovesIntoTheRenderedFileAndASecondRunChangesNothing()
    {
        var root = Directory.CreateTempSubdirectory("site-ssh-").FullName;
        try
        {
            var paths = Layout(root);
            using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());

            var first = SiteSshFiles.Update(program, paths, new Settings(_ => Beijing), "Generated for a test");

            Assert.True(first.Changed);
            Assert.True(first.IncludeAdded);
            Assert.Equal(["fern-autodl-bj-5090"], first.Removed);
            Assert.NotNull(first.Backup);
            Assert.Equal(HandConfig, File.ReadAllText(first.Backup!));
            var config = File.ReadAllText(Path.Combine(paths.SshDirectory, "config"));
            Assert.StartsWith("Include ~/.ssh/fern-sites.config\n", config, StringComparison.Ordinal);
            Assert.DoesNotContain("Host fern-autodl-bj-5090", config, StringComparison.Ordinal);
            Assert.Contains("Host fern-autodl-home", config, StringComparison.Ordinal);
            Assert.Contains("Host fern-autodl-bj-5090\n", File.ReadAllText(Path.Combine(paths.SshDirectory, "fern-sites.config")),
                StringComparison.Ordinal);

            var second = SiteSshFiles.Update(program, paths, new Settings(_ => Beijing), "Generated for a test");

            Assert.False(second.Changed);
            Assert.Null(second.Backup);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AChangeInWhatSshWouldUsePutsBothFilesBack()
    {
        var root = Directory.CreateTempSubdirectory("site-ssh-").FullName;
        try
        {
            var paths = Layout(root);
            using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
            var moved = new Dictionary<string, string>(Beijing) { ["port"] = "22" };

            var result = SiteSshFiles.Update(program, paths, new Settings(call => call == 0 ? Beijing : moved), "t");

            Assert.False(result.Changed);
            Assert.Contains("ssh's port for fern-autodl-bj-5090 changed from 50300 to 22", result.Problems);
            Assert.Equal(HandConfig, File.ReadAllText(Path.Combine(paths.SshDirectory, "config")));
            Assert.False(File.Exists(Path.Combine(paths.SshDirectory, "fern-sites.config")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
