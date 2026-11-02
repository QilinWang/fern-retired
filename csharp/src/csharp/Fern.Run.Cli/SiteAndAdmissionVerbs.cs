using System.CommandLine;
using Fern.Registry;

namespace Fern.Run.Cli;

/// <summary>
/// The one `site` command group: prepare a Site's pinned model environment.
/// CHG 2026-09-11 night (slice 9 of the cell port): `site list`, `site
/// register`, `site enable` and `site disable` administered the old Worker
/// Site registry (`worker_sites`); the Site is the `sites` row now, and those
/// four are `register-site`, `monitor-site`, `enable-site` and `disable-site`
/// at the root. What stays under `site` is the environment install and its
/// lease reconcile, which admit a model set on the Site's row.
/// The paragraph below is the record of the retired verbs.
/// `site register` recorded one execution
/// profile as a Worker Site with the machine facts observed now;
/// `site enable`/`site disable` stopped or resumed claims. CHG 2026-09-04:
/// `register-experiment-contrast` and `accept-sweep`
/// left with the recipe route whose catalog and extensions they read
/// (retired/csharp/recipe-route-2026-09-04).
///
/// Each verb parses the flags of its Rust clap struct, calls ONE typed C#
/// owner, and writes the bytes the operator saw before. Where Rust reached its
/// owner through a subprocess — the private Go Home writer for the Site
/// transitions, `fern-registry` for the other two — the host calls the owner
/// in-process, so the clause that named the child ("Go Home writer refused
/// set-site-enabled: ", "C# Task writer refused accept-sweep: ") is not
/// reproduced: naming a process that does not run would be a lie about the
/// mechanism. The owner's own sentence is the contract, as agreed for
/// cancel-plan and the `task` verbs.
///
/// `site register` is the one verb here that measures something before it
/// writes: it observes the machine the profile names, refuses unless that
/// machine can execute what the profile declares, and only then records what
/// it saw. The observation is Fern.Scheduler's
/// (ReadyExecutionGate.Resolve / ObservedExecutionProbe.Observe), which runs
/// `python/fern_stage_runtime/observe_execution.py` under the profile's own
/// interpreter.
/// </summary>
public static class SiteAndAdmissionVerbs
{
    public static void Add(Command parent)
    {
        var site = new Command(
            "site",
            "Prepare a Site's pinned model environment: install-model-environment and reconcile-environment-install-lease.");
        ModelEnvironmentInstallationVerbs.Add(site);
        parent.Subcommands.Add(site);
    }
}
