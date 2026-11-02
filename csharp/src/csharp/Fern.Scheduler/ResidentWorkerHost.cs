using Fern.Site;

namespace Fern.Scheduler;

/// <summary>
/// The resident Python worker as the local cell worker drives it: one warm
/// process per interpreter, source and card, fed through a folder inbox.
///
/// CHG 2026-09-11 night (slice 4 of the cell port, design note
/// docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md,
/// row 20). The protocol is <see cref="ResidentPythonWorker"/>'s, unchanged
/// (Program 18, Worker-B): `worker.lock` for the process lifetime,
/// `worker.json` published as starting then ready, `inbox/` claimed into
/// `running/`, one receipt per phase under `receipts/&lt;cell&gt;/`, `done` or
/// `failed.json` at the end. This interface exists for the seam only: the
/// production adapter forwards every call, and a test answers with files.
/// </summary>
public interface IResidentWorkerHost
{
    SiteResult<ResidentPythonWorker.WorkerIdentity> Ensure(
        string root, string python, string sourceRoot, TimeSpan startDeadline,
        Action<ResidentPythonWorker.WorkerStartupObservation>? observeStartup,
        IReadOnlyDictionary<string, string> environment);

    SiteResult<string> Submit(string root, string cellRequestJson);

    bool IsAlive(ResidentPythonWorker.WorkerIdentity identity);

    /// <summary>End exactly this worker lifetime; the caller settles the cell it was running.</summary>
    SiteResult<bool> Kill(ResidentPythonWorker.WorkerIdentity identity);
}

public sealed class ResidentWorkerHost : IResidentWorkerHost
{
    public SiteResult<ResidentPythonWorker.WorkerIdentity> Ensure(
        string root, string python, string sourceRoot, TimeSpan startDeadline,
        Action<ResidentPythonWorker.WorkerStartupObservation>? observeStartup,
        IReadOnlyDictionary<string, string> environment) =>
        ResidentPythonWorker.EnsureRunning(root, python, sourceRoot, startDeadline, observeStartup, environment);

    public SiteResult<string> Submit(string root, string cellRequestJson) =>
        ResidentPythonWorker.Submit(root, cellRequestJson);

    public bool IsAlive(ResidentPythonWorker.WorkerIdentity identity) =>
        ResidentPythonWorker.IsAlive(identity.Pid);

    public SiteResult<bool> Kill(ResidentPythonWorker.WorkerIdentity identity) =>
        ResidentPythonWorker.Kill(identity);
}
