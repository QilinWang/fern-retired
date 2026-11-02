using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace Fern.Registry;

/// <summary>
/// The lifecycle string-identity family, ported from fern_run/src/lifecycle.rs
/// and proved against real stored rows in a Registry Home -- ids written by
/// the Rust (and, for owner references, the Go scheduler) that this code did
/// not write.
///
/// These are NOT JSON. Each id is sha256 over a \x1f-joined string, in two
/// shapes that must not be confused: the FULL form puts the separator
/// BETWEEN parts, prefix first, and keeps the whole 64-hex digest; digest_of
/// puts the separator AFTER every part, has no prefix, and TRUNCATES to the
/// first 8 bytes -- 16 hex characters.
///
/// Truncation is identity: a digest_of port returning the full digest is
/// wrong in a way no round-trip test catches, which is why the fixture tests
/// pin stored 16-hex ids.
///
/// Two sharp edges, ported faithfully rather than repaired, because refusing
/// what Rust accepts would refuse ids Rust has minted. First, empty
/// components are legal and occur in production: matrix identity writes the
/// empty string for an absent preset, recipe hash, or snapshot id, so
/// consecutive separators appear in real preimages. Second, no component is
/// checked for containing \x1f itself, so in the full form a component
/// carrying the separator can forge a part boundary; digest_of's trailing
/// separator makes ("ab","c") differ from ("a","bc") -- the comment in the
/// Rust says exactly this -- but a component carrying the separator still
/// collides with the split pair. Every production component today is a
/// hash, hex id, epoch, timestamp, or machine name, none of which carry
/// the separator; a new caller passing free text must know the boundary
/// can be forged.
///
/// NOT ported: progress_event_id (lifecycle.rs:4538). Its preimage takes the
/// writing process's pid and an in-process counter, neither of which is
/// stored, so no stored row can prove a recompute -- porting it would rest
/// on self-consistent tests alone.
/// </summary>
public static class LifecycleStringIdentity
{
    const char Separator = '\u001f';

    /// <summary>
    /// Ports lifecycle.rs digest_of: sha256 with every part FOLLOWED by \x1f
    /// -- a separator, so ("ab","c") and ("a","bc") differ -- truncated to
    /// the first 8 bytes, 16 lowercase hex. Shared by matrix identity,
    /// coordinate ids, adoption attempts, and artifact snapshot ids.
    /// </summary>
    public static string DigestOf(IReadOnlyList<string> parts)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            hasher.AppendData(Encoding.UTF8.GetBytes(part));
            hasher.AppendData([0x1f]);
        }

        var digest = hasher.GetHashAndReset();
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>Full-form id: sha256 hex over the separator-joined parts.</summary>
    static string FullForm(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(Separator, parts))));

    /// <summary>
    /// Ports the claim-route attempt id (lifecycle.rs:2861): "attempt" then
    /// coordinate_id, operation_scope, actor_id, idempotency_key, now_ms.
    /// The scope for a work claim reads "matrix:MATRIX:profile:REVISION".
    /// </summary>
    public static string AttemptId(
        string coordinateId, string operationScope, string actorId, string idempotencyKey, long nowMs) =>
        FullForm(
            "attempt", coordinateId, operationScope, actorId, idempotencyKey,
            nowMs.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Ports the adoption-route attempt id (lifecycle.rs:7298), minted when
    /// an existing run row is recorded without a lease:
    /// digest_of(coordinate_id, claimed_at, owner_host, sequence) where
    /// sequence is the count of the coordinate's attempts at insert.
    /// </summary>
    public static string AdoptionAttemptId(
        string coordinateId, string claimedAt, string ownerHost, long sequence) =>
        DigestOf([coordinateId, claimedAt, ownerHost, sequence.ToString(CultureInfo.InvariantCulture)]);

    /// <summary>
    /// Ports lifecycle.rs:2872: "coordinate-lease" then coordinate_id,
    /// fencing_epoch, attempt_id.
    /// </summary>
    public static string CoordinateLeaseId(string coordinateId, long fencingEpoch, string attemptId) =>
        FullForm("coordinate-lease", coordinateId, fencingEpoch.ToString(CultureInfo.InvariantCulture), attemptId);

    /// <summary>
    /// Ports lifecycle.rs:2956. The PREFIX is "resource-lease", not the
    /// column name "resource_slot_lease_id" -- the one place in this family
    /// where the field name and the preimage prefix disagree.
    /// </summary>
    public static string ResourceSlotLeaseId(string resourceSlotId, long fencingEpoch, string attemptId) =>
        FullForm("resource-lease", resourceSlotId, fencingEpoch.ToString(CultureInfo.InvariantCulture), attemptId);

    /// <summary>
    /// Ports lifecycle.rs:2756: "coordinator-lease" then
    /// coordinator_authority_id, fencing_epoch, idempotency_key, now_ms.
    /// Minted once per lease; later claims under a live lease reuse the id,
    /// so only the minting operation's idempotency key reproduces it.
    /// </summary>
    public static string CoordinatorLeaseId(
        string coordinatorAuthorityId, long fencingEpoch, string idempotencyKey, long nowMs) =>
        FullForm(
            "coordinator-lease", coordinatorAuthorityId,
            fencingEpoch.ToString(CultureInfo.InvariantCulture), idempotencyKey,
            nowMs.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Ports lifecycle.rs:2035: "coordinator-authority" then matrix_id,
    /// execution_profile_revision.
    /// </summary>
    public static string CoordinatorAuthorityId(string matrixId, string executionProfileRevision) =>
        FullForm("coordinator-authority", matrixId, executionProfileRevision);

    /// <summary>
    /// Ports lifecycle.rs:6161: digest_of(attempt_id, run_hash, artifact_dir,
    /// stamp), where the stamp is stored verbatim as recorded_at.
    /// </summary>
    public static string ArtifactSnapshotId(
        string attemptId, string runHash, string artifactDir, string recordedAt) =>
        DigestOf([attemptId, runHash, artifactDir, recordedAt]);

    /// <summary>
    /// Ports the RUST snapshot-file reference id (lifecycle.rs:6041), written
    /// with owner_kind "artifact_snapshot": "artifact-reference" then
    /// snapshot_id, relative_path, sha256. The relative path is in the
    /// preimage but NOT in the reference row; it lives in
    /// artifact_snapshot_files.
    /// </summary>
    public static string SnapshotFileReferenceId(string snapshotId, string relativePath, string sha256) =>
        FullForm("artifact-reference", snapshotId, relativePath, sha256);

    /// <summary>
    /// Ports lifecycle.rs artifact location identity: "artifact-location"
    /// then blob digest, storage site, and the complete recorded path.
    /// </summary>
    public static string ArtifactLocationId(string sha256, string storageSiteId, string path) =>
        FullForm("artifact-location", sha256, storageSiteId, path);

    /// <summary>
    /// Ports the GO scheduler reference id (tools/fern-control/scheduler/
    /// stage_return.go:483), written for stage_consumer and stage_plan_export
    /// owners: "artifact-reference" then owner_kind, owner_id, sha256,
    /// purpose. Same prefix and table as the Rust form above, DIFFERENT
    /// component list; owner_kind decides which formula a row was minted
    /// under.
    /// </summary>
    public static string OwnerReferenceId(string ownerKind, string ownerId, string sha256, string purpose) =>
        FullForm("artifact-reference", ownerKind, ownerId, sha256, purpose);
}
