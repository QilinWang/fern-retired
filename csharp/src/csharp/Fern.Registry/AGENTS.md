# Registry boundaries

Read [the C# guide](../README.md) and
[workflow routing](../../../docs/how-to/README.md) before changing a boundary.

Registry Home is the only authority that accepts a fact. A receipt is not a
cursor, and static verification or systemd state is never a reconciliation
outcome. The ordered passes this project implements are
[receipt shared rules](../../../docs/how-to/receipt-shared-rules.md),
[registry schema baseline](../../../docs/how-to/registry-schema-baseline.md),
[registry path selection](../../../docs/how-to/registry-path-selection.md) and
[analysis registration](../../../docs/how-to/analysis-registration.md).
Update the workflow's order in the same patch as a changed sequence.

Commits must name the actual model and reasoning effort as required by
[commit provenance](../../../docs/rules/tooling.md#commit-provenance).
