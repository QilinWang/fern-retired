# C# agent instructions

Read [README.md](README.md), [workflow routing](../../docs/how-to/README.md), and the
[run/storage tutorial](../../docs/how-to/run-study-and-store-results.md) before
launching, admitting, deploying or collecting an experiment. Root instructions
and the distributed boundary review still apply.

- Formal Plan ownership stays in the selected Registry Home. Never create a
  worker-local Registry to work around a missing Home, rejected Binding or an
  unavailable command. A dedicated experiment Home needs the user's declared
  destination; “not the paper DB” is insufficient provenance by itself.
- Report configuration admission, Plan registration and accepted training
  progress separately. `--admit-only`, a process PID and GPU utilization prove
  different things. Use `fern-run monitor-study` for registered progress.
- Registration refusal requires fixing its real prerequisite. No automatic
  switch to direct Python Study, no forged implementation resolution, and no
  weakened source identity to get past admission.
- Python changes must be verified with the matching C# reader before launch.
  Backup/build/install is one sequence; a working-tree build is not proof that
  the installed binary was upgraded. Never publish a mismatched source label.
- Worker-local tensors and checkpoints stay with their producer. Run numerical
  summaries there; collect small declared outputs to Home. Do not fetch large
  arrays just because an older collection example does so.
- Write the host and purpose beside every proposed storage path. Mark a design
  example as an example; never infer a new database Home from a worker's output
  folder. Reuse the selected, authorized storage configuration without another
  confirmation; a change of host or database purpose is the relevant decision.
- Source manifests include required untracked modules and declared data.
  A missing module after freeze requires a new complete freeze, not a silent
  edit to bytes already named by a source digest.

Owner: [run/storage tutorial](../../docs/how-to/run-study-and-store-results.md).

Before editing remote installation, read [Fern.Site](Fern.Site/README.md) and
[Plan installation](../../docs/how-to/plan-installation.md). Scheduler selects
and sequences work; Fern.Site owns interpreting file declarations, verification,
publication and remote execution. SSH transports bytes. Do not generate shell
implementations of Site rules in Scheduler or add a parallel Python installer.
Name the owning workflow and its production caller before editing, and update
that workflow with the changed sequence. A directory listing is not this read.

Every AI-assisted commit follows [commit provenance](../../docs/rules/tooling.md#commit-provenance):
record the actual model and reasoning effort; use unknown when unavailable.
