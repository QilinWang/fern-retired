# Scheduler boundaries

Read [the C# guide](../README.md), [Fern.Site](../Fern.Site/README.md), and
[workflow routing](../../../docs/how-to/README.md) before changing a boundary.

Scheduler decides what runs, where, and in which order. Fern.Site owns remote
file verification, installation and execution. The SSH helper transports bytes;
it must not become a second interpreter of source manifests or Plan policy.
Find the existing Fern.Site operation before writing installation code here.

For installation, read [Plan installation](../../../docs/how-to/plan-installation.md),
[source freezing](../../../docs/how-to/source-freezing.md) and
[binary installation](../../../docs/how-to/binary-installation.md). For process/session
lifetime, read [command output](../../../docs/how-to/command-output.md). This project
also implements the ordered passes in
[dispatch queue](../../../docs/how-to/dispatch-queue.md),
[cell attempt recovery](../../../docs/how-to/cell-attempt-recovery.md),
[resident reconciliation cycle](../../../docs/how-to/resident-reconciliation-cycle.md),
[resident service obligation](../../../docs/how-to/resident-service-obligation.md) and
[verification call flow](../../../docs/how-to/verification-call-flow.md); each names a
sequence this code must keep, and a changed sequence is updated in the same patch.
State the workflow and real caller in the change, update its order in the same
patch, and pass the distributed boundary review before a live canary.

Commits must name the actual model and reasoning effort as required by
[commit provenance](../../../docs/rules/tooling.md#commit-provenance).
