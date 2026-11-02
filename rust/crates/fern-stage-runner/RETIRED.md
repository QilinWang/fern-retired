# `fern-stage-runner` (Rust) — retired 2026-08-28

The unprivileged Stage return checker. It served two verbs: `probe`, which
printed the binary's own `fern.stage_runner_capability.v1` receipt, and
`check-return`, which reopened a Home-issued runner request together with the
report and output bytes a Stage left behind and wrote the scientific receipt.

## Who owns this now

`csharp/Fern.StageRunner.Cli` — the same two verbs, and the same executable
name, `fern-stage-runner`. The checking itself lives in
`csharp/Fern.Experiment/StageReturnChecker.cs`; the C# front end is only an
argument parser over it.

The two binaries are interchangeable to every caller that matters, because what
the installer verifies is the capability receipt, not the language. Compared on
the day of retirement, the receipts agree field for field:

    schema             fern.stage_runner_capability.v1
    protocol_version   fern.stage_runner.v1
    implementation_digest  sha256:… (this binary's own bytes; differs by build)
    handlers           ["fern.stage.return.check"]
    entrypoint         fern-stage-runner

`entrypoint` is the load-bearing one: it appears inside hashed identity records
(`fern_run::source_content::RUST_BINARY_ENTRYPOINT`,
`csharp/Fern.Registry/SourceContentIdentity.cs`,
`crates/fern-experiment-types/src/stage_plan.rs`), so the name had to survive
the move unchanged, and it did.

## The golden this port was proved against

`tests/protocol.rs`, preserved beside this file — four tests, ported by name to
`csharp/Fern.Experiment.Tests/StageReturnCheckerTests.cs`:

- `checker_accepts_python_postprocess_return`
- `checker_refuses_changed_output_bytes`
- `checker_refuses_runner_failure_report`
- `report_paths_must_stay_under_outputs`

## What changed at the caller, and what did not

The installer's `--stage-runner-binary` default moved from
`target/release/fern-stage-runner` to a binary it now builds itself from
`csharp/Fern.StageRunner.Cli`, alongside the scheduler and the Worker Site
(`csharp/Fern.Scheduler/InstallCli.cs`). An explicit `--stage-runner-binary`
still wins. Nothing else moved: every other reference to the string
`fern-stage-runner` in the repository names the entrypoint or the installed
binary-set member, and is satisfied by whichever binary carries that name.

No crate ever depended on this one. It was a workspace member and a leaf, so
retiring it removed two lines from `Cargo.toml` and nothing else.

`csharp/Fern.Scheduler/StagePackageBuilder.cs` still declares
`PackagedRustRunnerPath = "implementation/fern-stage-runner"`, a slot in the
Stage package for a runner binary. It is unreachable today:
`ReadyStageWorker.cs` refuses any Stage whose runner is not `PythonModule`, so
no Stage ships or executes one. That slot is a separate question from this
retirement and was left alone.
