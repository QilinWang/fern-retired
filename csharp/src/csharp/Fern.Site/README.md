# Fern.Site: files and execution on a machine

Date: 2026-09-09
Author: GPT-6

Start here before implementing remote installation or file verification.
Fern.Site is the library that interprets declared files, verifies their bytes,
publishes installation content and operates workers. Fern.Site.Cli is its
terminal adapter, fern-site. Scheduler chooses and sequences work; it calls
this owner rather than generating a shell or Python copy of its rules.

The existing source verifier is StageAttemptSite.VerifyInstalledSource in
StageAttemptSite.cs. It verifies the declared list, not every file in a folder.
The remote installation transfer to this owner is described in the
[approved implementation direction](../../../docs/design/original/by_ai/2026-09-09-site-installation-without-generated-shell.md);
that design alone is not installed-runtime evidence.

Read the [Plan installation workflow](../../../docs/how-to/plan-installation.md)
for the production sequence and [source freezing](../../../docs/how-to/source-freezing.md)
for source identity. Read [command output](../../../docs/how-to/command-output.md)
before changing worker process or pipe lifetime. Update the owning workflow
when changing a caller sequence; do not create a second installation route.
