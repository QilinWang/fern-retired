using System.CommandLine;
using System.ComponentModel;
using System.Text.Json;
using Fern.Scheduler;
using Fern.Install;

namespace Fern.Run.Cli;

/// <summary>
/// `publish-runtime-release` and `install-runtime-release`: a machine
/// downloads its runtime instead of being pushed one.
///
/// CHG 2026-09-12 (owner's intent
/// program-11-fern-result-experience.machines-download-the-runtime, and the
/// owner's word: "streamline the binary github publish and over pod install").
/// The rented machine used to receive a verified copy of the source, download
/// a .NET SDK, and build fern-run itself (scripts/bootstrap_fern.sh) — an SDK,
/// a compiler and a source tree on a machine that only had to run one binary.
/// Now the control machine publishes the set it installed, and the rented
/// machine fetches exactly those bytes: measured 22.6 MB/s on the Pod, so the
/// 45 MB fern-run lands in about two seconds, against a home connection that
/// has to push it.
/// </summary>
public static class RuntimeReleaseVerbs
{
    public static void Add(Command parent)
    {
        parent.Subcommands.Add(PublishRuntimeRelease());
        parent.Subcommands.Add(InstallRuntimeRelease());
    }

    static Option<string> Repository() => new("--repository")
    {
        Description = "owner/name of the private repository whose release holds the binaries",
        DefaultValueFactory = _ => RuntimeRelease.DefaultRepository,
    };

    static Option<string> Tag() => new("--tag")
    {
        Description = "the release tag that holds the content-addressed binaries",
        DefaultValueFactory = _ => RuntimeRelease.DefaultTag,
    };

    static Option<string?> TokenFile() => new("--token-file")
    {
        Description =
            "file holding a GitHub token with read access to the repository; without it "
            + GitHubReleases.TokenVariable + " is read, then `gh auth token`",
    };

    static Command PublishRuntimeRelease()
    {
        var deploymentRoot = new Option<string>("--deployment-root")
        {
            Description = "root holding the installed binaries and the build inventory whose active set is published",
            DefaultValueFactory = _ => Fern.Registry.RepositoryLayout.DeployDirectory, // CHG 2026-09-26: was deploy
        };
        var repository = Repository();
        var tag = Tag();
        var tokenFile = TokenFile();
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "print the manifest this would publish and upload nothing",
        };
        var command = new Command(
            "publish-runtime-release",
            "Publish this machine's active binary set to a private GitHub release, as one content-addressed asset per "
            + "member plus a manifest naming the set; a member whose bytes are already published is not uploaded again.");
        foreach (var option in new Option[] { deploymentRoot, repository, tag, tokenFile, dryRun })
        {
            command.Options.Add(option);
        }
        command.SetAction(parsed =>
        {
            try
            {
                var request = new RuntimeRelease.PublishRequest(
                    parsed.GetValue(deploymentRoot)!,
                    parsed.GetValue(repository)!,
                    parsed.GetValue(tag)!,
                    parsed.GetValue(dryRun));
                using var github = new GitHubReleases(GitHubReleases.ResolveToken(parsed.GetValue(tokenFile)));
                var report = RuntimeRelease.Publish(github, request);
                var manifest = report.Manifest;
                Console.Error.WriteLine(
                    $"binary set {RuntimeRelease.ShortSet(manifest.BinarySetId)} built from {manifest.BuiltFromCommit[..Math.Min(12, manifest.BuiltFromCommit.Length)]}"
                    );
                foreach (var asset in report.Unchanged) Console.Error.WriteLine($"unchanged {asset}");
                foreach (var asset in report.Uploaded) Console.Error.WriteLine($"uploaded  {asset}");
                if (report.RunAssetId > 0)
                {
                    Console.Error.WriteLine(
                        "a machine with nothing installed fetches fern-run with:\n  "
                        + RuntimeRelease.BootstrapCommand(request.Repository, report.RunAssetId, "<token file>", "./fern-run"));
                }
                Console.Error.WriteLine(
                    $"then: fern-run install-runtime-release --set {RuntimeRelease.ShortSet(manifest.BinarySetId)} "
                    + $"--repository {request.Repository} --tag {request.Tag} --registry <home path or http url> --token-file <file>");
                Console.Out.WriteLine(report.ReleaseUrl);
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidOperationException or HttpRequestException or JsonException or Win32Exception)
            {
                Console.Error.WriteLine("publish-runtime-release refused: " + error.Message);
                return 1;
            }
        });
        return command;
    }

    static Command InstallRuntimeRelease()
    {
        var repository = Repository();
        var tag = Tag();
        var tokenFile = TokenFile();
        var set = new Option<string?>("--set")
        {
            Description = "the binary set to install, by its identity or first twelve hex characters; without it, the set published last",
        };
        // What this machine is: the Home it answers to, and the stores it
        // keeps. A machine that pulls its work names the Home's address and
        // has neither execution profiles nor an SSH helper.
        var registry = new Option<string>("--registry")
        {
            Description = "the Registry Home this installed set answers to: a lifecycle.db path, or the http address of a served Home",
            Required = true,
        };
        var workRoot = new Option<string>("--work-root")
        {
            Description = "attempt handoff and worker work root recorded in the receipt",
            DefaultValueFactory = _ => Fern.Registry.RepositoryLayout.WorkerWorkRoot, // CHG 2026-09-26: was tmp/fern-run-worker
        };
        var deploymentRoot = new Option<string>("--deployment-root")
        {
            Description = "root for the immutable local binaries and the installation receipt",
            DefaultValueFactory = _ => Fern.Registry.RepositoryLayout.DeployDirectory, // CHG 2026-09-26: was deploy
        };
        var deployRoot = new Option<string>("--deploy-root")
        {
            Description = "content-addressed source store this machine materializes study sources into",
            DefaultValueFactory = _ => Fern.Registry.RepositoryLayout.DeployDirectory, // CHG 2026-09-26: was deploy
        };
        var profilesRoot = new Option<string?>("--profiles-root")
        {
            Description = "directory of execution profile JSON files; omit on a machine that places no work",
        };
        var sshHelper = new Option<string?>("--ssh-helper")
        {
            Description = "maintained helper an SSH Site is reached through; omit on a machine that reaches no other machine",
        };
        var command = new Command(
            "install-runtime-release",
            "Download a published binary set and install it here: the assets are verified against the manifest's digests, "
            + "probed, published content-addressed and activated, leaving the same receipt a local build leaves.");
        foreach (var option in new Option[]
                 { repository, tag, tokenFile, set, registry, workRoot, deploymentRoot, deployRoot, profilesRoot, sshHelper })
        {
            command.Options.Add(option);
        }
        command.SetAction(parsed =>
        {
            try
            {
                var request = new RuntimeRelease.InstallRequest(
                    parsed.GetValue(repository)!,
                    parsed.GetValue(tag)!,
                    parsed.GetValue(set),
                    parsed.GetValue(registry)!,
                    parsed.GetValue(workRoot)!,
                    parsed.GetValue(deploymentRoot)!,
                    parsed.GetValue(deployRoot)!,
                    parsed.GetValue(profilesRoot) ?? "",
                    parsed.GetValue(sshHelper) ?? "");
                using var github = new GitHubReleases(GitHubReleases.ResolveToken(parsed.GetValue(tokenFile)));
                var report = RuntimeRelease.Install(github, request);
                var megabytes = report.DownloadedBytes / (1024.0 * 1024.0);
                Console.Error.WriteLine(
                    $"downloaded {megabytes:F1} MB in {report.DownloadSeconds:F1} s"
                    + (report.DownloadSeconds > 0 ? $" ({megabytes / report.DownloadSeconds:F1} MB/s)" : ""));
                InstallCli.PrintInstallation(report.Installation, Console.Out);
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidOperationException or HttpRequestException or JsonException or Win32Exception)
            {
                Console.Error.WriteLine("install-runtime-release refused: " + error.Message);
                return 1;
            }
        });
        return command;
    }
}
