# Site boundaries

Read [this project's README](README.md), [the C# guide](../AGENTS.md) and
[the Scheduler boundary](../Fern.Scheduler/AGENTS.md) before changing how a
file reaches a machine. The root instructions still apply.

Fern.Site owns what "installed correctly" means: it reads the manifest,
verifies bytes, installs what is missing, refuses a content conflict, and
publishes atomically. That judgement has one implementation, and local
preparation, remote preparation and execution all call it. A second reading of
the same rule — in the Scheduler, in generated shell, in the SSH helper — is
the defect, not a shortcut, because a file rule then needs changing twice and
the remote half is invisible.

Verification takes data, never a generated program. A manifest entry is a
record with a path, a byte count and a digest; the number of files changes how
much data is sent, never the size or shape of the command that sends it. A
file list compiled into shell arguments fails on file count alone, and a path
containing a quote or a space stops being data.

Existing entry point: `StageAttemptSite.VerifyInstalledSource`. Find it before
writing verification anywhere else.
