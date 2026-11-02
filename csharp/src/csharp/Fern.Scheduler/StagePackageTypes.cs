using Fern.Site;

namespace Fern.Scheduler;

/// <summary>
/// One host file that travels into a Stage package, and the path it takes
/// inside it. The host path never enters identity: only the relative path and
/// the bytes do.
/// </summary>
public sealed record StageSourceFile(string RelativePath, string HostPath);

// CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
// slice 4): WorkerSiteTransport, the SSH Site as the remote installer addressed
// it, is removed with that installer.

/// <summary>
/// One semantic training checkpoint the remote Stage reported. It advances only
/// when the work unit advances; it is not a process heartbeat and must never be
/// used to refresh an epoch that has not moved.
/// </summary>
public readonly record struct RemoteTrainingProgress(long Current, long Total, bool Terminal);

/// <summary>Where one Attempt's three directories live on the Home machine.</summary>
public sealed record RemoteStageSites(string Outbound, string Runtime, string Returns);

/// <summary>One built package: where it is, and the instructions it satisfies.</summary>
public sealed record BuiltStagePackage(string Root, AttemptInstructions Instructions);
